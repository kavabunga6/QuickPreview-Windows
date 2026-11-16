using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using ExcelNumberFormat;

namespace QuickPreview;

/// <summary>Bounded, local spreadsheet values and layout. Formulas, macros and links are never executed.</summary>
internal static partial class SpreadsheetPreviewService
{
    private const int MaxSheets = 30, MaxRows = 200, MaxColumns = 80, MaxCells = 20_000;
    private const int MaxCharacters = 500_000, MaxStyles = 2048;
    private const long MaxFileBytes = 256L * 1024 * 1024, MaxPartBytes = 8L * 1024 * 1024;
    private static readonly CultureInfo DisplayCulture = CultureInfo.GetCultureInfo("ru-RU");
    // The key is held only as long as its workbook/style (or a constant format).
    // A sequence of unrelated files cannot grow a process-wide format cache.
    private static readonly ConditionalWeakTable<string, NumberFormat> NumberFormats = new();
    private static readonly SpreadsheetCellStyle DefaultStyle = new("Calibri", 11, false, false, false,
        null, null, "left", "bottom", false, "none", "none", "none", "none");

    public static bool Supports(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".xlsx" or ".xlsm" or ".xltx" or ".xltm" or ".ods" or ".ots" or ".xls" or ".xlt";

    public static Task<SpreadsheetWorkbook> LoadAsync(string path, CancellationToken token = default) =>
        Task.Run(() => Load(path, token), token);

    private static SpreadsheetWorkbook Load(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Supports(path)) throw new NotSupportedException("Этот формат таблицы не поддерживается.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > MaxFileBytes) throw new NotSupportedException("Таблица слишком большая для быстрого предпросмотра (более 256 МБ).");
        var budget = new Budget(token);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".xls" or ".xlt") return ReadLegacy(stream, budget);
        Span<byte> signature = stackalloc byte[4];
        var read = stream.Read(signature); stream.Position = 0;
        if (read == 4 && signature.SequenceEqual(new byte[] { 0xD0, 0xCF, 0x11, 0xE0 }))
            throw new NotSupportedException("Таблица защищена паролем. Откройте её в Office для ввода пароля.");
        using var package = new Package(stream, token);
        return extension is ".ods" or ".ots" ? ReadOds(package, budget) : ReadXlsx(package, budget);
    }

    private static SpreadsheetWorkbook ReadXlsx(Package package, Budget budget)
    {
        var main = package.MainPart();
        var workbook = package.Xml(main);
        var relationships = package.Relationships(main);
        var shared = relationships.Values.FirstOrDefault(r => r.Type.EndsWith("/sharedStrings", StringComparison.Ordinal));
        var strings = shared is null ? Array.Empty<string>() : package.Xml(shared.Path).Descendants()
            .Where(e => e.Name.LocalName == "si").Select(e => string.Concat(e.Descendants()
                .Where(t => t.Name.LocalName == "t" && !t.Ancestors().Any(a => a.Name.LocalName == "rPh"))
                .Select(t => t.Value))).ToArray();
        var stylePart = relationships.Values.FirstOrDefault(r => r.Type.EndsWith("/styles", StringComparison.Ordinal));
        var styleData = stylePart is null ? new List<XlsxStyle> { new(DefaultStyle, "General", true) } :
            ReadXlsxStyles(package.Xml(stylePart.Path), package);
        var styles = styleData.Select(s => s.Style).ToList();
        var date1904 = workbook.Descendants().Any(e => e.Name.LocalName == "workbookPr" && Bool(Attr(e, "date1904")));
        var definitions = workbook.Descendants().Where(e => e.Name.LocalName == "sheet").ToList();
        var sheets = new List<SpreadsheetSheet>();
        if (definitions.Count > MaxSheets) budget.Truncated = true;
        foreach (var definition in definitions.Take(MaxSheets))
        {
            budget.Check();
            var id = definition.Attributes().FirstOrDefault(a => a.Name.LocalName == "id")?.Value ?? "";
            if (!relationships.TryGetValue(id, out var part) || !part.Type.EndsWith("/worksheet", StringComparison.Ordinal)) continue;
            var xml = package.Xml(part.Path);
            var truncated = false;
            var columnOverrides = new Dictionary<int, SpreadsheetColumn>();
            foreach (var col in xml.Descendants().Where(e => e.Name.LocalName == "col"))
            {
                var start = Int(Attr(col, "min"), 1) - 1; var end = Int(Attr(col, "max"), start + 1) - 1;
                for (var index = Math.Max(0, start); index <= Math.Min(end, MaxColumns - 1); index++)
                    columnOverrides[index] = new(index, ColumnPixels(Number(Attr(col, "width"), 8.43)), Bool(Attr(col, "hidden")));
            }
            var format = xml.Descendants().FirstOrDefault(e => e.Name.LocalName == "sheetFormatPr");
            var defaultWidth = ColumnPixels(Number(Attr(format, "defaultColWidth"), 8.43));
            var defaultHeight = Math.Clamp(Number(Attr(format, "defaultRowHeight"), 15) * 96 / 72, 12, 400);
            var rows = new Dictionary<int, SpreadsheetRow>();
            var maxRow = 0; var maxColumn = 0; var nextRow = 0;
            foreach (var row in xml.Descendants().Where(e => e.Name.LocalName == "row"))
            {
                budget.Check();
                var rowIndex = Int(Attr(row, "r"), nextRow + 1) - 1; nextRow = rowIndex + 1;
                if (rowIndex < 0 || rowIndex >= MaxRows) { truncated = true; continue; }
                maxRow = Math.Max(maxRow, rowIndex);
                var cells = new List<SpreadsheetCell>(); var nextColumn = 0;
                foreach (var cell in row.Elements().Where(e => e.Name.LocalName == "c"))
                {
                    budget.Check();
                    var reference = Attr(cell, "r");
                    var column = reference.Length == 0 ? nextColumn : CellAddress(reference).Column;
                    nextColumn = column + 1;
                    if (column < 0 || column >= MaxColumns) { truncated = true; continue; }
                    if (!budget.TakeCell()) { truncated = true; break; }
                    var styleIndex = Int(Attr(cell, "s"), 0);
                    if (styleIndex < 0 || styleIndex >= styleData.Count) styleIndex = 0;
                    var type = Attr(cell, "t");
                    var cachedValue = cell.Elements().FirstOrDefault(e => e.Name.LocalName == "v");
                    var value = cachedValue?.Value ?? "";
                    var numeric = type is "" or "n";
                    if (type == "s") value = int.TryParse(value, out var index) && index >= 0 && index < strings.Length ? strings[index] : "";
                    else if (type == "inlineStr") value = string.Concat(cell.Descendants().Where(e => e.Name.LocalName == "t").Select(e => e.Value));
                    else if (type == "b") value = value == "1" ? "TRUE" : "FALSE";
                    else if (numeric && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                        value = FormatNumber(number, styleData[styleIndex].Format, date1904);
                    if (cachedValue is null && value.Length == 0 && cell.Elements().FirstOrDefault(e => e.Name.LocalName == "f") is { } formula)
                        value = "=" + formula.Value;
                    if (numeric && styleData[styleIndex].GeneralAlignment && value.Length > 0)
                        styleIndex = InternStyle(styles, styleData[styleIndex].Style with { HorizontalAlignment = "right" });
                    cells.Add(new(column, budget.Text(value), styleIndex));
                    maxColumn = Math.Max(maxColumn, column);
                }
                rows[rowIndex] = new(rowIndex, Math.Clamp(Number(Attr(row, "ht"), defaultHeight * 72 / 96) * 96 / 72, 12, 400),
                    Bool(Attr(row, "hidden")), cells.OrderBy(c => c.Column).ToArray());
            }
            var merges = ReadMerges(xml.Descendants().Where(e => e.Name.LocalName == "mergeCell").Select(e => Attr(e, "ref")), ref truncated);
            foreach (var merge in merges) { maxRow = Math.Max(maxRow, merge.Row + merge.RowSpan - 1); maxColumn = Math.Max(maxColumn, merge.Column + merge.ColumnSpan - 1); }
            var columns = Enumerable.Range(0, maxColumn + 1).Select(i => columnOverrides.GetValueOrDefault(i) ?? new(i, defaultWidth, false)).ToArray();
            var allRows = Enumerable.Range(0, maxRow + 1).Select(i => rows.GetValueOrDefault(i) ?? new(i, defaultHeight, false, Array.Empty<SpreadsheetCell>())).ToArray();
            sheets.Add(new(LimitName(Attr(definition, "name"), sheets.Count), columns, allRows, merges, truncated || budget.Truncated));
        }
        return Finish(sheets, styles, "Excel", budget);
    }

    private sealed record XlsxStyle(SpreadsheetCellStyle Style, string Format, bool GeneralAlignment);
    private static List<XlsxStyle> ReadXlsxStyles(XDocument document, Package package)
    {
        var root = document.Root;
        List<XElement> Group(string name) => root?.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Elements().Take(MaxStyles).ToList() ?? new();
        var fonts = Group("fonts"); var fills = Group("fills"); var borders = Group("borders");
        var formats = document.Descendants().Where(e => e.Name.LocalName == "numFmt")
            .GroupBy(e => Int(Attr(e, "numFmtId"), 0)).ToDictionary(g => g.Key, g => Attr(g.First(), "formatCode"));
        var theme = package.ThemeColors();
        var styles = new List<XlsxStyle>();
        foreach (var xf in Group("cellXfs"))
        {
            XElement? At(List<XElement> items, string key) { var index = Int(Attr(xf, key), 0); return index >= 0 && index < items.Count ? items[index] : null; }
            var font = At(fonts, "fontId"); var fill = At(fills, "fillId"); var border = At(borders, "borderId");
            var alignment = xf.Elements().FirstOrDefault(e => e.Name.LocalName == "alignment");
            XElement? Child(XElement? parent, string name) => parent?.Elements().FirstOrDefault(e => e.Name.LocalName == name);
            var name = Attr(Child(font, "name"), "val"); if (name.Length == 0 || name.Length > 64) name = "Calibri";
            var pattern = Child(fill, "patternFill");
            var background = Attr(pattern, "patternType") == "solid" ? Color(Child(pattern, "fgColor"), theme) : null;
            var horizontal = Attr(alignment, "horizontal"); var vertical = Attr(alignment, "vertical");
            var style = new SpreadsheetCellStyle(name, Math.Clamp(Number(Attr(Child(font, "sz"), "val"), 11), 6, 48),
                Enabled(Child(font, "b")), Enabled(Child(font, "i")), Child(font, "u") is { } underline && Attr(underline, "val") != "none",
                Color(Child(font, "color"), theme), background,
                horizontal is "center" or "centerContinuous" ? "center" : horizontal == "right" ? "right" : "left",
                vertical is "top" or "center" ? vertical : "bottom", Bool(Attr(alignment, "wrapText")),
                Border(Child(border, "left")), Border(Child(border, "top")), Border(Child(border, "right")), Border(Child(border, "bottom")));
            var formatId = Int(Attr(xf, "numFmtId"), 0);
            styles.Add(new(style, formats.GetValueOrDefault(formatId) ?? BuiltinFormat(formatId), horizontal.Length == 0 || horizontal == "general"));
        }
        if (styles.Count == 0) styles.Add(new(DefaultStyle, "General", true));
        return styles;
    }

    private static string Border(XElement? element) => Attr(element, "style") switch
    {
        "medium" or "mediumDashDot" or "mediumDashDotDot" or "mediumDashed" => "medium",
        "thick" => "thick", "double" => "double", "dashed" or "dashDot" or "dashDotDot" or "slantDashDot" => "dashed",
        "dotted" or "hair" => "dotted", "thin" => "thin", _ => "none"
    };

    private static string? Color(XElement? element, IReadOnlyList<string> theme)
    {
        if (element is null) return null;
        var rgb = Attr(element, "rgb");
        if (rgb.Length == 8) rgb = rgb[2..];
        if (rgb.Length != 6 || !rgb.All(Uri.IsHexDigit))
        {
            var themeIndex = Int(Attr(element, "theme"), -1);
            var indexed = Int(Attr(element, "indexed"), -1);
            rgb = themeIndex >= 0 && themeIndex < theme.Count ? theme[themeIndex] : indexed >= 0 && indexed < IndexedColors.Length ? IndexedColors[indexed] : "";
        }
        if (rgb.Length != 6) return null;
        var tint = Math.Clamp(Number(Attr(element, "tint"), 0), -1, 1);
        if (tint != 0)
        {
            var parts = new[] { 0, 2, 4 }.Select(i => Convert.ToInt32(rgb.Substring(i, 2), 16)).Select(v => (int)Math.Round(tint < 0 ? v * (1 + tint) : v * (1 - tint) + 255 * tint));
            rgb = string.Concat(parts.Select(v => Math.Clamp(v, 0, 255).ToString("X2", CultureInfo.InvariantCulture)));
        }
        return "#" + rgb.ToUpperInvariant();
    }

    private static readonly string[] IndexedColors = {
        "000000","FFFFFF","FF0000","00FF00","0000FF","FFFF00","FF00FF","00FFFF",
        "000000","FFFFFF","FF0000","00FF00","0000FF","FFFF00","FF00FF","00FFFF",
        "800000","008000","000080","808000","800080","008080","C0C0C0","808080",
        "9999FF","993366","FFFFCC","CCFFFF","660066","FF8080","0066CC","CCCCCC",
        "000080","FF00FF","FFFF00","00FFFF","800080","800000","008080","0000FF",
        "00CCFF","CCFFFF","CCFFCC","FFFF99","99CCFF","FF99CC","CC99FF","FFCC99",
        "3366FF","33CCCC","99CC00","FFCC00","FF9900","FF6600","666699","969696",
        "003366","339966","003300","333300","993300","993366","333399","333333" };

    private static string BuiltinFormat(int id) => id switch
    {
        1 => "0", 2 => "0.00", 3 => "#,##0", 4 => "#,##0.00", 9 => "0%", 10 => "0.00%",
        11 => "0.00E+00", 12 => "# ?/?", 13 => "# ??/??", 14 => "dd.MM.yyyy", 15 => "d-mmm-yy",
        16 => "d-mmm", 17 => "mmm-yy", 18 => "h:mm AM/PM", 19 => "h:mm:ss AM/PM", 20 => "h:mm",
        21 => "h:mm:ss", 22 => "dd.MM.yyyy h:mm", >= 27 and <= 36 => "dd.MM.yyyy", 37 => "#,##0",
        38 => "#,##0", 39 => "#,##0.00", 40 => "#,##0.00", 45 => "mm:ss", 46 => "[h]:mm:ss",
        47 => "mm:ss.0", >= 50 and <= 58 => "dd.MM.yyyy", _ => "General"
    };

    internal static string FormatNumber(double value, string format, bool date1904 = false) =>
        FormatValue(value, format, date1904);

    private static string FormatValue(object value, string format, bool date1904 = false)
    {
        var fallback = Convert.ToString(value, DisplayCulture) ?? "";
        if (format.Length == 0 || format.Length > 256) return fallback;
        try
        {
            var parsed = NumberFormats.GetValue(format, static key => new NumberFormat(key));
            return parsed.IsValid ? parsed.Format(value, DisplayCulture, date1904) : fallback;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
        {
            // Unsupported formats retain the raw value, never an unscaled value
            // with a misleading quoted unit appended to it.
            return fallback;
        }
    }
    private static IReadOnlyList<SpreadsheetMerge> ReadMerges(IEnumerable<string> references, ref bool truncated)
    {
        var result = new List<SpreadsheetMerge>(); var occupied = new HashSet<int>();
        foreach (var reference in references.Take(4097))
        {
            if (result.Count >= 4096) { truncated = true; break; }
            var pair = reference.Split(':'); var start = CellAddress(pair[0]); var end = CellAddress(pair.Length == 1 ? pair[0] : pair[1]);
            if (start.Row < 0 || start.Column < 0 || end.Row < start.Row || end.Column < start.Column) continue;
            if (start.Row >= MaxRows || start.Column >= MaxColumns) { truncated = true; continue; }
            if (end.Row >= MaxRows || end.Column >= MaxColumns) truncated = true;
            var merge = new SpreadsheetMerge(start.Row, start.Column, Math.Min(end.Row, MaxRows - 1) - start.Row + 1, Math.Min(end.Column, MaxColumns - 1) - start.Column + 1);
            var overlap = false;
            for (var row = merge.Row; row < merge.Row + merge.RowSpan && !overlap; row++)
                for (var column = merge.Column; column < merge.Column + merge.ColumnSpan; column++) if (occupied.Contains(row * MaxColumns + column)) { overlap = true; break; }
            if (overlap) { truncated = true; continue; }
            for (var row = merge.Row; row < merge.Row + merge.RowSpan; row++) for (var column = merge.Column; column < merge.Column + merge.ColumnSpan; column++) occupied.Add(row * MaxColumns + column);
            result.Add(merge);
        }
        return result;
    }

    private static SpreadsheetWorkbook Finish(List<SpreadsheetSheet> sheets, List<SpreadsheetCellStyle> styles, string format, Budget budget)
    {
        budget.Check();
        if (sheets.Count == 0) sheets.Add(new("Лист 1", new[] { new SpreadsheetColumn(0, 64, false) },
            new[] { new SpreadsheetRow(0, 20, false, Array.Empty<SpreadsheetCell>()) }, Array.Empty<SpreadsheetMerge>(), false));
        return new(sheets, styles, $"{format} · листов: {sheets.Count} · сохранённые значения ячеек", budget.Truncated || sheets.Any(s => s.IsTruncated));
    }
    private static int InternStyle(List<SpreadsheetCellStyle> styles, SpreadsheetCellStyle style)
    {
        var index = styles.IndexOf(style); if (index >= 0) return index;
        if (styles.Count >= MaxStyles) return 0;
        styles.Add(style); return styles.Count - 1;
    }
    private static string LimitName(string name, int index) => name.Length == 0 ? "Лист " + (index + 1) : name[..Math.Min(128, name.Length)];
    private static double ColumnPixels(double width) => Math.Clamp(width * 7 + 5, 24, 640);
    private static string Attr(XElement? element, string name) => element?.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value ?? "";
    private static bool Bool(string value) => value is "1" or "true";
    private static bool Enabled(XElement? element) => element is not null && Attr(element, "val") is not ("0" or "false");
    private static int Int(string text, int fallback) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : fallback;
    private static double Number(string text, double fallback) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) && double.IsFinite(result) ? result : fallback;
    private static (int Row, int Column) CellAddress(string value)
    {
        long column = 0; var index = 0; value = value.Replace("$", "");
        while (index < value.Length && value[index] is >= 'A' and <= 'Z' or >= 'a' and <= 'z') { column = column * 26 + char.ToUpperInvariant(value[index++]) - 'A' + 1; if (column > 16384) return (-1, -1); }
        return (Int(value[index..], 0) - 1, (int)column - 1);
    }
    private sealed class Budget
    {
        internal readonly CancellationToken Token; internal bool Truncated;
        private int _cells, _characters;
        internal Budget(CancellationToken token) => Token = token;
        internal void Check() => Token.ThrowIfCancellationRequested();
        internal bool TakeCell() { Check(); if (_cells >= MaxCells) { Truncated = true; return false; } _cells++; return true; }
        internal string Text(string text)
        {
            Check(); var allowed = Math.Min(4096, Math.Max(0, MaxCharacters - _characters));
            if (text.Length > allowed) { text = text[..allowed]; Truncated = true; }
            _characters += text.Length;
            return new string(text.Select(c => char.IsControl(c) && c is not ('\t' or '\n' or '\r') ? ' ' : c).ToArray());
        }
    }
    private sealed record Relationship(string Path, string Type);
    private sealed class Package : IDisposable
    {
        private readonly ZipArchive _archive; private readonly CancellationToken _token; private long _remaining = 32L * 1024 * 1024;
        internal Package(Stream stream, CancellationToken token)
        {
            _token = token; _archive = new(stream, ZipArchiveMode.Read, true);
            if (_archive.Entries.Count > 20_000) throw new NotSupportedException("Таблица содержит слишком много частей для быстрого предпросмотра.");
        }
        internal bool Contains(string path) => _archive.GetEntry(path) is not null;
        internal XDocument Xml(string path)
        {
            _token.ThrowIfCancellationRequested(); var entry = _archive.GetEntry(path) ?? throw new InvalidDataException("В таблице отсутствует часть: " + path);
            if (entry.Length > MaxPartBytes || entry.Length > _remaining) throw new NotSupportedException("Содержимое таблицы слишком большое для быстрого предпросмотра.");
            _remaining -= entry.Length; using var stream = entry.Open();
            return SafeXmlDocument.Load(stream, MaxPartBytes, _token);
        }
        internal string MainPart() => Contains("_rels/.rels") ? Relationships("").Values.FirstOrDefault(r => r.Type.EndsWith("/officeDocument", StringComparison.Ordinal))?.Path ?? "xl/workbook.xml" : "xl/workbook.xml";
        internal Dictionary<string, Relationship> Relationships(string owner)
        {
            var slash = owner.LastIndexOf('/'); var folder = slash >= 0 ? owner[..(slash + 1)] : ""; var file = slash >= 0 ? owner[(slash + 1)..] : owner;
            var path = owner.Length == 0 ? "_rels/.rels" : folder + "_rels/" + file + ".rels"; var result = new Dictionary<string, Relationship>(StringComparer.Ordinal);
            if (!Contains(path)) return result;
            foreach (var relationship in Xml(path).Descendants().Where(e => e.Name.LocalName == "Relationship"))
            {
                _token.ThrowIfCancellationRequested(); if (Attr(relationship, "TargetMode").Equals("External", StringComparison.OrdinalIgnoreCase)) continue;
                var target = Attr(relationship, "Target"); if (target.Length == 0 || target.Contains('\\')) continue;
                if (!Uri.TryCreate(new Uri("https://package.invalid/" + owner), target, out var resolved) || resolved.Host != "package.invalid" || resolved.Scheme != "https" || resolved.Query.Length > 0 || resolved.Fragment.Length > 0) continue;
                result[Attr(relationship, "Id")] = new(Uri.UnescapeDataString(resolved.AbsolutePath.TrimStart('/')), Attr(relationship, "Type"));
            }
            return result;
        }
        internal IReadOnlyList<string> ThemeColors()
        {
            var fallback = new[] { "FFFFFF", "000000", "EEECE1", "1F497D", "4F81BD", "C0504D", "9BBB59", "8064A2", "4BACC6", "F79646", "0000FF", "800080" };
            if (!Contains("xl/theme/theme1.xml")) return fallback;
            var scheme = Xml("xl/theme/theme1.xml").Descendants().FirstOrDefault(e => e.Name.LocalName == "clrScheme");
            if (scheme is null) return fallback;
            var values = scheme.Elements().Select(e => e.Elements().FirstOrDefault()).Select(e => Attr(e, "val") is { Length: 6 } value ? value : Attr(e, "lastClr")).ToList();
            // OOXML theme indices use light1,dark1,light2,dark2 rather than XML order.
            if (values.Count >= 4) { (values[0], values[1]) = (values[1], values[0]); (values[2], values[3]) = (values[3], values[2]); }
            return values.Count >= 12 && values.All(v => v.Length == 6 && v.All(Uri.IsHexDigit)) ? values : fallback;
        }
        public void Dispose() => _archive.Dispose();
    }
}
