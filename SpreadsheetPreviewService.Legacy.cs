using System.Globalization;
using System.IO;
using System.Text;
using ExcelDataReader;

namespace QuickPreview;

internal static partial class SpreadsheetPreviewService
{
    private static SpreadsheetWorkbook ReadLegacy(Stream stream, Budget budget)
    {
        if (stream.Length > 32L * 1024 * 1024)
            throw new NotSupportedException("Таблица XLS слишком большая для быстрого предпросмотра (более 32 МБ).");
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            using var reader = ExcelReaderFactory.CreateBinaryReader(stream, new ExcelReaderConfiguration
            {
                LeaveOpen = true, FallbackEncoding = Encoding.GetEncoding(1251)
            });
            var sheets = new List<SpreadsheetSheet>();
            var styles = new List<SpreadsheetCellStyle> { DefaultStyle };
            do
            {
                budget.Check();
                if (sheets.Count >= MaxSheets) { budget.Truncated = true; break; }
                var truncated = reader.FieldCount > MaxColumns || reader.RowCount > MaxRows;
                var count = Math.Clamp(reader.FieldCount, 1, MaxColumns);
                var columns = Enumerable.Range(0, count).Select(index =>
                {
                    var width = index < reader.FieldCount ? reader.GetColumnWidth(index) : 8.43;
                    return new SpreadsheetColumn(index, ColumnPixels(width > 0 ? width : 8.43), width == 0);
                }).ToArray();
                var rows = new List<SpreadsheetRow>();
                while (rows.Count < MaxRows && reader.Read())
                {
                    budget.Check();
                    var cells = new List<SpreadsheetCell>();
                    for (var column = 0; column < Math.Min(reader.FieldCount, MaxColumns); column++)
                    {
                        if (!budget.TakeCell()) { truncated = true; break; }
                        var value = reader.GetValue(column);
                        var numeric = value is double or float or decimal or int or long or short;
                        var sourceStyle = reader.GetCellStyle(column);
                        var horizontal = sourceStyle.HorizontalAlignment.ToString();
                        var vertical = sourceStyle.VerticalAlignment.ToString();
                        var style = DefaultStyle with
                        {
                            HorizontalAlignment = horizontal is "Center" or "Centered" or "CenteredAcrossSelection" ? "center" :
                                horizontal == "Right" || horizontal == "General" && numeric ? "right" : "left",
                            VerticalAlignment = vertical is "Top" ? "top" : vertical == "Center" ? "center" : "bottom"
                        };
                        var text = value switch
                        {
                            null => "",
                            DateTime date => FormatValue(date, reader.GetNumberFormatString(column) ?? "dd.MM.yyyy"),
                            TimeSpan time => FormatValue(time, reader.GetNumberFormatString(column) ?? "[h]:mm:ss"),
                            bool flag => flag ? "TRUE" : "FALSE",
                            _ when numeric => FormatNumber(Convert.ToDouble(value, CultureInfo.InvariantCulture), reader.GetNumberFormatString(column) ?? "General"),
                            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
                        };
                        cells.Add(new(column, budget.Text(text), InternStyle(styles, style)));
                    }
                    var height = reader.RowHeight;
                    rows.Add(new(rows.Count, Math.Clamp(height > 0 ? height * 96 / 72 : 20, 12, 400), height == 0, cells));
                }
                var references = reader.MergeCells.Select(range => Address(range.FromRow, range.FromColumn) + ":" + Address(range.ToRow, range.ToColumn));
                var merges = ReadMerges(references, ref truncated);
                var maxRow = merges.Count == 0 ? rows.Count : Math.Max(rows.Count, merges.Max(m => m.Row + m.RowSpan));
                while (rows.Count < maxRow) rows.Add(new(rows.Count, 20, false, Array.Empty<SpreadsheetCell>()));
                if (rows.Count == 0) rows.Add(new(0, 20, false, Array.Empty<SpreadsheetCell>()));
                var maxColumn = merges.Count == 0 ? columns.Length : Math.Max(columns.Length, merges.Max(m => m.Column + m.ColumnSpan));
                if (maxColumn > columns.Length) columns = Enumerable.Range(0, maxColumn).Select(i => i < columns.Length ? columns[i] : new(i, 64, false)).ToArray();
                sheets.Add(new(LimitName(reader.Name ?? "", sheets.Count), columns, rows, merges, truncated || budget.Truncated));
            } while (reader.NextResult());
            return Finish(sheets, styles, "Excel XLS", budget);
        }
        catch (ExcelDataReader.Exceptions.InvalidPasswordException)
        {
            throw new NotSupportedException("Таблица защищена паролем. Откройте её в Office для ввода пароля.");
        }
    }

    private static string Address(int row, int column)
    {
        var name = "";
        for (column++; column > 0; column = (column - 1) / 26) name = (char)('A' + (column - 1) % 26) + name;
        return name + (row + 1).ToString(CultureInfo.InvariantCulture);
    }
}
