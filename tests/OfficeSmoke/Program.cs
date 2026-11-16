using System.IO.Compression;
using System.Xml;
using QuickPreview;

var root = Path.Combine(Path.GetTempPath(), "QuickPreview.OfficeSmoke." + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var count = 0;
try
{
    var word = Write("content.docx", new Dictionary<string, string>
    {
        ["_rels/.rels"] = Rel("rMain", "word/custom.xml", "officeDocument"),
        ["word/custom.xml"] = "<w:document xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main'><w:body><w:p><w:r><w:t>Привет </w:t></w:r><w:hyperlink><w:r><w:t>мир</w:t><w:tab/><w:t>&lt;script&gt;</w:t></w:r></w:hyperlink></w:p><w:tbl><w:tr><w:tc><w:p><w:r><w:t>Ячейка</w:t></w:r></w:p></w:tc></w:tr></w:tbl></w:body></w:document>"
    });
    var wordResult = await OfficePreviewService.LoadAsync(word);
    Check(wordResult.Text.Contains("Привет мир\t<script>") && wordResult.Text.Contains("Ячейка"), "Word text, run boundaries, table and markup-as-text");
    Check(!wordResult.IsTruncated, "Word result flags");

    var slides = Write("content.pptx", new Dictionary<string, string>
    {
        ["ppt/presentation.xml"] = "<p:presentation xmlns:p='p' xmlns:r='http://schemas.openxmlformats.org/officeDocument/2006/relationships'><p:sldIdLst><p:sldId id='257' r:id='second'/><p:sldId id='256' r:id='first'/></p:sldIdLst></p:presentation>",
        ["ppt/_rels/presentation.xml.rels"] = "<Relationships><Relationship Id='first' Target='slides/slide1.xml' Type='urn/slide'/><Relationship Id='second' Target='slides/slide2.xml' Type='urn/slide'/></Relationships>",
        ["ppt/slides/slide1.xml"] = Slide("First"),
        ["ppt/slides/slide2.xml"] = Slide("Second")
    });
    var slideResult = await OfficePreviewService.LoadAsync(slides);
    Check(slideResult.Text.IndexOf("Second", StringComparison.Ordinal) < slideResult.Text.IndexOf("First", StringComparison.Ordinal), "Slides use presentation order and relationship id rather than numeric slide id");

    var odt = Write("content.odt", new Dictionary<string, string>
    {
        ["content.xml"] = "<office:document-content xmlns:office='urn:oasis:names:tc:opendocument:xmlns:office:1.0' xmlns:text='urn:oasis:names:tc:opendocument:xmlns:text:1.0'><office:body><office:text><text:h>Заголовок</text:h><text:p>Два<text:s text:c='2'/>пробела<text:line-break/>Строка</text:p></office:text></office:body></office:document-content>"
    });
    Check((await OfficePreviewService.LoadAsync(odt)).Text.Contains("Два  пробела"), "OpenDocument headings, spaces and paragraphs");

    var large = Write("large.docx", new Dictionary<string, string>
    {
        ["word/document.xml"] = "<document><body><p><r><t>" + new string('a', 600_000) + "</t></r></p></body></document>"
    });
    var limited = await OfficePreviewService.LoadAsync(large);
    Check(limited.IsTruncated && limited.Text.Length < 501_000, "Document text output limit");

    var protectedFile = Path.Combine(root, "protected.docx");
    File.WriteAllBytes(protectedFile, new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 });
    await Expect<NotSupportedException>(() => OfficePreviewService.LoadAsync(protectedFile), "Encrypted OOXML explains password requirement");

    var malicious = Write("malicious.docx", new Dictionary<string, string>
    {
        ["word/document.xml"] = "<!DOCTYPE document [<!ENTITY ext SYSTEM 'file:///C:/Windows/win.ini'>]><document><p><t>&ext;</t></p></document>"
    });
    await Expect<XmlException>(() => OfficePreviewService.LoadAsync(malicious), "DTD and external entities rejected");
    await Expect<TaskCanceledException>(() => OfficePreviewService.LoadAsync(word, new CancellationToken(true)), "Cancellation honored");
    Check(!OfficePreviewService.Supports("file.xlsb") && !OfficePreviewService.Supports("file.xlsx") && !OfficePreviewService.Supports("file.ods") && !OfficePreviewService.Supports("file.xls") && OfficePreviewService.Supports("file.doc") && OfficePreviewService.Supports("FILE.DOCM"), "Office routing delegates all spreadsheets to their dedicated reader");
    foreach (var extension in new[] { "doc", "ppt" })
    {
        var legacyPath = Path.Combine(AppContext.BaseDirectory, "fixtures", "legacy." + extension);
        Check(File.Exists(legacyPath), "Legacy ." + extension + " fixture included");
        await Expect<OperationCanceledException>(() => OfficePreviewService.LoadAsync(legacyPath, new CancellationToken(true)), "Legacy ." + extension + " honors cancellation before taking the worker gate");
        var legacy = await OfficePreviewService.LoadAsync(legacyPath);
        Check(legacy.Text.Length > 0 && legacy.Summary.Contains("системный обработчик"), "Windows IFilter reads real legacy ." + extension);
    }
    Console.WriteLine($"Passed {count} Office preview smoke checks.");
}
finally
{
    Directory.Delete(root, true);
}

void Check(bool condition, string title)
{
    if (!condition) throw new Exception("FAILED: " + title);
    count++;
    Console.WriteLine("PASS: " + title);
}
async Task Expect<T>(Func<Task<OfficePreviewResult>> action, string title) where T : Exception
{
    try { await action(); }
    catch (T) { Check(true, title); return; }
    throw new Exception("FAILED: " + title);
}
string Write(string name, Dictionary<string, string> parts)
{
    var path = Path.Combine(root, name);
    using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
    foreach (var part in parts)
    {
        using var writer = new StreamWriter(archive.CreateEntry(part.Key).Open());
        writer.Write(part.Value);
    }
    return path;
}
string Rel(string id, string target, string type) => $"<Relationships><Relationship Id='{id}' Target='{target}' Type='urn/{type}'/></Relationships>";
string Slide(string text) => $"<p:sld xmlns:p='urn:p' xmlns:a='http://schemas.openxmlformats.org/drawingml/2006/main'><a:p><a:r><a:t>{text}</a:t></a:r></a:p></p:sld>";
