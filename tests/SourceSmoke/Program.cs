using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using QuickPreview;

var directory = Path.Combine(Path.GetTempPath(), "QuickPreview.SourceSmoke." + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
var checks = 0;
try
{
    var models = ".step .stp .iges .igs .brep .brp .fcstd .stl .obj .3mf .ply .off .amf .3ds .dae .fbx .gltf .glb .wrl .3dm .ifc .bim".Split(' ');
    Check(models.All(extension => SourcePreviewService.IsCandidate("модель" + extension)), "All visual model extensions are candidates");
    Check(new[] { ".dxf", ".dwg", ".svg", ".svgz", ".csv", ".tsv" }.All(extension => SourcePreviewService.IsCandidate("file" + extension)), "Other structured visual formats are candidates");
    Check(new[] { ".txt", ".pdf", ".docx", ".xlsx", ".png", ".jpg", ".eml", ".epub" }.All(extension => !SourcePreviewService.IsCandidate("file" + extension)), "Ordinary text, Office, images and email excluded");
    var stepText = "ISO-10303-21;\r\nHEADER;\r\nFILE_DESCRIPTION(('Деталь корпуса'),'2;1');\r\nENDSEC;\r\nDATA;\r\n#1=CARTESIAN_POINT('Начало',(0.,0.,0.));\r\nENDSEC;\r\nEND-ISO-10303-21;\r\n";
    var step = Write("Деталь корпуса.STEP", Encoding.GetEncoding(1251).GetBytes(stepText));
    var hash = Hash(step);
    Check(await SourcePreviewService.CanShowAsync(step), "Cyrillic uppercase STEP with Windows-1251 text accepted");
    var loaded = await SourcePreviewService.LoadAsync(step);
    Check(loaded.Text == stepText && !loaded.IsTruncated, "STEP raw whitespace, line endings and Cyrillic preserved");
    Check(Hash(step) == hash, "STEP source hash unchanged");
    foreach (var (name, text) in new[]
    {
        ("ascii.stl", "solid sample\nfacet normal 0 0 1\nouter loop\nvertex 0 0 0\nvertex 1 0 0\nvertex 0 1 0\nendloop\nendfacet\nendsolid sample\n"),
        ("ascii.ply", "ply\nformat ascii 1.0\ncomment Модель\nelement vertex 0\nend_header\n"),
        ("ascii.fbx", "; FBX 7.4.0 project\nObjects: {}\n"),
        ("drawing.dxf", "0\r\nSECTION\r\n2\r\nENTITIES\r\n0\r\nENDSEC\r\n0\r\nEOF\r\n"),
        ("model.gltf", "{  \"asset\":{\"version\":\"2.0\"},\n\"scenes\": [ ] }\n"),
        ("vector.svg", "<?xml version=\"1.0\"?><svg xmlns=\"http://www.w3.org/2000/svg\">  <rect width=\"2\"/> </svg>\n")
    })
    {
        var path = Write(name, Encoding.UTF8.GetBytes(text));
        var before = Hash(path);
        Check(await SourcePreviewService.CanShowAsync(path) && (await SourcePreviewService.LoadAsync(path)).Text == text, name + " raw source available and unchanged");
        Check(Hash(path) == before, name + " file remains read-only");
    }
    var binaryStl = Enumerable.Repeat((byte)'X', 134).ToArray();
    Encoding.ASCII.GetBytes("solid ambiguous binary").CopyTo(binaryStl, 0);
    BitConverter.GetBytes(1u).CopyTo(binaryStl, 80);
    var binaryCases = new Dictionary<string, byte[]>
    {
        ["binary.stl"] = binaryStl,
        ["binary.ply"] = Encoding.ASCII.GetBytes("ply\nformat binary_little_endian 1.0\nelement vertex 1\nend_header\nABCD"),
        ["binary.fbx"] = Encoding.ASCII.GetBytes("Kaydara FBX Binary  \0\x1a\0"),
        ["binary.dxf"] = Encoding.ASCII.GetBytes("AutoCAD Binary DXF\r\n\x1a\0"),
        ["binary.glb"] = Encoding.ASCII.GetBytes("glTFtext-is-still-a-binary-format"),
        ["binary.3dm"] = Encoding.ASCII.GetBytes("3D Geometry File Format"),
        ["binary.dwg"] = Encoding.ASCII.GetBytes("AC1032"),
        ["binary.3ds"] = Encoding.ASCII.GetBytes("anything"),
        ["binary.obj"] = Encoding.UTF8.GetBytes("v 0 0 0\n\0BINARY"),
        ["controls.step"] = Encoding.UTF8.GetBytes("ISO-10303-21;\n\x01unreadable")
    };
    foreach (var binary in binaryCases)
    {
        var path = Write(binary.Key, binary.Value);
        Check(!await SourcePreviewService.CanShowAsync(path), binary.Key + " hides source tab");
        await Expect<NotSupportedException>(() => SourcePreviewService.LoadAsync(path), binary.Key + " cannot load binary as text");
    }
    var csvText = "Имя;Описание\r\nКорпус;Сталь\r\n";
    var csv = Write("таблица.CSV", Encoding.GetEncoding(1251).GetBytes(csvText));
    Check(await SourcePreviewService.CanShowAsync(csv) && (await SourcePreviewService.LoadAsync(csv)).Text == csvText, "CSV Windows-1251 source decoded without table transformation");
    foreach (var encoding in new Encoding[] { new UTF8Encoding(true), new UnicodeEncoding(false, true), new UnicodeEncoding(true, true), new UTF32Encoding(false, true), new UTF32Encoding(true, true) })
    {
        var text = "Имя\tDescription\r\nДеталь\tКириллица\r\n";
        var path = Write("unicode-" + encoding.CodePage + ".tsv", encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray());
        Check(await SourcePreviewService.CanShowAsync(path) && (await SourcePreviewService.LoadAsync(path)).Text == text, encoding.WebName + " source encoding recognized");
    }
    var xml = "<?xml version=\"1.0\"?>\n<svg xmlns=\"http://www.w3.org/2000/svg\">\n  <text>Сжатый SVG</text>\n</svg>\n";
    var svgz = Path.Combine(directory, "drawing.SVGZ");
    using (var file = File.Create(svgz)) using (var gzip = new GZipStream(file, CompressionMode.Compress)) gzip.Write(Encoding.UTF8.GetBytes(xml));
    Check(await SourcePreviewService.CanShowAsync(svgz) && (await SourcePreviewService.LoadAsync(svgz)).Text == xml, "SVGZ source is bounded decompressed XML");
    var modelXml = "<model unit=\"millimeter\">\n  <resources/>\n</model>\n";
    var threeMf = Zip("model.3mf", "3D/3dmodel.model", modelXml);
    var threeHash = Hash(threeMf);
    var source3mf = await SourcePreviewService.LoadAsync(threeMf);
    Check(await SourcePreviewService.CanShowAsync(threeMf) && source3mf.Text == modelXml && source3mf.Summary.Contains("внутри 3MF"), "3MF embedded model XML available and labeled");
    Check(Hash(threeMf) == threeHash && !Directory.Exists(Path.Combine(directory, "3D")), "3MF source unchanged and no members extracted");
    var fcstd = Zip("model.FCStd", "Document.xml", "<Document>  Исходник  </Document>\n");
    Check(await SourcePreviewService.CanShowAsync(fcstd) && (await SourcePreviewService.LoadAsync(fcstd)).Text.Contains("  Исходник  "), "FCStd document XML preserved");
    Check(!await SourcePreviewService.CanShowAsync(Zip("missing.3mf", "other.xml", "<other/>")), "3MF missing model part hides source");
    Check(!await SourcePreviewService.CanShowAsync(Write("broken.svgz", Encoding.ASCII.GetBytes("not gzip"))), "Malformed SVGZ hides source without throwing");
    var large = Write("large.obj", Encoding.ASCII.GetBytes(new string(' ', 2 * 1024 * 1024 + 500) + "tail"));
    var capped = await SourcePreviewService.LoadAsync(large);
    Check(capped.IsTruncated && capped.Text.Length == 2 * 1024 * 1024 && capped.Summary.Contains("2 МБ"), "Plain source capped at 2 MiB without appended content");
    var largeSvgz = Path.Combine(directory, "large.svgz");
    using (var file = File.Create(largeSvgz)) using (var gzip = new GZipStream(file, CompressionMode.Compress)) gzip.Write(Encoding.UTF8.GetBytes("<svg>" + new string('x', 3 * 1024 * 1024) + "</svg>"));
    Check((await SourcePreviewService.LoadAsync(largeSvgz)).Text.Length == 2 * 1024 * 1024 && (await SourcePreviewService.LoadAsync(largeSvgz)).IsTruncated, "Compressed source limit applies after decompression");
    var lateBinary = Write("late-binary.obj", Encoding.ASCII.GetBytes(new string('v', 5000) + "\0payload"));
    Check(await SourcePreviewService.CanShowAsync(lateBinary), "Sniff intentionally examines first 4 KiB only");
    await Expect<NotSupportedException>(() => SourcePreviewService.LoadAsync(lateBinary), "Load rejects binary characters beyond sniff sample");
    await Expect<OperationCanceledException>(() => SourcePreviewService.CanShowAsync(step, new CancellationToken(true)), "Sniff cancellation");
    await Expect<OperationCanceledException>(() => SourcePreviewService.LoadAsync(step, new CancellationToken(true)), "Load cancellation");
    Check(!await SourcePreviewService.CanShowAsync(Path.Combine(directory, "missing.obj")), "Missing source sniff returns false");
    using (var writer = new FileStream(csv, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        Check((await SourcePreviewService.LoadAsync(csv)).Text == csvText, "Source readable while another writer holds file open");
    Console.WriteLine($"Passed {checks} source preview smoke checks.");
}
catch (Exception exception) { Console.Error.WriteLine(exception); Environment.ExitCode = 1; }
finally { Directory.Delete(directory, true); }

void Check(bool condition, string label) { if (!condition) throw new Exception("FAILED: " + label); checks++; Console.WriteLine("PASS: " + label); }
async Task Expect<T>(Func<Task> action, string label) where T : Exception
{
    try { await action(); } catch (T) { Check(true, label); return; }
    throw new Exception("FAILED: " + label);
}
string Write(string name, byte[] data) { var path = Path.Combine(directory, name); File.WriteAllBytes(path, data); return path; }
string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
string Zip(string name, string entryName, string text)
{
    var path = Path.Combine(directory, name);
    using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
    using var writer = new StreamWriter(zip.CreateEntry(entryName).Open(), new UTF8Encoding(false)); writer.Write(text);
    return path;
}
