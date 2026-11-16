using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml;
using OpenMcdf;
using QuickPreview;

var root = Path.Combine(Path.GetTempPath(), "QuickPreview.DocumentSmoke." + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var count = 0;
try
{
    var plain = Write("plain.eml", "From: =?utf-8?B?0JDQvdC90LA=?= <anna@example.test>\r\nTo: reader@example.test\r\nSubject: =?utf-8?B?0J/RgNC40LLQtdGC?=\r\nDate: Mon, 16 Nov 2026 10:00:00 +0300\r\nMIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary=parts\r\n\r\n--parts\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: quoted-printable\r\n\r\n=D0=9F=D1=80=D0=B8=D0=B2=D0=B5=D1=82!\r\n--parts\r\nContent-Type: application/octet-stream; name=report.bin\r\nContent-Disposition: attachment; filename=report.bin\r\nContent-Transfer-Encoding: base64\r\n\r\nSGVsbG8=\r\n--parts--\r\n");
    var mail = await DocumentPreviewService.LoadAsync(plain);
    Check(mail.Text.Contains("Тема: Привет") && mail.Text.Contains("Анна") && mail.Text.Contains("Привет!"), "EML encoded headers and quoted printable body decoded");
    Check(mail.Text.Contains("report.bin") && mail.Summary.Contains("1") && !File.Exists(Path.Combine(root,"report.bin")), "Email lists attachments without extracting them");
    var htmlMail = Write("html.eml", "From: a@example.test\r\nSubject: HTML\r\nMIME-Version: 1.0\r\nContent-Type: text/html; charset=utf-8\r\n\r\n<html><head><style>.x{display:none}</style><script>hiddenHead()</script></head><body><h1>Title &amp; text</h1><p>First<br>Second</p><img src='https://example.test/pixel'><script>hiddenBody()</script><ul><li>Item</li></ul></body></html>");
    var html = await DocumentPreviewService.LoadAsync(htmlMail);
    Check(html.Text.Contains("Title & text") && html.Text.Contains("First") && html.Text.Contains("Second") && html.Text.Contains("• Item"), "HTML email readable paragraphs and lists");
    Check(!html.Text.Contains("hiddenHead") && !html.Text.Contains("hiddenBody") && !html.Text.Contains("example.test/pixel"), "HTML scripts/styles and remote images omitted");
    var msgPath = Path.Combine(root, "generated.msg");
    using (var storage = RootStorage.Create(msgPath))
    {
        using (var props = storage.CreateStream("__properties_version1.0")) props.Write(new byte[32]);
        MsgText(storage, "001A", "IPM.Note"); MsgText(storage, "0037", "Служебное письмо");
        MsgText(storage, "1000", "Локальный текст MSG"); MsgText(storage, "0C1A", "Тестовый отправитель");
        MsgText(storage, "0C1F", "sender@example.test");
        var attachment = storage.CreateStorage("__attach_version1.0_#00000000");
        using (var attachmentProps = attachment.CreateStream("__properties_version1.0")) attachmentProps.Write(new byte[8]);
        MsgText(attachment, "3707", "document.txt"); MsgText(attachment, "3704", "document.txt");
        storage.Flush();
    }
    var msg = await DocumentPreviewService.LoadAsync(msgPath);
    Check(msg.Text.Contains("Служебное письмо") && msg.Text.Contains("Локальный текст MSG"), "MSG Unicode subject and body");
    Check(msg.Text.Contains("document.txt"), "MSG attachment names");
    var epub = Zip("book.epub", new Dictionary<string, string>
    {
        ["META-INF/container.xml"] = "<container><rootfiles><rootfile full-path='OEBPS/book.opf'/></rootfiles></container>",
        ["OEBPS/book.opf"] = "<package><metadata><title>Книга</title><creator>Автор</creator><language>ru</language></metadata><manifest><item id='one' href='one.xhtml' media-type='application/xhtml+xml'/><item id='two' href='two.xhtml' media-type='application/xhtml+xml'/><item id='remote' href='https://example.test/remote.html' media-type='text/html'/></manifest><spine><itemref idref='two'/><itemref idref='remote'/><itemref idref='one'/></spine></package>",
        ["OEBPS/one.xhtml"] = "<html><body><h1>Chapter One</h1><p>First text</p></body></html>",
        ["OEBPS/two.xhtml"] = "<html><body><h1>Chapter Two</h1><p>Second text</p><script>DO_NOT_SHOW()</script></body></html>"
    });
    var book = await DocumentPreviewService.LoadAsync(epub);
    Check(book.Text.Contains("Книга") && book.Text.Contains("Автор") && book.Text.IndexOf("Chapter Two", StringComparison.Ordinal) < book.Text.IndexOf("Chapter One", StringComparison.Ordinal), "EPUB metadata and spine order");
    Check(!book.Text.Contains("DO_NOT_SHOW") && book.Summary.Contains("2"), "EPUB scripts and remote spine ignored");
    var ics = Write("events.ics", "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VEVENT\r\nSUMMARY:Встреча\r\nDTSTART;TZID=Europe/Moscow:20261116T143000\r\nDTEND:20261116T123000Z\r\nDESCRIPTION:Длинное описание\\nВторая строка\\, и запятая\r\n продолжение\r\nBEGIN:VALARM\r\nDESCRIPTION:Не показывать будильник\r\nEND:VALARM\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nSUMMARY:Праздник\r\nDTSTART;VALUE=DATE:20261117\r\nEND:VEVENT\r\nEND:VCALENDAR");
    var calendar = await DocumentPreviewService.LoadAsync(ics);
    Check(calendar.Text.Contains("2026-11-16 14:30 Europe/Moscow") && calendar.Text.Contains("2026-11-16 12:30 UTC") && calendar.Text.Contains("2026-11-17 (весь день)"), "Calendar explicit timezones and all-day dates");
    Check(calendar.Text.Contains("Вторая строка, и запятаяпродолжение") && !calendar.Text.Contains("Не показывать"), "Calendar folded/escaped text and nested alarms");
    var vcf = Write("contacts.vcf", "BEGIN:VCARD\r\nVERSION:3.0\r\nFN:Иван Иванов\r\nN:Иванов;Иван;;;\r\nTEL;TYPE=CELL:+7 900 111 22 33\r\nEMAIL:ivan@example.test\r\nADR:;;Улица\\; дом;Москва;;;\r\nNOTE:Первая\\nВторая\r\nPHOTO;ENCODING=B:AA==\r\nEND:VCARD\r\nBEGIN:VCARD\r\nVERSION:2.1\r\nFN;CHARSET=WINDOWS-1251;ENCODING=QUOTED-PRINTABLE:=C0=ED=ED=E0\r\nEND:VCARD");
    var contacts = await DocumentPreviewService.LoadAsync(vcf);
    Check(contacts.Text.Contains("Иван Иванов") && contacts.Text.Contains("+7 900") && contacts.Text.Contains("Улица; дом") && contacts.Text.Contains("Анна"), "vCard escaped structured fields and QP charset");
    Check(!contacts.Text.Contains("AA==") && contacts.Summary.Contains("2"), "vCard photos skipped");
    var rtf = Write("document.rtf", "{\\rtf1\\ansi\\ansicpg1252{\\fonttbl{\\f0 Arial;}}\\f0 Hello \\b bold\\b0\\par Unicode \\u1040?\\u1041?\\par}");
    var rich = await DocumentPreviewService.LoadAsync(rtf);
    Check(rich.Text.Contains("Hello bold") && rich.Text.Contains("АБ") && !rich.Text.Contains("\\fonttbl"), "RTF paragraphs and Unicode decoded");
    await Expect<InvalidDataException>(() => DocumentPreviewService.LoadAsync(Write("invalid.rtf", "not RTF")), "Invalid RTF rejected");
    var limited = await DocumentPreviewService.LoadAsync(Write("long.eml", "Content-Type: text/plain; charset=utf-8\r\n\r\n" + new string('x', 600_000)));
    Check(limited.IsTruncated && limited.Text.Length < 501_000, "Document output bounded");
    await Expect<OperationCanceledException>(() => DocumentPreviewService.LoadAsync(plain, new CancellationToken(true)), "Document cancellation");
    var svg = Write("simple.svg", "<svg xmlns='http://www.w3.org/2000/svg' width='120' height='80'><rect width='120' height='80' fill='#ff0000'/><text x='5' y='20'>SVG</text></svg>");
    var bitmap = await SvgPreviewService.LoadAsync(svg);
    Check(bitmap.IsFrozen && bitmap.PixelWidth == 120 && bitmap.PixelHeight == 80, "SVG frozen bitmap natural bounds");
    var svgz = Path.Combine(root, "compressed.svgz");
    using (var target = File.Create(svgz)) using (var gzip = new GZipStream(target, CompressionMode.Compress)) gzip.Write(File.ReadAllBytes(svg));
    Check((await SvgPreviewService.LoadAsync(svgz)).PixelWidth == 120, "Compressed SVGZ");
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    try
    {
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var external = Write("external.svg", $"<svg xmlns='http://www.w3.org/2000/svg' xmlns:xlink='http://www.w3.org/1999/xlink'><style>@import url(http://127.0.0.1:{port}/style.css);</style><image width='1000' height='1000' xlink:href='http://127.0.0.1:{port}/image.png'/><use xlink:href='http://127.0.0.1:{port}/external.svg#id'/><rect width='20' height='10' fill='blue'/></svg>");
        var safe = await SvgPreviewService.LoadAsync(external);
        Check(!listener.Pending() && safe.PixelWidth == 20 && safe.PixelHeight == 10, "SVG external image/use/CSS blocked before rendering");
    }
    finally { listener.Stop(); }
    var dtd = Write("dtd.svg", "<!DOCTYPE svg [<!ENTITY ext SYSTEM 'file:///C:/Windows/win.ini'>]><svg xmlns='http://www.w3.org/2000/svg'><text>&ext;</text></svg>");
    await Expect<XmlException>(() => SvgPreviewService.LoadAsync(dtd), "SVG external entities rejected");
    var cycle = Write("cycle.svg", "<svg xmlns='http://www.w3.org/2000/svg'><g id='loop'><use href='#loop'/></g></svg>");
    await Expect<InvalidDataException>(() => SvgPreviewService.LoadAsync(cycle), "SVG cyclic references rejected");
    await Expect<OperationCanceledException>(() => SvgPreviewService.LoadAsync(svg, new CancellationToken(true)), "SVG cancellation");
    Check(DocumentPreviewService.Supports("MAIL.EML") && SvgPreviewService.Supports("image.SVGZ") && !DocumentPreviewService.Supports("data.bin"), "Extension routing");
    Console.WriteLine($"Passed {count} document and SVG smoke checks.");
}
catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
finally { Directory.Delete(root, true); }

void Check(bool condition, string title) { if (!condition) throw new Exception("FAILED: " + title); count++; Console.WriteLine("PASS: " + title); }
async Task Expect<T>(Func<Task> action, string title) where T : Exception
{
    try { await action(); } catch (T) { Check(true, title); return; }
    throw new Exception("FAILED: " + title);
}
string Write(string name, string content) { var path = Path.Combine(root, name); File.WriteAllText(path, content, new UTF8Encoding(false)); return path; }
string Zip(string name, Dictionary<string, string> parts)
{
    var path = Path.Combine(root, name); using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
    foreach (var part in parts) { using var writer = new StreamWriter(archive.CreateEntry(part.Key).Open()); writer.Write(part.Value); }
    return path;
}
void MsgText(Storage storage, string tag, string value)
{
    using var stream = storage.CreateStream("__substg1.0_" + tag + "001F"); stream.Write(Encoding.Unicode.GetBytes(value + "\0"));
}
