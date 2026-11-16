using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using MimeKit;
using MimeKit.Text;
using MsgReader.Outlook;

namespace QuickPreview;

internal sealed record DocumentPreviewResult(string Title, string Text, string Summary, bool IsTruncated,
    bool IsMonospaced = false);

/// <summary>Local content previews. HTML is reduced to text; links and attachments are never opened.</summary>
internal static class DocumentPreviewService
{
    private const int MaxCharacters = 500_000;
    private const int MaxSmallFileBytes = 8 * 1024 * 1024;
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".eml", ".msg", ".epub", ".rtf", ".ics", ".ifb", ".vcf", ".vcard" };
    private static readonly HashSet<string> SuppressedHtmlTags = new(StringComparer.OrdinalIgnoreCase)
        { "script", "style", "head", "iframe", "object", "noscript", "template" };
    private static readonly HashSet<string> BlockHtmlTags = new(StringComparer.OrdinalIgnoreCase)
        { "p", "div", "section", "article", "header", "footer", "h1", "h2", "h3", "h4", "h5", "h6", "li", "ul", "ol", "tr", "blockquote", "pre", "hr", "br" };

    public static bool Supports(string path) => Extensions.Contains(Path.GetExtension(path));

    public static Task<DocumentPreviewResult> LoadAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() => Load(path, cancellationToken), cancellationToken);

    private static DocumentPreviewResult Load(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (!Supports(path)) throw new NotSupportedException("Этот формат документа не поддерживается.");
        var length = new FileInfo(path).Length;
        var limit = extension == ".epub" ? 256L * 1024 * 1024 : extension is ".eml" or ".msg" ? 64L * 1024 * 1024 : MaxSmallFileBytes;
        if (length > limit) throw new NotSupportedException("Документ слишком большой для быстрого предпросмотра.");
        var output = new ContentBuilder(token);
        string summary;
        switch (extension)
        {
            case ".eml": summary = ReadEml(path, output, token); break;
            case ".msg": summary = ReadMsg(path, output, token); break;
            case ".epub": summary = ReadEpub(path, output, token); break;
            case ".rtf":
                var rtf = ReadText(path, MaxSmallFileBytes);
                if (!rtf.TrimStart().StartsWith("{\\rtf", StringComparison.Ordinal))
                    throw new InvalidDataException("Файл не содержит документ RTF.");
                token.ThrowIfCancellationRequested();
                output.Append(HtmlToText(RtfPipe.Rtf.ToHtml(rtf), token));
                summary = "RTF · текст документа";
                break;
            case ".vcf": case ".vcard": summary = ReadCards(path, output, calendar: false); break;
            default: summary = ReadCards(path, output, calendar: true); break;
        }
        token.ThrowIfCancellationRequested();
        var text = output.ToString().Trim();
        if (text.Length == 0) text = "В документе нет доступного текста.";
        if (output.Truncated) text += "\n\n… Показана часть содержимого. Откройте файл для просмотра целиком.";
        return new DocumentPreviewResult(Path.GetFileName(path), text, summary, output.Truncated);
    }

    private static string ReadEml(string path, ContentBuilder output, CancellationToken token)
    {
        var options = ParserOptions.Default.Clone();
        options.MaxMimeDepth = 32;
        options.MaxAddressGroupDepth = 8;
        using var stream = OpenRead(path);
        using var message = MimeMessage.Load(options, stream, cancellationToken: token);
        output.Field("Тема", message.Subject);
        output.Field("От", message.From.ToString());
        output.Field("Кому", message.To.ToString());
        output.Field("Копия", message.Cc.ToString());
        if (message.Date != DateTimeOffset.MinValue)
            output.Field("Дата", message.Date.ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture));
        output.Line();
        var text = message.TextBody;
        if (!string.IsNullOrEmpty(text)) output.Append(text);
        else if (!string.IsNullOrEmpty(message.HtmlBody)) output.Append(HtmlToText(message.HtmlBody, token));
        else output.Line("В письме нет текстового содержимого.");
        output.Line();
        var attachments = message.Attachments.Take(201).ToList();
        if (attachments.Count > 0)
        {
            output.Line("Вложения:");
            foreach (var attachment in attachments.Take(200))
            {
                token.ThrowIfCancellationRequested();
                var name = attachment.ContentDisposition?.FileName ?? attachment.ContentType.Name;
                if (string.IsNullOrWhiteSpace(name)) name = attachment is MessagePart ? "Вложенное письмо.eml" : "Без имени";
                output.Line($"  • {OneLine(name)}  ({attachment.ContentType.MimeType})");
            }
            if (attachments.Count > 200) output.Truncated = true;
        }
        return $"Письмо EML · вложений: {(attachments.Count > 200 ? "200+" : attachments.Count.ToString(CultureInfo.InvariantCulture))}";
    }

    private static string ReadMsg(string path, ContentBuilder output, CancellationToken token)
    {
        using var stream = OpenRead(path);
        using var message = new Storage.Message(stream, FileAccess.Read, leaveStreamOpen: true);
        token.ThrowIfCancellationRequested();
        output.Field("Тема", message.Subject);
        var sender = message.Sender;
        if (sender is not null) output.Field("От", $"{sender.DisplayName} <{sender.Email}>");
        output.Field("Кому", message.GetEmailRecipients(RecipientType.To, false, false));
        output.Field("Копия", message.GetEmailRecipients(RecipientType.Cc, false, false));
        if (message.SentOn is { } sent)
            output.Field("Дата", sent.ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture));
        output.Line();
        var text = message.BodyText;
        if (!string.IsNullOrEmpty(text)) output.Append(text);
        else if (!string.IsNullOrEmpty(message.BodyHtml)) output.Append(HtmlToText(message.BodyHtml, token));
        else output.Line("В письме нет текстового содержимого.");
        output.Line();
        var attachments = message.Attachments;
        if (attachments.Count > 0)
        {
            output.Line("Вложения:");
            foreach (var attachment in attachments.Take(200))
            {
                token.ThrowIfCancellationRequested();
                var name = attachment switch
                {
                    Storage.Attachment file => file.FileName,
                    Storage.Message email => email.Subject + ".msg",
                    _ => "Без имени"
                };
                output.Line("  • " + OneLine(name));
            }
            if (attachments.Count > 200) output.Truncated = true;
        }
        return $"Письмо MSG · вложений: {attachments.Count}";
    }

    private static string ReadEpub(string path, ContentBuilder output, CancellationToken token)
    {
        using var stream = OpenRead(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        if (zip.Entries.Count > 20_000) throw new NotSupportedException("В книге слишком много частей для быстрого предпросмотра.");
        long remaining = 32L * 1024 * 1024;
        XDocument ReadXmlPart(string part)
        {
            token.ThrowIfCancellationRequested();
            var entry = zip.GetEntry(part) ?? throw new InvalidDataException("В EPUB отсутствует часть: " + part);
            if (entry.Length > MaxSmallFileBytes || entry.Length > remaining)
                throw new NotSupportedException("Содержимое книги слишком большое для быстрого предпросмотра.");
            remaining -= entry.Length;
            using var entryStream = entry.Open();
            return SafeXmlDocument.Load(entryStream, MaxSmallFileBytes, token);
        }
        var container = ReadXmlPart("META-INF/container.xml");
        var packagePath = container.Descendants().FirstOrDefault(e => e.Name.LocalName == "rootfile")?
            .Attribute("full-path")?.Value ?? throw new InvalidDataException("В EPUB отсутствует описание книги.");
        var package = ReadXmlPart(packagePath);
        string Metadata(string name) => package.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value ?? string.Empty;
        output.Field("Название", Metadata("title"));
        output.Field("Автор", Metadata("creator"));
        output.Field("Язык", Metadata("language"));
        output.Line();
        var manifest = package.Descendants().Where(e => e.Name.LocalName == "manifest").Elements()
            .Where(e => e.Name.LocalName == "item" && e.Attribute("id") is not null)
            .GroupBy(e => e.Attribute("id")!.Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var spine = package.Descendants().Where(e => e.Name.LocalName == "spine").Elements()
            .Where(e => e.Name.LocalName == "itemref" && (string?)e.Attribute("linear") != "no").ToList();
        var chapterCount = 0;
        foreach (var item in spine.Take(150))
        {
            token.ThrowIfCancellationRequested();
            if (!manifest.TryGetValue((string?)item.Attribute("idref") ?? string.Empty, out var resource)) continue;
            var type = (string?)resource.Attribute("media-type") ?? string.Empty;
            if (type is not ("application/xhtml+xml" or "text/html")) continue;
            var partPath = ResolvePackagePath(packagePath, (string?)resource.Attribute("href") ?? string.Empty);
            if (partPath is null) continue;
            var entry = zip.GetEntry(partPath);
            if (entry is null) continue;
            if (entry.Length > MaxSmallFileBytes || entry.Length > remaining) { output.Truncated = true; break; }
            remaining -= entry.Length;
            using var entryStream = entry.Open();
            using var reader = new StreamReader(entryStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var html = reader.ReadToEnd();
            token.ThrowIfCancellationRequested();
            chapterCount++;
            output.Line($"Глава {chapterCount}");
            output.Line(new string('─', 32));
            output.Line(HtmlToText(html, token));
            output.Line();
            if (output.Full) break;
        }
        if (spine.Count > 150) output.Truncated = true;
        if (chapterCount == 0)
            throw new NotSupportedException("В книге нет доступных текстовых глав. EPUB может быть защищён DRM или состоять из изображений.");
        return $"EPUB · глав в предпросмотре: {chapterCount}";
    }

    internal static string HtmlToText(string html, CancellationToken token)
    {
        using var reader = new StringReader(html);
        var tokenizer = new HtmlTokenizer(reader) { DecodeCharacterReferences = true };
        var text = new StringBuilder();
        var suppressed = new Stack<string>();
        var preformatted = false;
        while (tokenizer.ReadNextToken(out var current))
        {
            token.ThrowIfCancellationRequested();
            if (current is HtmlTagToken tag)
            {
                var name = tag.Name;
                if (suppressed.Count > 0)
                {
                    if (tag.IsEndTag && name.Equals(suppressed.Peek(), StringComparison.OrdinalIgnoreCase)) suppressed.Pop();
                    else if (!tag.IsEndTag && !tag.IsEmptyElement && SuppressedHtmlTags.Contains(name)) suppressed.Push(name);
                    continue;
                }
                if (!tag.IsEndTag && SuppressedHtmlTags.Contains(name))
                {
                    if (!tag.IsEmptyElement) suppressed.Push(name);
                    continue;
                }
                if (BlockHtmlTags.Contains(name)) NewLine(text);
                if (!tag.IsEndTag && name.Equals("li", StringComparison.OrdinalIgnoreCase)) text.Append("• ");
                if (name is "td" or "th" && tag.IsEndTag) text.Append('\t');
                if (name.Equals("pre", StringComparison.OrdinalIgnoreCase)) preformatted = !tag.IsEndTag;
            }
            else if (suppressed.Count == 0 && current is HtmlDataToken data)
            {
                foreach (var c in data.Data)
                {
                    if (text.Length > MaxCharacters) return text.ToString();
                    if (preformatted || !char.IsWhiteSpace(c)) text.Append(c == '\u00a0' ? ' ' : c);
                    else if (text.Length > 0 && !char.IsWhiteSpace(text[^1])) text.Append(' ');
                }
            }
            if (text.Length > MaxCharacters) break;
        }
        return text.ToString().Trim();
    }

    private static void NewLine(StringBuilder text)
    {
        while (text.Length > 0 && text[^1] is ' ' or '\t') text.Length--;
        if (text.Length > 0 && text[^1] != '\n') text.AppendLine();
    }

    private static string ReadCards(string path, ContentBuilder output, bool calendar)
    {
        var raw = ReadText(path, MaxSmallFileBytes).Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = raw.Split('\n');
        var unfolded = new List<string>();
        var current = new StringBuilder();
        var quotedPrintable = false;
        foreach (var line in lines)
        {
            output.CheckCancellation();
            if (current.Length > 0 && (line.StartsWith(' ') || line.StartsWith('\t')))
                current.Append(line.AsSpan(1));
            else if (current.Length > 0 && current[^1] == '=' && quotedPrintable)
            { current.Length--; current.Append(line); }
            else
            {
                if (current.Length > 0) unfolded.Add(current.ToString());
                current.Clear(); current.Append(line);
                var separator = PropertySeparator(line);
                quotedPrintable = separator >= 0 && line[..separator].Contains("ENCODING=QUOTED-PRINTABLE", StringComparison.OrdinalIgnoreCase);
            }
            if (unfolded.Count >= 100_000) { output.Truncated = true; break; }
        }
        if (current.Length > 0) unfolded.Add(current.ToString());
        var records = 0;
        var depth = 0;
        var shown = 0;
        foreach (var line in unfolded)
        {
            output.CheckCancellation();
            var separator = PropertySeparator(line);
            if (separator < 0) continue;
            var property = line[..separator];
            var value = line[(separator + 1)..];
            var parameters = property.Split(';');
            var name = parameters[0].Split('.').Last().ToUpperInvariant();
            if (name == "BEGIN")
            {
                if (value is "VEVENT" or "VTODO" or "VJOURNAL" or "VFREEBUSY" or "VCARD")
                {
                    records++;
                    if (records > 200) { output.Truncated = true; break; }
                    output.Line(calendar ? $"Событие {records}" : $"Контакт {records}");
                    output.Line(new string('─', 32));
                    depth = 1;
                }
                else if (depth > 0) depth++;
                continue;
            }
            if (name == "END")
            {
                if (depth > 0 && --depth == 0) output.Line();
                continue;
            }
            if (depth != 1) continue;
            var label = calendar ? CalendarLabel(name) : ContactLabel(name);
            if (label is null) continue;
            if (parameters.Any(p => p.Equals("ENCODING=QUOTED-PRINTABLE", StringComparison.OrdinalIgnoreCase)))
            {
                var charset = parameters.FirstOrDefault(p => p.StartsWith("CHARSET=", StringComparison.OrdinalIgnoreCase))?[8..].Trim('"') ?? "utf-8";
                value = DecodeQuotedPrintable(value, charset);
            }
            else if (parameters.Any(p => p.Equals("ENCODING=B", StringComparison.OrdinalIgnoreCase) ||
                                         p.Equals("ENCODING=BASE64", StringComparison.OrdinalIgnoreCase)))
            {
                try { value = Encoding.UTF8.GetString(Convert.FromBase64String(value)); }
                catch (FormatException) { continue; }
            }
            if (calendar && name is "DTSTART" or "DTEND" or "DUE" or "COMPLETED") value = CalendarDate(value, parameters);
            else value = UnescapeCardValue(value, structured: name is "N" or "ADR" or "ORG");
            output.Field(label, value);
            if (++shown > 15_000 || output.Full) { output.Truncated = true; break; }
        }
        if (records == 0) throw new InvalidDataException(calendar ? "В календаре нет доступных событий." : "В файле нет доступных контактов.");
        return calendar ? $"Календарь · записей: {records}" : $"Контакты · записей: {records}";
    }

    private static string? CalendarLabel(string name) => name switch
    {
        "SUMMARY" => "Название", "DTSTART" => "Начало", "DTEND" => "Окончание", "DUE" => "Срок",
        "DESCRIPTION" => "Описание", "LOCATION" => "Место", "ORGANIZER" => "Организатор", "ATTENDEE" => "Участник",
        "STATUS" => "Статус", "RRULE" => "Повторение", "URL" => "Ссылка", "CATEGORIES" => "Категории",
        "TRANSP" => "Занятость", "PRIORITY" => "Приоритет", "PERCENT-COMPLETE" => "Выполнено, %", _ => null
    };
    private static string? ContactLabel(string name) => name switch
    {
        "FN" => "Имя", "N" => "ФИО", "NICKNAME" => "Псевдоним", "ORG" => "Организация", "TITLE" => "Должность",
        "ROLE" => "Роль", "EMAIL" => "Почта", "TEL" => "Телефон", "ADR" => "Адрес", "URL" => "Сайт",
        "BDAY" => "День рождения", "NOTE" => "Заметка", "CATEGORIES" => "Категории", _ => null
    };
    private static int PropertySeparator(string line)
    {
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') quoted = !quoted;
            else if (line[i] == ':' && !quoted) return i;
        }
        return -1;
    }
    private static string CalendarDate(string value, string[] parameters)
    {
        if (DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " (весь день)";
        var utc = value.EndsWith('Z');
        if (DateTime.TryParseExact(value.TrimEnd('Z'), new[] { "yyyyMMdd'T'HHmmss", "yyyyMMdd'T'HHmm" },
                CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        {
            var timezone = utc ? "UTC" : parameters.FirstOrDefault(p => p.StartsWith("TZID=", StringComparison.OrdinalIgnoreCase))?[5..].Trim('"');
            return date.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + (string.IsNullOrEmpty(timezone) ? "" : " " + timezone);
        }
        return value;
    }
    private static string UnescapeCardValue(string value, bool structured)
    {
        var text = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '\\' && i + 1 < value.Length)
            {
                c = value[++i];
                text.Append(c is 'n' or 'N' ? '\n' : c);
            }
            else if (c == ';' && structured) text.Append(", ");
            else text.Append(c);
        }
        return text.ToString();
    }
    private static string DecodeQuotedPrintable(string value, string charset)
    {
        using var bytes = new MemoryStream();
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '=' && i + 2 < value.Length && byte.TryParse(value.AsSpan(i + 1, 2),
                    NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
            { bytes.WriteByte(b); i += 2; }
            else bytes.WriteByte((byte)value[i]);
        }
        try { return Encoding.GetEncoding(charset).GetString(bytes.ToArray()); }
        catch (ArgumentException) { return Encoding.UTF8.GetString(bytes.ToArray()); }
    }
    private static string? ResolvePackagePath(string owner, string target)
    {
        if (target.Length == 0 || target.Contains('\\')) return null;
        if (!Uri.TryCreate(new Uri("https://package.invalid/" + owner), target, out var uri) ||
            uri.Scheme != "https" || uri.Host != "package.invalid" || uri.Query.Length > 0) return null;
        return Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
    }
    private static FileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    private static string ReadText(string path, int limit)
    {
        using var stream = OpenRead(path);
        if (stream.Length > limit) throw new NotSupportedException("Документ слишком большой для быстрого предпросмотра.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
    private static string OneLine(string? value) => (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ');

    private sealed class ContentBuilder
    {
        private readonly StringBuilder _text = new();
        private readonly CancellationToken _token;
        public ContentBuilder(CancellationToken token) => _token = token;
        public bool Truncated { get; set; }
        public bool Full => _text.Length >= MaxCharacters;
        public void CheckCancellation() => _token.ThrowIfCancellationRequested();
        public void Append(string? value)
        {
            CheckCancellation();
            if (value is null) return;
            var length = Math.Min(value.Length, MaxCharacters - _text.Length);
            _text.Append(value.AsSpan(0, length));
            if (length < value.Length) Truncated = true;
        }
        public void Line(string value = "") { Append(value); Append(Environment.NewLine); }
        public void Field(string label, string? value) { if (!string.IsNullOrWhiteSpace(value)) Line(label + ": " + value); }
        public override string ToString() => _text.ToString();
    }
}
