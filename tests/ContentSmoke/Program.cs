using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuickPreview;

internal static class Program
{
    private static int _assertions;
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "fixtures");

    [STAThread]
    private static int Main()
    {
        Directory.CreateDirectory(Root);
        try
        {
            CheckTables().GetAwaiter().GetResult();
            CheckFont();
            Console.WriteLine($"PASS: {_assertions} CSV/font content assertions.");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }

    private static void CheckFont()
    {
        var fontPath = new[] { "arial.ttf", "segoeui.ttf", "tahoma.ttf" }
            .Select(name => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), name))
            .FirstOrDefault(File.Exists) ?? throw new Exception("No known Windows TrueType fixture font is available.");
        var copiedFont = Path.Combine(Root, "local font specimen.ttf");
        File.Copy(fontPath, copiedFont, true);
        var before = SHA256.HashData(File.ReadAllBytes(copiedFont));
        var preview = FontPreviewService.Load(copiedFont);
        Assert(!string.IsNullOrWhiteSpace(preview.Name) && preview.Family.GetTypefaces().Any(), "Font preview reads a local uninstalled file with spaces in its filename.");
        var typeface = new Typeface(preview.Family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var resolvesGlyphs = typeface.TryGetGlyphTypeface(out var glyphs) && glyphs.CharacterToGlyphMap.ContainsKey('A');
        if (!resolvesGlyphs)
        {
            var baseUri = new Uri(Root + Path.DirectorySeparatorChar);
            foreach (var source in new[] { "./" + Path.GetFileName(copiedFont) + "#" + preview.Name, "./" + Uri.EscapeDataString(Path.GetFileName(copiedFont)) + "#" + preview.Name, "./#" + preview.Name, new Uri(copiedFont).AbsoluteUri + "#" + preview.Name })
            {
                var candidate = new Typeface(new FontFamily(baseUri, source), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                Console.WriteLine($"Font diagnostic: {source}: {candidate.TryGetGlyphTypeface(out var candidateGlyph)} {candidateGlyph?.FontUri}");
            }
        }
        Assert(resolvesGlyphs, "Font resolves real glyph metadata from the file.");
        var text = new FormattedText("Aa Бб 0123", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 24, Brushes.Black, 1);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, 320, 80));
            drawing.DrawText(text, new Point(8, 8));
        }
        var bitmap = new RenderTargetBitmap(320, 80, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[320 * 80 * 4];
        bitmap.CopyPixels(pixels, 320 * 4, 0);
        Assert(text.Width > 40 && pixels.Where((_, index) => index % 4 != 3).Any(value => value < 100), "Font specimen actually renders visible Latin/Cyrillic/numeric glyphs.");
        Assert(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(copiedFont))), "Font specimen leaves its source unchanged and needs no font installation.");
    }

    private static async Task CheckTables()
    {
        var csv = Path.Combine(Root, "quoted.csv");
        var content = "Name,Notes,Count\r\n\"Doe, Jane\",\"first line\r\nsecond \"\"quoted\"\" line\",2\r\nplain,,3\r\n";
        File.WriteAllText(csv, content, new UTF8Encoding(true));
        var before = SHA256.HashData(File.ReadAllBytes(csv));
        var quoted = await TabularPreviewService.LoadAsync(csv, CancellationToken.None);
        Assert(quoted.Columns.SequenceEqual(new[] { "Name", "Notes", "Count" }) && quoted.Rows.Count == 2, "CSV headers and rows parse without a phantom trailing row.");
        Assert(quoted.Rows[0][0] == "Doe, Jane" && quoted.Rows[0][1] == "first line\r\nsecond \"quoted\" line", "CSV respects quoted delimiters, multiline cells and escaped quotes.");
        Assert(quoted.Rows[1][1] == "" && quoted.Rows[1][2] == "3", "CSV preserves empty middle fields.");
        Assert(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(csv))), "CSV preview is read-only.");

        var utf16 = Path.Combine(Root, "unicode-semicolon.csv");
        File.WriteAllText(utf16, "Имя;Город;Цена\r\nАнна;Москва;\"1,25\"\r\n", Encoding.Unicode);
        var semicolon = await TabularPreviewService.LoadAsync(utf16, CancellationToken.None);
        Assert(semicolon.Columns[0] == "Имя" && semicolon.Rows[0].SequenceEqual(new[] { "Анна", "Москва", "1,25" }), "UTF16 CSV preserves Cyrillic and detects semicolon outside decimal-comma quotes.");

        var tsv = Path.Combine(Root, "sample.tsv");
        File.WriteAllText(tsv, "one\ttwo\tthree\n1\t2\t3\n4\t5\n");
        var tabs = await TabularPreviewService.LoadAsync(tsv, CancellationToken.None);
        Assert(tabs.Columns.Count == 3 && tabs.Rows[1][2] == "", "TSV recognizes tabs and pads ragged rows to common columns.");

        var wide = Path.Combine(Root, "wide.csv");
        File.WriteAllText(wide, string.Join(',', Enumerable.Range(0, 60).Select(index => $"H{index}")) + "\n" + string.Join(',', Enumerable.Range(0, 60)));
        var columns = await TabularPreviewService.LoadAsync(wide, CancellationToken.None);
        Assert(columns.Columns.Count == 50 && columns.Rows[0].Length == 50 && columns.Rows[0][49] == "49" && columns.Summary.Contains("часть"), "Wide CSV caps and reports 50 columns.");

        var longCsv = Path.Combine(Root, "long.csv");
        File.WriteAllText(longCsv, "index,value\n" + string.Join('\n', Enumerable.Range(1, 500).Select(index => $"{index},row {index}")));
        var rows = await TabularPreviewService.LoadAsync(longCsv, CancellationToken.None);
        Assert(rows.Rows.Count == 300 && rows.Rows[^1][0] == "300" && rows.Summary.Contains("часть"), "Large CSV caps and reports the first 300 data rows.");

        var oversized = Path.Combine(Root, "large-cell.csv");
        File.WriteAllText(oversized, "header\n\"" + new string('x', 3 * 1024 * 1024) + "\"\n");
        var capped = await TabularPreviewService.LoadAsync(oversized, CancellationToken.None);
        Assert(capped.Rows.Count == 1 && capped.Rows[0][0].Length == 4096 && capped.Summary.Contains("часть"), "CSV byte and per-cell limits bound oversized quoted fields.");

        var empty = Path.Combine(Root, "empty.csv");
        File.WriteAllBytes(empty, Array.Empty<byte>());
        Assert((await TabularPreviewService.LoadAsync(empty, CancellationToken.None)).Rows.Count == 0, "Empty CSV is handled.");
        try
        {
            await TabularPreviewService.LoadAsync(csv, new CancellationToken(true));
            throw new Exception("CSV cancellation was ignored.");
        }
        catch (OperationCanceledException) { Assert(true, "CSV cancellation is honored."); }
    }

    private static void Assert(bool value, string message)
    {
        if (!value) throw new Exception(message);
        _assertions++;
        Console.WriteLine("PASS: " + message);
    }
}
