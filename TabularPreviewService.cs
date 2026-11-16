using System.IO;
using System.Text;

namespace QuickPreview;

internal sealed record TabularPreviewResult(IReadOnlyList<string> Columns, IReadOnlyList<string[]> Rows, string Summary);

internal static class TabularPreviewService
{
    public static bool Supports(string path) => Path.GetExtension(path).ToLowerInvariant() is ".csv" or ".tsv";
    public static Task<TabularPreviewResult> LoadAsync(string path, CancellationToken token) => Task.Run(async () =>
    {
        const int maxBytes = 2 * 1024 * 1024;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[(int)Math.Min(stream.Length, maxBytes)];
        var length = 0;
        while (length < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(length), token);
            if (read == 0) break;
            length += read;
        }
        if (length < bytes.Length) Array.Resize(ref bytes, length);
        var content = FormatCatalog.DecodeText(bytes);
        var delimiter = Path.GetExtension(path).Equals(".tsv", StringComparison.OrdinalIgnoreCase) ? '\t' : DetectDelimiter(content);
        var records = new List<string[]>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false; var started = false; var truncated = stream.Length > maxBytes;
        var index = 0;
        for (; index < content.Length && records.Count < 301; index++)
        {
            if (index % 4096 == 0) token.ThrowIfCancellationRequested();
            var value = content[index]; started = true;
            if (value == '"' && (quoted || field.Length == 0))
            {
                if (quoted && index + 1 < content.Length && content[index + 1] == '"')
                { Append('"'); index++; }
                else quoted = !quoted;
            }
            else if (!quoted && value == delimiter) FinishField();
            else if (!quoted && value is '\r' or '\n')
            {
                if (value == '\r' && index + 1 < content.Length && content[index + 1] == '\n') index++;
                FinishRecord(); started = false;
            }
            else Append(value);
        }
        if (records.Count < 301 && (started || fields.Count > 0 || field.Length > 0)) FinishRecord();
        if (records.Count == 301 && index < content.Length) truncated = true;
        if (records.Count == 0) return new TabularPreviewResult(Array.Empty<string>(), Array.Empty<string[]>(), "Таблица пуста");
        var width = records.Max(row => row.Length);
        var headers = Enumerable.Range(0, width).Select(index => index < records[0].Length && !string.IsNullOrWhiteSpace(records[0][index])
            ? records[0][index] : $"Столбец {index + 1}").ToArray();
        var rows = records.Skip(1).Select(row => Enumerable.Range(0, width).Select(index => index < row.Length ? row[index] : string.Empty).ToArray()).ToArray();
        var summary = $"Строк: {rows.Length:N0} · столбцов: {width} · разделитель: {(delimiter == '\t' ? "табуляция" : delimiter.ToString())}";
        if (truncated) summary += " · показана часть таблицы";
        return new TabularPreviewResult(headers, rows, summary);

        void Append(char value) { if (field.Length < 4096) field.Append(value); else truncated = true; }
        void FinishField()
        {
            if (fields.Count < 50) fields.Add(field.ToString()); else truncated = true;
            field.Clear();
        }
        void FinishRecord() { FinishField(); records.Add(fields.ToArray()); fields.Clear(); }
    }, token);

    private static char DetectDelimiter(string content)
    {
        var candidates = new[] { ',', ';', '\t', '|' };
        var counts = new int[candidates.Length]; var quoted = false; var lines = 0;
        for (var index = 0; index < Math.Min(content.Length, 8192) && lines < 8; index++)
        {
            if (content[index] == '"') { quoted = !quoted; continue; }
            if (quoted) continue;
            if (content[index] == '\n') lines++;
            for (var c = 0; c < candidates.Length; c++) if (content[index] == candidates[c]) counts[c]++;
        }
        return candidates[Array.IndexOf(counts, counts.Max())];
    }
}
