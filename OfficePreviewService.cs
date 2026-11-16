using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Xml.Linq;

namespace QuickPreview;

internal sealed record OfficePreviewResult(string Title, string Text, string Summary, bool IsTruncated);

/// <summary>Reads document content locally without launching Office, evaluating formulas, or loading links.</summary>
internal static class OfficePreviewService
{
    private const int MaxCharacters = 500_000;
    private const int MaxSlides = 100;
    private const long MaxPartBytes = 8 * 1024 * 1024;
    private static readonly HashSet<string> WordExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".docx", ".docm", ".dotx", ".dotm" };
    private static readonly HashSet<string> PowerPointExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".pptx", ".pptm", ".ppsx", ".ppsm", ".potx", ".potm" };
    private static readonly HashSet<string> OdfExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".odt", ".ott", ".odp", ".otp" };

    public static bool Supports(string path)
    {
        var extension = Path.GetExtension(path);
        return WordExtensions.Contains(extension) ||
               PowerPointExtensions.Contains(extension) || OdfExtensions.Contains(extension) || LegacyOfficePreviewService.Supports(path);
    }

    public static Task<OfficePreviewResult> LoadAsync(string path, CancellationToken cancellationToken = default) =>
        LegacyOfficePreviewService.Supports(path) ? LegacyOfficePreviewService.LoadAsync(path, cancellationToken) :
            Task.Run(() => Load(path, cancellationToken), cancellationToken);

    private static OfficePreviewResult Load(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Supports(path))
            throw new NotSupportedException("Этот формат Office не поддерживает встроенный просмотр содержимого.");
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (file.Length > 256L * 1024 * 1024)
            throw new NotSupportedException("Документ слишком большой для быстрого предпросмотра (более 256 МБ).");
        Span<byte> signature = stackalloc byte[8];
        var read = file.Read(signature);
        file.Position = 0;
        if (read >= 4 && signature[0] == 0xD0 && signature[1] == 0xCF && signature[2] == 0x11 && signature[3] == 0xE0)
            throw new NotSupportedException("Документ защищён паролем. Откройте его в Office для ввода пароля.");
        using var package = new Package(file, token);
        var output = new PreviewBuilder(token);
        var extension = Path.GetExtension(path);
        string summary;
        if (WordExtensions.Contains(extension))
        {
            var main = package.MainPart("word/document.xml");
            var document = package.ReadXml(main);
            var paragraphs = document.Descendants().Where(e => e.Name.LocalName == "p");
            foreach (var paragraph in paragraphs)
            {
                token.ThrowIfCancellationRequested();
                var text = InlineText(paragraph, odf: false);
                if (paragraph.Descendants().Any(e => e.Name.LocalName == "numPr"))
                    text = "• " + text;
                output.Line(text);
                if (output.Full) break;
            }
            summary = "Word · текст документа";
        }
        else if (PowerPointExtensions.Contains(extension))
            summary = ReadPresentation(package, output);
        else
            summary = ReadOpenDocument(package, output);

        var textResult = output.ToString();
        if (string.IsNullOrWhiteSpace(textResult))
            textResult = "В документе нет доступного текста. Изображения, диаграммы и сложная вёрстка отображаются в приложении Office.";
        if (output.Truncated)
            textResult += "\n\n… Показана часть содержимого. Откройте файл для просмотра целиком.";
        return new OfficePreviewResult(Path.GetFileName(path), textResult, summary, output.Truncated);
    }

    private static string ReadPresentation(Package package, PreviewBuilder output)
    {
        var main = package.MainPart("ppt/presentation.xml");
        var presentation = package.ReadXml(main);
        var relationships = package.Relationships(main);
        var slides = presentation.Descendants().Where(e => e.Name.LocalName == "sldId").ToList();
        var index = 0;
        foreach (var slide in slides.Take(MaxSlides))
        {
            output.CheckCancellation();
            index++;
            if (!relationships.TryGetValue(RelationshipId(slide), out var part)) continue;
            output.Line($"Слайд {index}");
            output.Line(new string('─', 32));
            var slideXml = package.ReadXml(part.Path);
            foreach (var paragraph in slideXml.Descendants().Where(e => e.Name.LocalName == "p" &&
                         e.Name.NamespaceName.Contains("drawingml", StringComparison.Ordinal)))
            {
                output.Line(InlineText(paragraph, odf: false));
                if (output.Full) break;
            }
            output.Line();
            if (output.Full) break;
        }
        if (slides.Count > MaxSlides) output.Truncated = true;
        return $"PowerPoint · слайдов: {slides.Count} · текст слайдов";
    }

    private static string ReadOpenDocument(Package package, PreviewBuilder output)
    {
        if (package.Contains("META-INF/manifest.xml") && package.ReadXml("META-INF/manifest.xml").Descendants()
            .Any(e => e.Name.LocalName == "encryption-data"))
            throw new NotSupportedException("Документ защищён паролем. Откройте его в офисном приложении для ввода пароля.");
        var document = package.ReadXml("content.xml");
        var body = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "body") ??
                   throw new InvalidDataException("В документе отсутствует основное содержимое.");
        var pages = body.Descendants().Where(e => e.Name.LocalName == "page" &&
            e.Name.NamespaceName.Contains("drawing", StringComparison.Ordinal)).ToList();
        if (pages.Count > 0)
        {
            var index = 0;
            foreach (var page in pages.Take(MaxSlides))
            {
                output.Line($"Слайд {++index}");
                foreach (var paragraph in page.Descendants().Where(IsOdfParagraph))
                {
                    output.Line(InlineText(paragraph, odf: true));
                    if (output.Full) break;
                }
                output.Line();
                if (output.Full) break;
            }
            if (pages.Count > MaxSlides) output.Truncated = true;
            return $"OpenDocument · слайдов: {pages.Count} · текст слайдов";
        }
        foreach (var paragraph in body.Descendants().Where(IsOdfParagraph))
        {
            output.Line(InlineText(paragraph, odf: true));
            if (output.Full) break;
        }
        return "OpenDocument · текст документа";
    }

    private static bool IsOdfParagraph(XElement element) => element.Name.LocalName is "p" or "h" &&
        element.Name.NamespaceName.Contains("text", StringComparison.Ordinal);

    private static int RepeatCount(XElement element, string attribute) =>
        int.TryParse(Attribute(element, attribute), NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            ? Math.Max(1, count) : 1;

    private static string InlineText(XElement paragraph, bool odf)
    {
        var result = new StringBuilder();
        foreach (var node in paragraph.DescendantNodes())
        {
            if (odf && node is XText text) result.Append(text.Value);
            else if (node is XElement element)
            {
                switch (element.Name.LocalName)
                {
                    case "t" when !odf: result.Append(element.Value); break;
                    case "tab": result.Append('\t'); break;
                    case "br": case "cr": case "line-break": result.AppendLine(); break;
                    case "s" when odf: result.Append(' ', Math.Min(100, RepeatCount(element, "c"))); break;
                }
            }
            if (result.Length >= MaxCharacters) break;
        }
        return result.ToString();
    }

    private static string Attribute(XElement element, string name) =>
        element.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value ?? string.Empty;

    private static string RelationshipId(XElement element) => element.Attributes().FirstOrDefault(a =>
        a.Name.LocalName == "id" && a.Name.NamespaceName.EndsWith("/relationships", StringComparison.Ordinal))?.Value ?? string.Empty;

    private sealed record Relationship(string Path, string Type);

    private sealed class Package : IDisposable
    {
        private readonly ZipArchive _archive;
        private readonly CancellationToken _token;
        private long _remainingBytes = 32 * 1024 * 1024;
        public Package(Stream stream, CancellationToken token)
        {
            _token = token;
            _archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            if (_archive.Entries.Count > 20_000)
            {
                _archive.Dispose();
                throw new NotSupportedException("Документ содержит слишком много частей для быстрого предпросмотра.");
            }
        }
        public bool Contains(string path) => _archive.GetEntry(path) is not null;
        public XDocument ReadXml(string path)
        {
            _token.ThrowIfCancellationRequested();
            var entry = _archive.GetEntry(path) ?? throw new InvalidDataException("В документе отсутствует часть: " + path);
            if (entry.Length > MaxPartBytes || entry.Length > _remainingBytes)
                throw new NotSupportedException("Содержимое документа слишком большое для быстрого предпросмотра.");
            _remainingBytes -= entry.Length;
            using var stream = entry.Open();
            return SafeXmlDocument.Load(stream, MaxPartBytes, _token);
        }
        public string MainPart(string fallback)
        {
            if (!Contains("_rels/.rels")) return fallback;
            return Relationships(string.Empty).Values.FirstOrDefault(r =>
                r.Type.EndsWith("/officeDocument", StringComparison.Ordinal))?.Path ?? fallback;
        }
        public Dictionary<string, Relationship> Relationships(string owner)
        {
            var slash = owner.LastIndexOf('/');
            var folder = slash >= 0 ? owner[..(slash + 1)] : string.Empty;
            var file = slash >= 0 ? owner[(slash + 1)..] : owner;
            var path = owner.Length == 0 ? "_rels/.rels" : folder + "_rels/" + file + ".rels";
            var result = new Dictionary<string, Relationship>(StringComparer.Ordinal);
            if (!Contains(path)) return result;
            foreach (var relationship in ReadXml(path).Descendants().Where(e => e.Name.LocalName == "Relationship"))
            {
                _token.ThrowIfCancellationRequested();
                if (Attribute(relationship, "TargetMode").Equals("External", StringComparison.OrdinalIgnoreCase)) continue;
                var target = Attribute(relationship, "Target");
                if (target.Length == 0 || target.Contains('\\')) continue;
                var baseUri = new Uri("https://package.invalid/" + owner);
                if (!Uri.TryCreate(baseUri, target, out var resolved) || resolved.Host != "package.invalid" ||
                    resolved.Scheme != "https" || resolved.Query.Length > 0 || resolved.Fragment.Length > 0) continue;
                var partPath = Uri.UnescapeDataString(resolved.AbsolutePath.TrimStart('/'));
                result[Attribute(relationship, "Id")] = new Relationship(partPath, Attribute(relationship, "Type"));
            }
            return result;
        }
        public void Dispose() => _archive.Dispose();
    }

    private sealed class PreviewBuilder
    {
        private readonly StringBuilder _text = new();
        private readonly CancellationToken _token;
        public PreviewBuilder(CancellationToken token) => _token = token;
        public bool Truncated { get; set; }
        public bool Full => _text.Length >= MaxCharacters;
        public void CheckCancellation() => _token.ThrowIfCancellationRequested();
        public void Line(string text = "")
        {
            CheckCancellation();
            var remaining = MaxCharacters - _text.Length;
            if (text.Length + Environment.NewLine.Length > remaining)
            {
                _text.Append(text.AsSpan(0, Math.Min(text.Length, remaining)));
                Truncated = true;
                return;
            }
            _text.AppendLine(text);
        }
        public override string ToString() => _text.ToString().TrimEnd();
    }
}
