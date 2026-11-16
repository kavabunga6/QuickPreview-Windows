using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace QuickPreview;

internal sealed record SourcePreviewResult(string Text, string Summary, bool IsTruncated);

/// <summary>Reads source text for visual formats without parsing, formatting, rendering, or extracting it.</summary>
internal static class SourcePreviewService
{
    private const int SniffBytes = 4096;
    private const int MaxPreviewBytes = 2 * 1024 * 1024;
    private const long MaxContainerBytes = 128L * 1024 * 1024;
    private static readonly HashSet<string> Candidates = new(StringComparer.OrdinalIgnoreCase)
    {
        ".step", ".stp", ".iges", ".igs", ".brep", ".brp", ".fcstd", ".stl", ".obj",
        ".3mf", ".ply", ".off", ".amf", ".3ds", ".dae", ".fbx", ".gltf", ".glb",
        ".wrl", ".3dm", ".ifc", ".bim", ".dxf", ".dwg", ".svg", ".svgz", ".csv", ".tsv"
    };
    private static readonly HashSet<string> BinaryOnly = new(StringComparer.OrdinalIgnoreCase)
        { ".glb", ".3ds", ".3dm", ".dwg" };
    private static readonly byte[] BinaryFbxHeader = Encoding.ASCII.GetBytes("Kaydara FBX Binary");
    private static readonly byte[] BinaryDxfHeader = Encoding.ASCII.GetBytes("AutoCAD Binary DXF");

    public static bool IsCandidate(string path) => Candidates.Contains(Path.GetExtension(path));

    public static Task<bool> CanShowAsync(string path, CancellationToken token = default) =>
        Task.Run(() => CanShowCoreAsync(path, token), token);

    private static async Task<bool> CanShowCoreAsync(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var extension = Path.GetExtension(path);
        if (!IsCandidate(path) || BinaryOnly.Contains(extension)) return false;
        try
        {
            using var source = OpenSource(path);
            var bytes = await ReadAtMostAsync(source.Stream, SniffBytes, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return IsReadableSource(bytes, extension, source.Length);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        { return false; }
    }

    public static Task<SourcePreviewResult> LoadAsync(string path, CancellationToken token = default) =>
        Task.Run(() => LoadCoreAsync(path, token), token);

    private static async Task<SourcePreviewResult> LoadCoreAsync(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var extension = Path.GetExtension(path);
        if (!IsCandidate(path) || BinaryOnly.Contains(extension))
            throw new NotSupportedException("У этого формата нет текстового представления.");
        using var source = OpenSource(path);
        var bytes = await ReadAtMostAsync(source.Stream, MaxPreviewBytes + 1, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var truncated = bytes.Length > MaxPreviewBytes || source.Length > MaxPreviewBytes;
        if (bytes.Length > MaxPreviewBytes) Array.Resize(ref bytes, MaxPreviewBytes);
        if (!IsReadableSource(bytes, extension, source.Length))
            throw new NotSupportedException("Файл содержит двоичные данные, текстовый просмотр недоступен.");
        var text = FormatCatalog.DecodeText(bytes);
        var summary = source.Label + " · " + text.Length.ToString("N0", CultureInfo.CurrentCulture) + " символов";
        if (truncated) summary += " · показаны первые 2 МБ";
        return new SourcePreviewResult(text, summary, truncated);
    }

    private static bool IsReadableSource(byte[] bytes, string extension, long? sourceLength)
    {
        var sample = bytes.AsSpan();
        if (sample.StartsWith(BinaryFbxHeader) || sample.StartsWith(BinaryDxfHeader)) return false;
        if (extension.Equals(".stl", StringComparison.OrdinalIgnoreCase) && bytes.Length >= 84 && sourceLength.HasValue)
        {
            var triangles = BinaryPrimitives.ReadUInt32LittleEndian(sample.Slice(80, 4));
            if (84L + triangles * 50L == sourceLength.Value) return false;
        }
        string text;
        try { text = FormatCatalog.DecodeText(bytes); }
        catch (DecoderFallbackException) { return false; }
        if ((extension.Equals(".svgz", StringComparison.OrdinalIgnoreCase) ||
             extension.Equals(".3mf", StringComparison.OrdinalIgnoreCase) ||
             extension.Equals(".fcstd", StringComparison.OrdinalIgnoreCase)) && !text.TrimStart().StartsWith('<')) return false;
        if (extension.Equals(".ply", StringComparison.OrdinalIgnoreCase))
        {
            var header = text.AsSpan(0, Math.Min(text.Length, SniffBytes));
            if (header.IndexOf("format binary_".AsSpan(), StringComparison.OrdinalIgnoreCase) >= 0) return false;
        }
        // DecodeText understands BOM/UTF-16/UTF-32 and falls back to Windows-1251.
        // Validate characters after decoding so legitimate Unicode NUL bytes are retained.
        foreach (var character in text)
            if (character == '\0' || char.IsControl(character) && character is not ('\r' or '\n' or '\t' or '\f')) return false;
        return true;
    }

    private static async Task<byte[]> ReadAtMostAsync(Stream stream, int limit, CancellationToken token)
    {
        var bytes = new byte[limit];
        var length = 0;
        while (length < limit)
        {
            var count = await stream.ReadAsync(bytes.AsMemory(length, limit - length), token).ConfigureAwait(false);
            if (count == 0) break;
            length += count;
        }
        if (length != bytes.Length) Array.Resize(ref bytes, length);
        return bytes;
    }

    private static SourceInput OpenSource(string path)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: SniffBytes, useAsync: true);
        try
        {
            var extension = Path.GetExtension(path);
            if (extension.Equals(".svgz", StringComparison.OrdinalIgnoreCase))
                return new SourceInput(new GZipStream(file, CompressionMode.Decompress), null, null, "Исходный SVG/XML из SVGZ");
            if (extension.Equals(".3mf", StringComparison.OrdinalIgnoreCase) || extension.Equals(".fcstd", StringComparison.OrdinalIgnoreCase))
            {
                if (file.Length > MaxContainerBytes)
                    throw new NotSupportedException("Контейнер модели слишком большой для текстового предпросмотра.");
                var zip = new ZipArchive(file, ZipArchiveMode.Read);
                try
                {
                    if (zip.Entries.Count > 20_000)
                        throw new NotSupportedException("Контейнер модели содержит слишком много частей.");
                    var is3mf = extension.Equals(".3mf", StringComparison.OrdinalIgnoreCase);
                    var name = is3mf ? "3D/3dmodel.model" : "Document.xml";
                    var entry = zip.Entries.FirstOrDefault(part => part.FullName.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (entry is null) throw new NotSupportedException("В контейнере модели нет доступного исходного XML.");
                    return new SourceInput(entry.Open(), zip, entry.Length,
                        is3mf ? "XML модели внутри 3MF" : "XML документа внутри FCStd");
                }
                catch { zip.Dispose(); throw; }
            }
            return new SourceInput(file, null, file.Length, "Исходный текст");
        }
        catch { file.Dispose(); throw; }
    }

    private sealed class SourceInput : IDisposable
    {
        private readonly IDisposable? _container;
        public Stream Stream { get; }
        public long? Length { get; }
        public string Label { get; }
        public SourceInput(Stream stream, IDisposable? container, long? length, string label)
        { Stream = stream; _container = container; Length = length; Label = label; }
        public void Dispose() { try { Stream.Dispose(); } finally { _container?.Dispose(); } }
    }
}
