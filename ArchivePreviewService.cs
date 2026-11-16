using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace QuickPreview;

internal sealed record ArchivePreviewEntry(
    string Path, long? Size, bool IsDirectory, DateTime? Modified, bool IsEncrypted);

internal sealed record ArchivePreviewResult(
    IReadOnlyList<ArchivePreviewEntry> Entries, string Summary, bool IsTruncated, string? Error = null);

internal static class ArchivePreviewService
{
    private const int MaxEntries = 1_000;
    private static readonly TimeSpan PreviewTimeout = TimeSpan.FromSeconds(8);
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".zipx", ".7z", ".rar", ".tar", ".gz", ".gzip", ".tgz",
        ".bz2", ".bzip2", ".tbz", ".tbz2", ".xz", ".txz", ".lz", ".tlz",
        ".zst", ".zstd", ".tzst", ".jar", ".war", ".ear", ".apk", ".cbz", ".cbr"
    };

    public static bool Supports(string path) => Extensions.Contains(System.IO.Path.GetExtension(path));

    public static Task<ArchivePreviewResult> LoadAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() => Load(path, cancellationToken), cancellationToken);

    private static ArchivePreviewResult Load(string path, CancellationToken cancellationToken)
    {
        var entries = new List<ArchivePreviewEntry>();
        var truncated = false;
        string? error = null;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var stream = new PreviewReadStream(file, cancellationToken, stopwatch);
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var options = new ReaderOptions
            {
                LeaveStreamOpen = true,
                // Legacy ZIP names use the system OEM code page (e.g. CP866 on Russian
                // Windows). Explicit UTF-8 names in modern archives retain their own encoding.
                ArchiveEncoding = new ArchiveEncoding
                {
                    Default = IsZipContainer(path)
                        ? Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage)
                        : Encoding.UTF8
                }
            };

            // Random-access metadata avoids decompressing ZIP/7z/RAR payloads just to list names.
            // TAR wrappers need the forward-only reader, which recognises e.g. tar.gz and tar.xz.
            if (IsCompressedStream(path))
            {
                using var reader = ReaderFactory.OpenReader(stream, options);
                while (reader.MoveToNextEntry())
                {
                    CheckBudget();
                    if (entries.Count == MaxEntries)
                    {
                        truncated = true;
                        break;
                    }
                    entries.Add(CreateEntry(reader.Entry, path));
                }
            }
            else
            {
                using var archive = ArchiveFactory.OpenArchive(stream, options);
                foreach (var entry in archive.Entries)
                {
                    CheckBudget();
                    if (entries.Count == MaxEntries)
                    {
                        truncated = true;
                        break;
                    }
                    entries.Add(CreateEntry(entry, path));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PreviewLimitException)
        {
            truncated = true;
            if (entries.Count == 0)
                error = "Архив слишком большой или сложный для быстрого предпросмотра. Откройте его в архиваторе.";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException && exception is not StackOverflowException)
        {
            // Some formats hide their entire directory behind a password. Do not prompt for,
            // cache, or guess passwords during an otherwise passive Explorer preview.
            truncated = entries.Count > 0;
            error = IsPasswordError(exception)
                ? "Список файлов зашифрован. Откройте архив в архиваторе и введите пароль."
                : "Не удалось прочитать список файлов: архив повреждён, недоступен или использует неподдерживаемый метод сжатия.";
        }

        var files = entries.Count(entry => !entry.IsDirectory);
        var folders = entries.Count - files;
        var encrypted = entries.Count(entry => entry.IsEncrypted);
        var summary = entries.Count == 0 && error is null
            ? "Архив пуст"
            : $"Файлов: {files:N0} · папок: {folders:N0}";
        if (truncated)
            summary += $" · показано первых {entries.Count:N0} элементов";
        if (encrypted > 0)
            summary += $" · зашифрованных: {encrypted:N0}";

        return new ArchivePreviewResult(entries.AsReadOnly(), summary, truncated, error);

        void CheckBudget()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stopwatch.Elapsed > PreviewTimeout)
                throw new PreviewLimitException();
        }
    }

    private static bool IsCompressedStream(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() is
        ".gz" or ".gzip" or ".tgz" or ".bz2" or ".bzip2" or ".tbz" or ".tbz2" or
        ".xz" or ".txz" or ".lz" or ".tlz" or ".zst" or ".zstd" or ".tzst";

    private static bool IsZipContainer(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() is
        ".zip" or ".zipx" or ".jar" or ".war" or ".ear" or ".apk" or ".cbz";

    private static ArchivePreviewEntry CreateEntry(IEntry entry, string archivePath)
    {
        var name = entry.Key;
        if (string.IsNullOrWhiteSpace(name))
            name = System.IO.Path.GetFileNameWithoutExtension(archivePath);
        // Preserve the complete relative path while preventing control characters from
        // forging rows. Names are display data only and are never resolved on disk.
        name = new string(name.Take(4_096).Select(character => char.IsControl(character) ? ' ' : character).ToArray());
        return new ArchivePreviewEntry(name.Replace('\\', '/'), entry.IsDirectory ? null : Math.Max(0, entry.Size),
            entry.IsDirectory, entry.LastModifiedTime, entry.IsEncrypted);
    }

    private static bool IsPasswordError(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SharpCompress.Common.CryptographicException or System.Security.Cryptography.CryptographicException ||
                current.GetType().Name.Contains("Password", StringComparison.OrdinalIgnoreCase) ||
                current.Message.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                current.Message.Contains("encrypted", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private sealed class PreviewLimitException : IOException { }

    // Checks time/cancellation even while a library is parsing an archive's header or
    // skipping entries. A byte budget also bounds work before the first row is available.
    private sealed class PreviewReadStream : Stream
    {
        private const long MaxReadBytes = 64L * 1024 * 1024;
        private readonly Stream _inner;
        private readonly CancellationToken _cancellationToken;
        private readonly Stopwatch _stopwatch;
        private long _bytesRead;

        public PreviewReadStream(Stream inner, CancellationToken cancellationToken, Stopwatch stopwatch)
        {
            _inner = inner;
            _cancellationToken = cancellationToken;
            _stopwatch = stopwatch;
        }

        public override bool CanRead => true;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set { CheckBudget(); _inner.Position = value; } }
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) { CheckBudget(); return _inner.Seek(offset, origin); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            CheckBudget();
            var read = _inner.Read(buffer, offset, (int)Math.Min(count, MaxReadBytes - _bytesRead));
            _bytesRead += read;
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            CheckBudget();
            var read = _inner.Read(buffer[..(int)Math.Min(buffer.Length, MaxReadBytes - _bytesRead)]);
            _bytesRead += read;
            return read;
        }

        public override int ReadByte()
        {
            CheckBudget();
            var value = _inner.ReadByte();
            if (value >= 0)
                _bytesRead++;
            return value;
        }

        private void CheckBudget()
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (_bytesRead >= MaxReadBytes || _stopwatch.Elapsed > PreviewTimeout)
                throw new PreviewLimitException();
        }
    }
}
