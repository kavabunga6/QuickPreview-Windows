using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using QuickPreview;

var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../artifacts/fixtures"));
Directory.CreateDirectory(directory);
var checks = 0;
var clock = Stopwatch.StartNew();

var deep = string.Concat(Enumerable.Repeat("<span>", 40_000)) + "original regression value" + string.Concat(Enumerable.Repeat("</span>", 40_000));
var ods = Package("deep.ods", new()
{
    ["content.xml"] = "<office:document-content xmlns:office='urn:oasis:names:tc:opendocument:xmlns:office:1.0' xmlns:table='urn:oasis:names:tc:opendocument:xmlns:table:1.0' xmlns:text='urn:oasis:names:tc:opendocument:xmlns:text:1.0'><office:body><office:spreadsheet><table:table table:name='Original'><table:table-row><table:table-cell><text:p>" + deep + "</text:p></table:table-cell></table:table-row></table:table></office:spreadsheet></office:body></office:document-content>"
});
await Rejected(() => SpreadsheetPreviewService.LoadAsync(ods), "Deep ODS is rejected before XElement.Value can overflow the process stack");
var xlsx = Package("deep.xlsx", new()
{
    ["xl/workbook.xml"] = "<workbook xmlns:r='http://schemas.openxmlformats.org/officeDocument/2006/relationships'><sheets><sheet name='Original' r:id='one'/></sheets></workbook>",
    ["xl/_rels/workbook.xml.rels"] = "<Relationships><Relationship Id='one' Target='worksheets/sheet1.xml' Type='urn/worksheet'/></Relationships>",
    ["xl/worksheets/sheet1.xml"] = "<worksheet><sheetData><row r='1'><c r='A1' t='inlineStr'><is><t>" + deep + "</t></is></c></row></sheetData></worksheet>"
});
await Rejected(() => SpreadsheetPreviewService.LoadAsync(xlsx), "Deep XLSX is rejected before inline-string extraction");
var docx = Package("deep.docx", new()
{
    ["word/document.xml"] = "<document><body><p><r><t>" + deep + "</t></r></p></body></document>"
});
await Rejected(() => OfficePreviewService.LoadAsync(docx), "Deep Word XML is rejected before recursive text values");
var epub = Package("deep.epub", new()
{
    ["META-INF/container.xml"] = "<container>" + deep + "</container>"
});
await Rejected(() => DocumentPreviewService.LoadAsync(epub), "Deep EPUB package XML is rejected");
var svg = Path.Combine(directory, "deep.svg");
File.WriteAllText(svg, "<svg xmlns='http://www.w3.org/2000/svg'>" + deep + "</svg>");
await Rejected(() => SvgPreviewService.LoadAsync(svg), "Deep SVG is rejected before the renderer traverses it");

using (var xml = new StringReader("<?xml version='1.0' encoding='utf-16'?><?demo keep?><root><!--original note-->  text\n  <child /> </root>"))
{
    var document = SafeXmlDocument.Load(xml, 4096, default, LoadOptions.PreserveWhitespace, preserveMarkup: true);
    Check(document.Root!.Nodes().OfType<XText>().Any(text => text.Value.Contains("  text\n  ")), "Decoded UTF-16 XML declaration and whitespace remain readable");
    Check(document.Root.Nodes().OfType<XComment>().Any() && document.Nodes().OfType<XProcessingInstruction>().Any(), "Plain-XML formatting retains comments and processing instructions as text");
}
using (var xml = new StringReader("<root>" + deep + "</root>"))
{
    try { SafeXmlDocument.Load(xml, 2 * 1024 * 1024, default); throw new Exception("Depth guard did not reject XML."); }
    catch (XmlException) { Check(true, "Plain XML reports a catchable XmlException for raw-text fallback"); }
}
using (var xml = new StringReader("<!DOCTYPE root [<!ENTITY x SYSTEM 'file:///not-read'>]><root>&x;</root>"))
{
    try { SafeXmlDocument.Load(xml, 4096, default); throw new Exception("DTD was allowed."); }
    catch (XmlException) { Check(true, "DTD/external entities remain disabled"); }
}
await RejectedCancellation(() => SpreadsheetPreviewService.LoadAsync(xlsx, new CancellationToken(true)));

Equal(SpreadsheetPreviewService.FormatNumber(12500, "0.0,\" тыс.\""), "12,5 тыс.", "Thousands scaling precedes literal units");
Equal(SpreadsheetPreviewService.FormatNumber(1200000, "0.0,,\" mln\""), "1,2 mln", "Millions scaling preserves the displayed magnitude");
Equal(SpreadsheetPreviewService.FormatNumber(0, "0;-0;\"—\""), "—", "Zero section is selected");
Equal(SpreadsheetPreviewService.FormatNumber(45000, "mmmm"), "март", "Month-only dates use the calendar rather than raw serial");
Equal(SpreadsheetPreviewService.FormatNumber(125d / 86400, "mm:ss"), "02:05", "Minute/second format does not invent an hour component");
Equal(SpreadsheetPreviewService.FormatNumber(1.5, "[h]:mm:ss"), "36:00:00", "Elapsed hours do not wrap at 24");
Equal(SpreadsheetPreviewService.FormatNumber(-0.125, "0.00%;(0.00%)"), "(12,50%)", "Negative percentages retain section punctuation");
Equal(SpreadsheetPreviewService.FormatNumber(123, "00000"), "00123", "Identifier leading zeroes are retained");
Equal(SpreadsheetPreviewService.FormatNumber(0, "0.00"), "0,00", "A cached zero remains a formatted numeric value");
Equal(SpreadsheetPreviewService.FormatNumber(0, "dd.MM.yyyy", true), "01.01.1904", "1904 workbook date epoch is retained");
Equal(SpreadsheetPreviewService.FormatNumber(10.5, "\"USD \"0.00"), "USD 10,50", "Currency prefix retains its position");
Equal(SpreadsheetPreviewService.FormatNumber(7, "[broken"), "7", "Invalid formats fall back to an unlabelled raw value");
Check(!OfficePreviewService.Supports("test.xlsx") && !OfficePreviewService.Supports("test.ods") && !LegacyOfficePreviewService.Supports("test.xls"), "Spreadsheet routing has no duplicate Office text reader");
Console.WriteLine($"PASS: {checks} reader regressions, {clock.Elapsed.TotalSeconds:F1}s; no native windows created.");

void Equal(string actual, string expected, string title)
{
    if (actual != expected) throw new Exception($"FAILED: {title}: expected '{expected}', got '{actual}'.");
    Check(true, title);
}
void Check(bool condition, string title)
{
    if (!condition) throw new Exception("FAILED: " + title);
    checks++; Console.WriteLine("PASS: " + title);
}
async Task Rejected(Func<Task> load, string title)
{
    try { await load(); }
    catch (XmlException) { Check(true, title); return; }
    throw new Exception("FAILED: " + title);
}
async Task RejectedCancellation(Func<Task> load)
{
    try { await load(); }
    catch (OperationCanceledException) { Check(true, "Already-canceled XML load remains cancellable"); return; }
    throw new Exception("Cancellation was ignored.");
}
string Package(string name, Dictionary<string, string> parts)
{
    var path = Path.Combine(directory, name);
    using var file = File.Create(path);
    using var zip = new ZipArchive(file, ZipArchiveMode.Create);
    foreach (var part in parts)
    {
        using var writer = new StreamWriter(zip.CreateEntry(part.Key).Open(), new UTF8Encoding(false));
        writer.Write(part.Value);
    }
    return path;
}
