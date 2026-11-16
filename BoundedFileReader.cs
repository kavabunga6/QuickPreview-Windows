using System.IO;

namespace QuickPreview;

/// <summary>Bounds file opening and reads without occupying WPF or accumulating blocked SMB calls.</summary>
internal static class BoundedFileReader
{
    internal static async Task<byte[]> ReadAsync(string path, int maximumBytes, CancellationToken token)
    {
        var result = await FolderPreviewService.RunBoundedAsync<byte[]?>(workerToken =>
        {
            workerToken.ThrowIfCancellationRequested();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
            var bytes = new byte[(int)Math.Min(stream.Length, maximumBytes)];
            var total = 0;
            while (total < bytes.Length)
            {
                workerToken.ThrowIfCancellationRequested();
                var read = stream.Read(bytes, total, Math.Min(64 * 1024, bytes.Length - total));
                if (read == 0) break;
                total += read;
            }
            workerToken.ThrowIfCancellationRequested();
            if (total != bytes.Length) Array.Resize(ref bytes, total);
            return bytes;
        }, null, token);
        token.ThrowIfCancellationRequested();
        return result ?? throw new IOException("Чтение файла заняло слишком много времени. Откройте его в основном приложении.");
    }
}
