using System.Globalization;
using System.Xml.Linq;

namespace QuickPreview;

internal static partial class SpreadsheetPreviewService
{
    private static SpreadsheetWorkbook ReadOds(Package package, Budget budget)
    {
        if (package.Contains("META-INF/manifest.xml") && package.Xml("META-INF/manifest.xml").Descendants().Any(e => e.Name.LocalName == "encryption-data"))
            throw new NotSupportedException("Таблица защищена паролем. Откройте её в офисном приложении для ввода пароля.");
        var document = package.Xml("content.xml");
        var definitions = new Dictionary<string, XElement>(StringComparer.Ordinal);
        void Collect(XDocument xml)
        {
            foreach (var style in xml.Descendants().Where(e => e.Name.LocalName == "style").Take(4096))
                if (Attr(style, "name") is { Length: > 0 } name) definitions[name] = style;
        }
        if (package.Contains("styles.xml")) Collect(package.Xml("styles.xml"));
        Collect(document);
        Dictionary<string, string> Properties(string name)
        {
            var chain = new List<XElement>(); var visited = new HashSet<string>();
            while (name.Length > 0 && chain.Count < 8 && visited.Add(name) && definitions.TryGetValue(name, out var style))
            { chain.Add(style); name = Attr(style, "parent-style-name"); }
            var properties = new Dictionary<string, string>();
            foreach (var style in chain.AsEnumerable().Reverse())
                foreach (var property in style.Elements()) foreach (var attribute in property.Attributes()) properties[attribute.Name.LocalName] = attribute.Value;
            return properties;
        }
        var styles = new List<SpreadsheetCellStyle> { DefaultStyle };
        int Style(string name, bool numeric)
        {
            var properties = Properties(name);
            string Get(string key) => properties.GetValueOrDefault(key) ?? "";
            var horizontal = Get("text-align"); var vertical = Get("vertical-align");
            string? Hex(string value) => value.Length == 7 && value[0] == '#' && value[1..].All(Uri.IsHexDigit) ? value.ToUpperInvariant() : null;
            string BorderValue(string side)
            {
                var value = Get("border-" + side); if (value.Length == 0) value = Get("border");
                return value.Length == 0 || value == "none" ? "none" : value.Contains("double", StringComparison.Ordinal) ? "double" : value.Contains("dashed", StringComparison.Ordinal) ? "dashed" : value.Contains("dotted", StringComparison.Ordinal) ? "dotted" : "thin";
            }
            return InternStyle(styles, new(Get("font-name") is { Length: > 0 and <= 64 } font ? font : "Calibri",
                Math.Clamp(Length(Get("font-size"), 11 * 96 / 72) * 72 / 96, 6, 48), Get("font-weight") == "bold",
                Get("font-style") == "italic", Get("text-underline-style") is { Length: > 0 } line && line != "none",
                Hex(Get("color")), Hex(Get("background-color")), horizontal == "center" ? "center" : horizontal is "end" or "right" || horizontal.Length == 0 && numeric ? "right" : "left",
                vertical is "top" or "middle" ? vertical == "middle" ? "center" : "top" : "bottom", Get("wrap-option") == "wrap",
                BorderValue("left"), BorderValue("top"), BorderValue("right"), BorderValue("bottom")));
        }
        var tables = document.Descendants().Where(e => e.Name.LocalName == "spreadsheet").Elements().Where(e => e.Name.LocalName == "table").ToList();
        var sheets = new List<SpreadsheetSheet>();
        if (tables.Count > MaxSheets) budget.Truncated = true;
        foreach (var table in tables.Take(MaxSheets))
        {
            budget.Check(); var truncated = false;
            var columnDefinitions = new List<SpreadsheetColumn>();
            foreach (var column in table.Descendants().Where(e => e.Name.LocalName == "table-column"))
            {
                var properties = Properties(Attr(column, "style-name"));
                var width = Math.Clamp(Length(properties.GetValueOrDefault("column-width") ?? "", 64), 24, 640);
                var repeat = Math.Clamp(Int(Attr(column, "number-columns-repeated"), 1), 1, MaxColumns);
                for (var i = 0; i < repeat && columnDefinitions.Count < MaxColumns; i++) columnDefinitions.Add(new(columnDefinitions.Count, width, Attr(column, "visibility") is "collapse" or "filter"));
            }
            var rows = new List<SpreadsheetRow>(); var mergeReferences = new List<string>(); var maxColumn = 0;
            var sourceRows = table.Descendants().Where(e => e.Name.LocalName == "table-row").ToList();
            foreach (var row in sourceRows)
            {
                budget.Check(); var elements = row.Elements().Where(e => e.Name.LocalName is "table-cell" or "covered-table-cell").ToList();
                var repeatRows = Math.Max(1, Int(Attr(row, "number-rows-repeated"), 1));
                var blank = elements.All(e => string.IsNullOrWhiteSpace(e.Value) && Attr(e, "value").Length == 0 && Attr(e, "date-value").Length == 0 && Attr(e, "formula").Length == 0 && Attr(e, "style-name").Length == 0);
                // A trailing empty tail is not content. An interior repeated row,
                // however, must retain its positions rather than shifting later data.
                if (blank && repeatRows > MaxRows && ReferenceEquals(row, sourceRows[^1])) continue;
                if (rows.Count >= MaxRows) { truncated = true; break; }
                var rowProperties = Properties(Attr(row, "style-name")); var height = Math.Clamp(Length(rowProperties.GetValueOrDefault("row-height") ?? "", 20), 12, 400);
                var repeatsToRead = Math.Min(repeatRows, MaxRows - rows.Count);
                for (var repetition = 0; repetition < repeatsToRead; repetition++)
                {
                    budget.Check(); var cells = new List<SpreadsheetCell>(); var columnIndex = 0;
                    foreach (var cell in elements)
                    {
                        var repeatCells = Math.Max(1, Int(Attr(cell, "number-columns-repeated"), 1));
                        var paragraphs = cell.Descendants().Where(e => e.Name.LocalName == "p").Select(OdsText).ToArray();
                        var text = string.Join('\n', paragraphs);
                        var type = Attr(cell, "value-type"); var numeric = type is "float" or "currency" or "percentage";
                        if (text.Length == 0)
                        {
                            text = new[] { "string-value", "date-value", "time-value", "boolean-value", "value" }.Select(key => Attr(cell, key)).FirstOrDefault(v => v.Length > 0) ?? "";
                            if (numeric && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) text = FormatNumber(value, type == "percentage" ? "0.00%" : "General");
                            else if (type == "date" && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) text = date.ToString("dd.MM.yyyy", DisplayCulture);
                            if (text.Length == 0 && Attr(cell, "formula") is { Length: > 0 } formula) text = formula;
                        }
                        var style = Style(Attr(cell, "style-name"), numeric);
                        var spanRows = Math.Max(1, Int(Attr(cell, "number-rows-spanned"), 1)); var spanColumns = Math.Max(1, Int(Attr(cell, "number-columns-spanned"), 1));
                        for (var repetitionCell = 0; repetitionCell < repeatCells && columnIndex < MaxColumns; repetitionCell++, columnIndex++)
                        {
                            if (!budget.TakeCell()) { truncated = true; break; }
                            cells.Add(new(columnIndex, budget.Text(text), style));
                            if (cell.Name.LocalName != "covered-table-cell" && (spanRows > 1 || spanColumns > 1))
                                mergeReferences.Add(Address(rows.Count, columnIndex) + ":" + Address(Math.Min(MaxRows, rows.Count + spanRows - 1), Math.Min(MaxColumns, columnIndex + spanColumns - 1)));
                            if (text.Length > 0 || Attr(cell, "style-name").Length > 0 || spanRows > 1 || spanColumns > 1) maxColumn = Math.Max(maxColumn, columnIndex);
                        }
                        if (columnIndex >= MaxColumns)
                        {
                            if (text.Length > 0 && repeatCells > MaxColumns) truncated = true;
                            break;
                        }
                    }
                    rows.Add(new(rows.Count, height, Attr(row, "visibility") is "collapse" or "filter", cells));
                }
                if (repeatRows > repeatsToRead) { truncated = true; break; }
            }
            var merges = ReadMerges(mergeReferences, ref truncated);
            foreach (var merge in merges) maxColumn = Math.Max(maxColumn, merge.Column + merge.ColumnSpan - 1);
            var columns = Enumerable.Range(0, maxColumn + 1).Select(i => i < columnDefinitions.Count ? columnDefinitions[i] : new(i, 64, false)).ToArray();
            var maxRow = merges.Count == 0 ? rows.Count : Math.Max(rows.Count, merges.Max(m => m.Row + m.RowSpan));
            while (rows.Count < maxRow) rows.Add(new(rows.Count, 20, false, Array.Empty<SpreadsheetCell>()));
            if (rows.Count == 0) rows.Add(new(0, 20, false, Array.Empty<SpreadsheetCell>()));
            sheets.Add(new(LimitName(Attr(table, "name"), sheets.Count), columns, rows, merges, truncated || budget.Truncated));
        }
        return Finish(sheets, styles, "OpenDocument", budget);
    }

    private static string OdsText(XElement element)
    {
        var output = new System.Text.StringBuilder();
        void Visit(XNode node)
        {
            if (node is XText text) output.Append(text.Value);
            else if (node is XElement child)
            {
                if (child.Name.LocalName == "s") output.Append(' ', Math.Clamp(Int(Attr(child, "c"), 1), 1, 100));
                else if (child.Name.LocalName == "line-break") output.Append('\n');
                else if (child.Name.LocalName == "tab") output.Append('\t');
                else foreach (var descendant in child.Nodes()) Visit(descendant);
            }
        }
        foreach (var node in element.Nodes()) Visit(node);
        return output.ToString();
    }

    private static double Length(string text, double fallback)
    {
        var end = 0; while (end < text.Length && (char.IsDigit(text[end]) || text[end] is '.' or '-' or '+')) end++;
        if (end == 0) return fallback; var value = Number(text[..end], fallback);
        return text[end..].ToLowerInvariant() switch { "pt" => value * 96 / 72, "cm" => value * 96 / 2.54, "mm" => value * 96 / 25.4, "in" => value * 96, "px" or "" => value, _ => fallback };
    }
}
