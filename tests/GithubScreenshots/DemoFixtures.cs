using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using FlowDirection = System.Windows.FlowDirection;

/// <summary>Original, deterministic demo documents. Never reads any user document or directory.</summary>
internal static class DemoFixtures
{
    private static readonly DateTime FixedTime = new(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private readonly record struct P(double X, double Y, double Z)
    {
        public static P operator -(P a, P b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static P Cross(P a, P b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        public P Unit() { var d = Math.Sqrt(X * X + Y * Y + Z * Z); return d == 0 ? new(0, 0, 1) : new(X / d, Y / d, Z / d); }
    }
    private readonly record struct Triangle(P A, P B, P C);

    public static IReadOnlyDictionary<string, string> Create(string directory)
    {
        Directory.CreateDirectory(directory);
        var paths = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["image"] = Path.Combine(directory, "Demo-landscape.png"),
            ["model"] = Path.Combine(directory, "Demo-rotor.stl"),
            ["step"] = Path.Combine(directory, "Demo-bracket.step"),
            ["obj"] = Path.Combine(directory, "Demo-rotor.obj"),
            ["threeMf"] = Path.Combine(directory, "Demo-rotor.3mf"),
            ["spreadsheet"] = Path.Combine(directory, "Demo-inventory.xlsx"),
            ["pdf"] = Path.Combine(directory, "Demo-design-guide.pdf"),
            ["archive"] = Path.Combine(directory, "Demo-workshop.zip"),
            ["json"] = Path.Combine(directory, "Demo-preview.json"),
            ["word"] = Path.Combine(directory, "Demo-project-brief.docx")
        };
        WriteImage(paths["image"]);
        var rotor = Rotor();
        WriteStl(paths["model"], rotor);
        WriteObj(paths["obj"], rotor);
        WriteThreeMf(paths["threeMf"], rotor);
        WriteStep(paths["step"]);
        WriteWorkbook(paths["spreadsheet"]);
        WritePdf(paths["pdf"]);
        WriteWord(paths["word"]);
        WriteText(paths["json"], @"{
  ""project"": ""QuickPreview demo workspace"",
  ""environment"": ""local-demo"",
  ""description"": ""Synthetic configuration for public screenshots"",
  ""preview"": {
    ""enabled"": true,
    ""shortcut"": ""Space"",
    ""followExplorerSelection"": true,
    ""formats"": [""STEP"", ""STL"", ""3MF"", ""OBJ"", ""PDF"", ""XLSX""],
    ""renderOnDemand"": true,
    ""preserveCameraOnTextSwitch"": true
  },
  ""assets"": {
    ""models"": ""./demo/models"",
    ""documents"": ""./demo/documents"",
    ""images"": ""./demo/images""
  },
  ""metadata"": {
    ""created"": ""2026-01-15"",
    ""testDataOnly"": true
  }
}
");
        WriteArchive(paths["archive"], paths);
        foreach (var path in paths.Values)
        {
            File.SetCreationTimeUtc(path, FixedTime);
            File.SetLastWriteTimeUtc(path, FixedTime);
        }
        return paths;
    }

    private static string N(double value) => value.ToString("0.######", Invariant);
    private static string E(string value) => SecurityElement.Escape(value) ?? "";
    private static void WriteText(string path, string text) => File.WriteAllText(path, text, new UTF8Encoding(false));
    private static void Quad(List<Triangle> triangles, P a, P b, P c, P d)
    { triangles.Add(new(a, b, c)); triangles.Add(new(a, c, d)); }

    private static List<Triangle> Rotor()
    {
        var triangles = new List<Triangle>();
        void Lathe((double R, double Z)[] profile)
        {
            const int segments = 192;
            P Vertex((double R, double Z) point, int segment)
            { var angle = segment * Math.Tau / segments; return new(point.R * Math.Cos(angle), point.R * Math.Sin(angle), point.Z); }
            for (var i = 0; i < profile.Length; i++)
            for (var segment = 0; segment < segments; segment++)
            {
                var next = (i + 1) % profile.Length;
                Quad(triangles, Vertex(profile[i], segment), Vertex(profile[i], segment + 1),
                    Vertex(profile[next], segment + 1), Vertex(profile[next], segment));
            }
        }
        Lathe(new[] { (16d, 1d), (17d, 0d), (49d, 0d), (50d, 1d), (50d, 4d), (49d, 5d), (17d, 5d), (16d, 4d) });
        Lathe(new[] { (6d, 1d), (7d, 0d), (15d, 0d), (16d, 1d), (16d, 19d), (15d, 21d), (7d, 21d), (6d, 20d) });
        for (var blade = 0; blade < 12; blade++)
        {
            P[] Section(int index)
            {
                var t = index / 32d;
                var radius = 16.5 + 31.5 * t;
                var angle = blade * Math.Tau / 12 + 0.65 * t;
                var halfWidth = 1.3 - 0.55 * t;
                var x = radius * Math.Cos(angle); var y = radius * Math.Sin(angle);
                var tx = -Math.Sin(angle) * halfWidth; var ty = Math.Cos(angle) * halfWidth;
                var top = 18 - 8 * t;
                return new[] { new P(x - tx, y - ty, 5), new P(x + tx, y + ty, 5), new P(x + tx, y + ty, top), new P(x - tx, y - ty, top) };
            }
            for (var section = 0; section < 32; section++)
            {
                var a = Section(section); var b = Section(section + 1);
                for (var side = 0; side < 4; side++) Quad(triangles, a[side], a[(side + 1) % 4], b[(side + 1) % 4], b[side]);
            }
            var first = Section(0); var last = Section(32);
            Quad(triangles, first[3], first[2], first[1], first[0]);
            Quad(triangles, last[0], last[1], last[2], last[3]);
        }
        return triangles;
    }

    private static void WriteStl(string path, IReadOnlyList<Triangle> triangles)
    {
        using var writer = new BinaryWriter(File.Create(path));
        var header = new byte[80];
        Encoding.ASCII.GetBytes("QuickPreview original demo rotor - synthetic test geometry").CopyTo(header, 0);
        writer.Write(header); writer.Write((uint)triangles.Count);
        foreach (var triangle in triangles)
        {
            var normal = P.Cross(triangle.B - triangle.A, triangle.C - triangle.A).Unit();
            foreach (var p in new[] { normal, triangle.A, triangle.B, triangle.C })
            { writer.Write((float)p.X); writer.Write((float)p.Y); writer.Write((float)p.Z); }
            writer.Write((ushort)0);
        }
    }

    private static void WriteObj(string path, IReadOnlyList<Triangle> triangles)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        writer.WriteLine("# Original QuickPreview demo rotor. Synthetic geometry; units: millimetres.\no DemoRotor");
        foreach (var triangle in triangles)
            foreach (var p in new[] { triangle.A, triangle.B, triangle.C }) writer.WriteLine($"v {N(p.X)} {N(p.Y)} {N(p.Z)}");
        for (var index = 0; index < triangles.Count; index++) writer.WriteLine($"f {index * 3 + 1} {index * 3 + 2} {index * 3 + 3}");
    }

    private static void WriteThreeMf(string path, IReadOnlyList<Triangle> triangles)
    {
        using var archive = NewZip(path);
        Add(archive, "[Content_Types].xml", "<Types xmlns='http://schemas.openxmlformats.org/package/2006/content-types'><Default Extension='rels' ContentType='application/vnd.openxmlformats-package.relationships+xml'/><Default Extension='model' ContentType='application/vnd.ms-package.3dmanufacturing-3dmodel+xml'/></Types>");
        Add(archive, "_rels/.rels", "<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='model' Target='/3D/3dmodel.model' Type='http://schemas.microsoft.com/3dmanufacturing/2013/01/3dmodel'/></Relationships>");
        var model = new StringBuilder("<model unit='millimeter' xmlns='http://schemas.microsoft.com/3dmanufacturing/core/2015/02'><metadata name='Title'>Original demo rotor</metadata><resources><object id='1' type='model'><mesh><vertices>");
        foreach (var triangle in triangles) foreach (var p in new[] { triangle.A, triangle.B, triangle.C })
            model.Append($"<vertex x='{N(p.X)}' y='{N(p.Y)}' z='{N(p.Z)}'/>");
        model.Append("</vertices><triangles>");
        for (var i = 0; i < triangles.Count; i++) model.Append($"<triangle v1='{i * 3}' v2='{i * 3 + 1}' v3='{i * 3 + 2}'/>");
        model.Append("</triangles></mesh></object></resources><build><item objectid='1' transform='1 0 0 0 1 0 0 0 1 125 -65 30'/></build></model>");
        Add(archive, "3D/3dmodel.model", model.ToString());
    }

    private static void WriteStep(string path)
    {
        var profile = new[] { (0d, 0d), (80d, 0d), (80d, 12d), (18d, 12d), (18d, 68d), (0d, 68d) };
        var points = profile.Select(p => new P(p.Item1, -15, p.Item2)).Concat(profile.Select(p => new P(p.Item1, 15, p.Item2))).ToArray();
        var faces = new List<int[]> { Enumerable.Range(0, 6).ToArray(), Enumerable.Range(6, 6).Reverse().ToArray() };
        for (var i = 0; i < 6; i++) faces.Add(new[] { i, i + 6, (i + 1) % 6 + 6, (i + 1) % 6 });
        string Real(double value) { var text = N(value); return text.Contains('.') ? text : text + "."; }
        string Vector(P p) => $"{Real(p.X)},{Real(p.Y)},{Real(p.Z)}";
        var step = new StringBuilder("ISO-10303-21;\nHEADER;\nFILE_DESCRIPTION(('Original synthetic L-bracket for QuickPreview screenshots'),'2;1');\nFILE_NAME('Demo-bracket.step','2026-01-15T12:00:00',('Demo Workshop'),(''),'QuickPreview demo generator','QuickPreview','');\nFILE_SCHEMA(('CONFIG_CONTROL_DESIGN'));\nENDSEC;\nDATA;\n");
        step.AppendLine("#1=APPLICATION_CONTEXT('configuration controlled 3d designs of mechanical parts and assemblies');\n#2=APPLICATION_PROTOCOL_DEFINITION('international standard','config_control_design',1994,#1);\n#3=PRODUCT_CONTEXT('',#1,'mechanical');\n#4=PRODUCT('Demo bracket','Demo bracket','',(#3));\n#5=PRODUCT_DEFINITION_FORMATION('','',#4);\n#6=PRODUCT_DEFINITION_CONTEXT('part definition',#1,'design');\n#7=PRODUCT_DEFINITION('design','',#5,#6);\n#8=PRODUCT_DEFINITION_SHAPE('','',#7);\n#9=SHAPE_DEFINITION_REPRESENTATION(#8,#10);\n#10=ADVANCED_BREP_SHAPE_REPRESENTATION('',(#11),#12);\n#11=MANIFOLD_SOLID_BREP('Demo bracket',#20);\n#12=(GEOMETRIC_REPRESENTATION_CONTEXT(3)GLOBAL_UNCERTAINTY_ASSIGNED_CONTEXT((#15))GLOBAL_UNIT_ASSIGNED_CONTEXT((#13,#14,#16))REPRESENTATION_CONTEXT('',''));\n#13=(LENGTH_UNIT()NAMED_UNIT(*)SI_UNIT(.MILLI.,.METRE.));\n#14=(NAMED_UNIT(*)PLANE_ANGLE_UNIT()SI_UNIT($,.RADIAN.));\n#15=UNCERTAINTY_MEASURE_WITH_UNIT(LENGTH_MEASURE(1.E-6),#13,'distance_accuracy_value','');\n#16=(NAMED_UNIT(*)SI_UNIT($,.STERADIAN.)SOLID_ANGLE_UNIT());");
        step.AppendLine("#20=CLOSED_SHELL('',(" + string.Join(',', faces.Select((_, i) => "#" + (30 + i * 3))) + "));");
        for (var i = 0; i < points.Length; i++) step.AppendLine($"#{100 + i}=CARTESIAN_POINT('',({Vector(points[i])}));");
        for (var i = 0; i < faces.Count; i++)
        {
            var id = 30 + i * 3; var plane = 300 + i * 4; var face = faces[i];
            var reference = (points[face[1]] - points[face[0]]).Unit();
            var normal = P.Cross(points[face[1]] - points[face[0]], points[face[2]] - points[face[0]]).Unit();
            step.AppendLine($"#{id}=ADVANCED_FACE('',(#{id + 1}),#{plane},.T.);\n#{id + 1}=FACE_OUTER_BOUND('',#{id + 2},.T.);\n#{id + 2}=POLY_LOOP('',(" + string.Join(',', face.Select(v => "#" + (100 + v))) + "));");
            step.AppendLine($"#{plane}=PLANE('',#{plane + 1});\n#{plane + 1}=AXIS2_PLACEMENT_3D('',#{100 + face[0]},#{plane + 2},#{plane + 3});\n#{plane + 2}=DIRECTION('',({Vector(normal)}));\n#{plane + 3}=DIRECTION('',({Vector(reference)}));");
        }
        step.AppendLine("ENDSEC;\nEND-ISO-10303-21;");
        WriteText(path, step.ToString());
    }

    private static ZipArchive NewZip(string path) => new(File.Create(path), ZipArchiveMode.Create);
    private static void Add(ZipArchive archive, string name, string text) => Add(archive, name, Encoding.UTF8.GetBytes(text));
    private static void Add(ZipArchive archive, string name, byte[] bytes)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal); entry.LastWriteTime = new DateTimeOffset(FixedTime);
        using var output = entry.Open(); output.Write(bytes);
    }

    private static void WriteWorkbook(string path)
    {
        using var archive = NewZip(path);
        Add(archive, "[Content_Types].xml", "<Types xmlns='http://schemas.openxmlformats.org/package/2006/content-types'><Default Extension='rels' ContentType='application/vnd.openxmlformats-package.relationships+xml'/><Default Extension='xml' ContentType='application/xml'/><Override PartName='/xl/workbook.xml' ContentType='application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml'/><Override PartName='/xl/worksheets/sheet1.xml' ContentType='application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml'/><Override PartName='/xl/worksheets/sheet2.xml' ContentType='application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml'/><Override PartName='/xl/styles.xml' ContentType='application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml'/></Types>");
        Add(archive, "_rels/.rels", "<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='main' Target='xl/workbook.xml' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument'/></Relationships>");
        Add(archive, "xl/workbook.xml", "<workbook xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main' xmlns:r='http://schemas.openxmlformats.org/officeDocument/2006/relationships'><bookViews><workbookView activeTab='0'/></bookViews><sheets><sheet name='Demo inventory' sheetId='1' r:id='one'/><sheet name='Notes' sheetId='2' r:id='two'/></sheets></workbook>");
        Add(archive, "xl/_rels/workbook.xml.rels", "<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='one' Target='worksheets/sheet1.xml' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet'/><Relationship Id='two' Target='worksheets/sheet2.xml' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet'/><Relationship Id='styles' Target='styles.xml' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles'/></Relationships>");
        Add(archive, "xl/styles.xml", "<styleSheet xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main'><numFmts count='1'><numFmt numFmtId='164' formatCode='#,##0.00 &quot;€&quot;'/></numFmts><fonts count='4'><font><sz val='11'/><name val='Calibri'/><color rgb='FF243347'/></font><font><b/><sz val='11'/><name val='Calibri'/><color rgb='FFFFFFFF'/></font><font><b/><sz val='19'/><name val='Calibri'/><color rgb='FF195EA6'/></font><font><sz val='10'/><name val='Calibri'/><color rgb='FF65748A'/></font></fonts><fills count='4'><fill><patternFill patternType='none'/></fill><fill><patternFill patternType='gray125'/></fill><fill><patternFill patternType='solid'><fgColor rgb='FF195EA6'/></patternFill></fill><fill><patternFill patternType='solid'><fgColor rgb='FFEAF2FB'/></patternFill></fill></fills><borders count='2'><border/><border><left style='thin'><color rgb='FFD0D8E4'/></left><right style='thin'><color rgb='FFD0D8E4'/></right><top style='thin'><color rgb='FFD0D8E4'/></top><bottom style='thin'><color rgb='FFD0D8E4'/></bottom></border></borders><cellStyleXfs count='1'><xf numFmtId='0' fontId='0' fillId='0' borderId='0'/></cellStyleXfs><cellXfs count='8'><xf numFmtId='0' fontId='0' fillId='0' borderId='0'/><xf numFmtId='0' fontId='0' fillId='0' borderId='1'><alignment vertical='center'/></xf><xf numFmtId='0' fontId='1' fillId='2' borderId='1'><alignment horizontal='center' vertical='center' wrapText='1'/></xf><xf numFmtId='0' fontId='2' fillId='0' borderId='0'><alignment vertical='center'/></xf><xf numFmtId='0' fontId='3' fillId='0' borderId='0'><alignment vertical='center' wrapText='1'/></xf><xf numFmtId='3' fontId='0' fillId='0' borderId='1'><alignment horizontal='right' vertical='center'/></xf><xf numFmtId='164' fontId='0' fillId='0' borderId='1'><alignment horizontal='right' vertical='center'/></xf><xf numFmtId='14' fontId='0' fillId='0' borderId='1'><alignment horizontal='center' vertical='center'/></xf></cellXfs></styleSheet>");
        string Text(string address, string value, int style = 1) => $"<c r='{address}' t='inlineStr' s='{style}'><is><t>{E(value)}</t></is></c>";
        string Number(string address, double value, int style) => $"<c r='{address}' s='{style}'><v>{N(value)}</v></c>";
        var sheet = new StringBuilder("<worksheet xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main'><sheetFormatPr defaultRowHeight='14'/><cols><col min='1' max='1' width='14'/><col min='2' max='2' width='32'/><col min='3' max='3' width='10'/><col min='4' max='5' width='16'/><col min='6' max='6' width='17'/></cols><sheetData>");
        sheet.Append("<row r='1' ht='30' customHeight='1'>" + Text("A1", "Demo Workshop — Inventory", 3) + "</row>");
        sheet.Append("<row r='2' ht='18' customHeight='1'>" + Text("A2", "January 2026 · Original fictional data for QuickPreview screenshots", 4) + "</row>");
        sheet.Append("<row r='4' ht='24' customHeight='1'>");
        var headers = new[] { "Part", "Description", "Qty", "Unit cost", "Total", "Updated" };
        for (var i = 0; i < headers.Length; i++) sheet.Append(Text(((char)('A' + i)) + "4", headers[i], 2));
        sheet.Append("</row>");
        var items = new[] { ("Mounting bracket", 12, 4.25), ("Rotor prototype", 3, 18.50), ("Cable guide", 24, 1.80), ("Bearing spacer", 16, 2.35), ("Enclosure panel", 8, 9.40), ("Knurled knob", 20, 3.15), ("Test fixture base", 5, 12.75) };
        double total = 0;
        for (var i = 0; i < items.Length; i++)
        {
            var row = i + 5; var item = items[i]; var line = item.Item2 * item.Item3; total += line;
            sheet.Append($"<row r='{row}' ht='19.5' customHeight='1'>" + Text($"A{row}", $"DEMO-{i + 1:000}") + Text($"B{row}", item.Item1) + Number($"C{row}", item.Item2, 5) + Number($"D{row}", item.Item3, 6) + $"<c r='E{row}' s='6'><f>C{row}*D{row}</f><v>{N(line)}</v></c>" + Number($"F{row}", FixedTime.Date.ToOADate(), 7) + "</row>");
        }
        sheet.Append("<row r='13' ht='24' customHeight='1'>" + Text("A13", "Total inventory value", 2) + $"<c r='E13' s='6'><f>SUM(E5:E11)</f><v>{N(total)}</v></c>" + Text("F13", "EUR", 1) + "</row>");
        sheet.Append("<row r='15' ht='18' customHeight='1'>" + Text("A15", "Demo data only. Prices, quantities and part identifiers are fictional.", 4) + "</row></sheetData><mergeCells count='4'><mergeCell ref='A1:F1'/><mergeCell ref='A2:F2'/><mergeCell ref='A13:D13'/><mergeCell ref='A15:F15'/></mergeCells></worksheet>");
        Add(archive, "xl/worksheets/sheet1.xml", sheet.ToString());
        Add(archive, "xl/worksheets/sheet2.xml", "<worksheet xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main'><cols><col min='1' max='3' width='28'/></cols><sheetData><row r='1' ht='36'>" + Text("A1", "About this demo", 3) + "</row><row r='3' ht='30'>" + Text("A3", "Source", 2) + Text("B3", "Original generated test files", 1) + "</row><row r='4' ht='30'>" + Text("A4", "Data", 2) + Text("B4", "Entirely fictional", 1) + "</row><row r='5' ht='30'>" + Text("A5", "Purpose", 2) + Text("B5", "Public product screenshots", 1) + "</row></sheetData><mergeCells count='1'><mergeCell ref='A1:C1'/></mergeCells></worksheet>");
    }

    private static void WriteWord(string path)
    {
        using var archive = NewZip(path);
        Add(archive, "[Content_Types].xml", "<Types xmlns='http://schemas.openxmlformats.org/package/2006/content-types'><Default Extension='rels' ContentType='application/vnd.openxmlformats-package.relationships+xml'/><Override PartName='/word/document.xml' ContentType='application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml'/></Types>");
        Add(archive, "_rels/.rels", "<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='main' Target='word/document.xml' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument'/></Relationships>");
        var paragraphs = new[] { "DEMO WORKSHOP", "Project brief — Desktop organizer", "Original fictional document · January 2026", "", "Purpose", "Create a small modular organizer for a demonstration workstation. This document contains synthetic project information only.", "", "Design requirements", "• Three removable trays for sample parts and tools.", "• Rounded edges and accessible mounting points.", "• Replaceable labels and a compact footprint.", "• Components suitable for a simple prototype assembly.", "", "Review checklist", "1. Inspect the CAD model and overall dimensions.", "2. Compare the demonstration inventory worksheet.", "3. Read the design guide and assembly notes.", "4. Export the final sample package for review.", "", "All names, quantities and project details in this document are fictional." };
        var xml = new StringBuilder("<w:document xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main'><w:body>");
        foreach (var paragraph in paragraphs) xml.Append("<w:p><w:r><w:t xml:space='preserve'>" + E(paragraph) + "</w:t></w:r></w:p>");
        xml.Append("</w:body></w:document>"); Add(archive, "word/document.xml", xml.ToString());
    }

    private static void WritePdf(string path)
    {
        string Escape(string text) => text.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
        string Text(double x, double y, int size, string text, bool bold = false) => $"BT /F{(bold ? 2 : 1)} {size} Tf {N(x)} {N(y)} Td ({Escape(text)}) Tj ET\n";
        // Landscape pages deliberately fit a desktop preview at its ordinary
        // fit-to-width scale, without manipulating the PDF viewer's own controls.
        var pageOne = new StringBuilder("0.098 0.369 0.651 rg 0 420 840 50 re f\n1 1 1 rg\n");
        pageOne.Append(Text(44, 438, 13, "DEMO WORKSHOP  /  DESIGN NOTE 01", true));
        pageOne.Append("0.12 0.18 0.25 rg\n" + Text(44, 382, 26, "Prototype mounting bracket", true));
        pageOne.Append("0.38 0.44 0.52 rg\n" + Text(44, 357, 11, "Original synthetic document for QuickPreview screenshots"));
        pageOne.Append("0.12 0.18 0.25 rg\n" + Text(44, 316, 14, "Review dimensions", true));
        var overview = new[] { ("Property", "Demo value"), ("Overall width", "80 mm"), ("Overall height", "68 mm"), ("Depth", "30 mm"), ("Base thickness", "12 mm"), ("Revision", "Demo A") };
        for (var i = 0; i < overview.Length; i++)
        {
            var y = 274 - i * 26;
            pageOne.Append(i == 0 ? "0.098 0.369 0.651 rg\n" : i % 2 == 0 ? "0.92 0.96 0.99 rg\n" : "1 1 1 rg\n");
            pageOne.Append($"44 {y} 350 26 re f\n0.80 0.84 0.89 RG 0.5 w 44 {y} 350 26 re S\n");
            pageOne.Append(i == 0 ? "1 1 1 rg\n" : "0.12 0.18 0.25 rg\n");
            pageOne.Append(Text(56, y + 8, 10, overview[i].Item1, i == 0));
            pageOne.Append(Text(239, y + 8, 10, overview[i].Item2, i == 0));
        }
        pageOne.Append("0.12 0.18 0.25 rg\n" + Text(452, 334, 14, "Original L-bracket", true));
        pageOne.Append("0.88 0.94 0.98 rg 452 144 344 182 re f\n0.20 0.47 0.66 rg\n");
        pageOne.Append("515 154 m 691 154 l 691 180.4 l 554.6 180.4 l 554.6 303.6 l 515 303.6 l h f\n");
        pageOne.Append("0.08 0.24 0.36 RG 1.3 w 515 154 m 691 154 l 691 180.4 l 554.6 180.4 l 554.6 303.6 l 515 303.6 l h S\n");
        pageOne.Append("0.36 0.42 0.49 RG 0.7 w 515 140 m 691 140 l S 515 136 m 515 144 l S 691 136 m 691 144 l S 712 154 m 712 303.6 l S 708 154 m 716 154 l S 708 303.6 m 716 303.6 l S\n");
        pageOne.Append("0.25 0.31 0.38 rg\n" + Text(585, 125, 10, "80 mm") + Text(723, 226, 10, "68 mm"));
        pageOne.Append(Text(44, 117, 10, "Check the model orientation and mounting surfaces."));
        pageOne.Append(Text(44, 99, 10, "Include the sample inventory sheet with the review package."));
        pageOne.Append(Text(452, 99, 10, "Synthetic geometry. No customer files or production design."));
        pageOne.Append("0.65 0.69 0.74 RG 44 55 m 796 55 l S\n0.38 0.44 0.52 rg\n");
        pageOne.Append(Text(44, 35, 9, "DEMO DATA ONLY  |  Generated 2026-01-15  |  Page 1 of 2"));
        var pageTwo = new StringBuilder("0.098 0.369 0.651 rg 0 420 840 50 re f\n1 1 1 rg\n" + Text(44, 438, 13, "DEMO WORKSHOP  /  SPECIFICATION", true));
        pageTwo.Append("0.12 0.18 0.25 rg\n" + Text(44, 382, 26, "Sample part specification", true));
        pageTwo.Append("0.38 0.44 0.52 rg\n" + Text(44, 357, 11, "A compact original fixture for software demonstrations and document preview."));
        var rows = new[] { ("Property", "Demonstration value"), ("Part identifier", "DEMO-BRACKET-001"), ("Overall width", "80 mm"), ("Overall height", "68 mm"), ("Depth", "30 mm"), ("Base thickness", "12 mm"), ("Nominal material", "Fictional prototype material"), ("Revision", "Demo A") };
        for (var i = 0; i < rows.Length; i++)
        {
            var y = 304 - i * 30;
            pageTwo.Append(i == 0 ? "0.098 0.369 0.651 rg\n" : i % 2 == 0 ? "0.92 0.96 0.99 rg\n" : "1 1 1 rg\n");
            pageTwo.Append($"44 {y} 752 30 re f\n0.80 0.84 0.89 RG 0.5 w 44 {y} 752 30 re S\n");
            pageTwo.Append(i == 0 ? "1 1 1 rg\n" : "0.12 0.18 0.25 rg\n");
            pageTwo.Append(Text(56, y + 10, 11, rows[i].Item1, i == 0)); pageTwo.Append(Text(306, y + 10, 11, rows[i].Item2, i == 0));
        }
        pageTwo.Append("0.38 0.44 0.52 rg\n" + Text(44, 75, 10, "Original test data for software demonstrations; not a manufacturing specification."));
        pageTwo.Append("0.65 0.69 0.74 RG 44 55 m 796 55 l S\n" + Text(44, 35, 9, "DEMO DATA ONLY  |  Generated 2026-01-15  |  Page 2 of 2"));
        string Stream(string content) => $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}endstream";
        var objects = new[] {
            "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [5 0 R 7 0 R] /Count 2 >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 840 470] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents 6 0 R >>", Stream(pageOne.ToString()),
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 840 470] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents 8 0 R >>", Stream(pageTwo.ToString()),
            "<< /Title (Demo design guide) /Author (Demo Workshop - fictional) /CreationDate (D:20260115120000Z) >>" };
        using var file = File.Create(path);
        void Write(string text) { var bytes = Encoding.ASCII.GetBytes(text); file.Write(bytes); }
        Write("%PDF-1.4\n"); var offsets = new List<long> { 0 };
        for (var i = 0; i < objects.Length; i++) { offsets.Add(file.Position); Write($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        var xref = file.Position; Write($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) Write(offset.ToString("0000000000", Invariant) + " 00000 n \n");
        Write($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R /Info 9 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    }

    private static void WriteImage(string path)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(new LinearGradientBrush(Color.FromRgb(22, 65, 90), Color.FromRgb(56, 137, 155), 90), null, new Rect(0, 0, 1500, 900));
            context.DrawEllipse(new SolidColorBrush(Color.FromRgb(255, 207, 106)), null, new Point(1130, 240), 105, 105);
            void Polygon(string color, params Point[] points)
            {
                var geometry = new StreamGeometry();
                using (var drawing = geometry.Open()) { drawing.BeginFigure(points[0], true, true); drawing.PolyLineTo(points.Skip(1).ToArray(), true, false); }
                context.DrawGeometry((Brush)new BrushConverter().ConvertFromString(color)!, null, geometry);
            }
            Polygon("#2F7083", new(0, 560), new(260, 265), new(565, 610), new(845, 320), new(1230, 650), new(1500, 440), new(1500, 900), new(0, 900));
            Polygon("#A9D6D3", new(0, 680), new(225, 475), new(470, 735), new(745, 535), new(1050, 760), new(1310, 570), new(1500, 720), new(1500, 900), new(0, 900));
            Polygon("#E6E7D2", new(0, 795), new(280, 695), new(610, 805), new(990, 680), new(1500, 830), new(1500, 900), new(0, 900));
            context.DrawText(new FormattedText("DEMO LANDSCAPE", Invariant, FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), 38, Brushes.White, 1), new Point(66, 66));
            context.DrawText(new FormattedText("Original synthetic image  /  1500 × 900", Invariant, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 21, new SolidColorBrush(Color.FromRgb(194, 224, 230)), 1), new Point(69, 120));
        }
        var bitmap = new RenderTargetBitmap(1500, 900, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file);
    }

    private static void WriteArchive(string path, IReadOnlyDictionary<string, string> files)
    {
        using var archive = NewZip(path);
        foreach (var name in new[] { "docs/", "models/", "images/", "tables/", "config/" }) Add(archive, name, Array.Empty<byte>());
        Add(archive, "README.md", "# Demo Workshop package\n\nOriginal synthetic test files for QuickPreview public screenshots.\nNo user documents or private data are included.\n");
        foreach (var (key, name) in new[] { ("pdf", "docs/design-guide.pdf"), ("word", "docs/project-brief.docx"), ("model", "models/rotor.stl"), ("step", "models/bracket.step"), ("obj", "models/rotor.obj"), ("threeMf", "models/rotor.3mf"), ("image", "images/landscape.png"), ("spreadsheet", "tables/inventory.xlsx"), ("json", "config/preview.json") }) Add(archive, name, File.ReadAllBytes(files[key]));
        Add(archive, "tables/parts.csv", "Part,Description,Quantity\nDEMO-001,Mounting bracket,12\nDEMO-002,Rotor prototype,3\nDEMO-003,Cable guide,24\n");
        for (var i = 1; i <= 9; i++) Add(archive, $"docs/notes/note-{i:00}.txt", $"Original demonstration note {i:00}.\nThis fictional note is included only to demonstrate archive listings.\n");
    }
}
