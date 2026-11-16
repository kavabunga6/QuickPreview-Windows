namespace QuickPreview;

internal sealed record SpreadsheetWorkbook(IReadOnlyList<SpreadsheetSheet> Sheets,
    IReadOnlyList<SpreadsheetCellStyle> Styles, string Summary, bool IsTruncated);
internal sealed record SpreadsheetSheet(string Name, IReadOnlyList<SpreadsheetColumn> Columns,
    IReadOnlyList<SpreadsheetRow> Rows, IReadOnlyList<SpreadsheetMerge> Merges, bool IsTruncated);
internal sealed record SpreadsheetColumn(int Index, double Width, bool Hidden);
internal sealed record SpreadsheetRow(int Index, double Height, bool Hidden, IReadOnlyList<SpreadsheetCell> Cells);
internal sealed record SpreadsheetCell(int Column, string Text, int StyleIndex);
internal sealed record SpreadsheetMerge(int Row, int Column, int RowSpan, int ColumnSpan);
internal sealed record SpreadsheetCellStyle(string FontName, double FontSize, bool Bold, bool Italic,
    bool Underline, string? Foreground, string? Background, string HorizontalAlignment,
    string VerticalAlignment, bool WrapText, string BorderLeft, string BorderTop, string BorderRight,
    string BorderBottom);
