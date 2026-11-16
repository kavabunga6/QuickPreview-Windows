using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace QuickPreview;

internal sealed record FolderPreviewEntry(string Name, bool IsDirectory, long? Size);
internal sealed record FolderListing(IReadOnlyList<FolderPreviewEntry> Entries, bool IsTruncated, string? Error = null);
internal sealed record PreviewPathInfo(bool Exists, bool IsDirectory, long Length, DateTime LastWriteTime);

/// <summary>One bounded filesystem reader. A blocked SMB MoveNext cannot accumulate abandoned workers.</summary>
internal static class FolderPreviewService
{
    private const int MaximumEntries = 4096;
    private static readonly TimeSpan ScanBudget = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(2);
    private static readonly SemaphoreSlim Gate = new(1, 1);

    internal static Task<FolderListing> LoadAsync(string path, CancellationToken token) =>
        RunBoundedAsync(workerToken => ReadFolder(path, workerToken),
            new FolderListing(Array.Empty<FolderPreviewEntry>(), true, "Содержимое папки временно недоступно. Откройте её в Проводнике."), token);

    internal static Task<PreviewPathInfo?> ReadPathInfoAsync(string path, CancellationToken token) =>
        RunBoundedAsync<PreviewPathInfo?>(workerToken =>
        {
            workerToken.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(path);
                var attributes = info.Attributes;
                if (attributes == (FileAttributes)(-1)) return new(false, false, 0, DateTime.MinValue);
                var directory = (attributes & FileAttributes.Directory) != 0;
                var length = directory ? 0 : info.Length;
                var modified = info.LastWriteTime;
                workerToken.ThrowIfCancellationRequested();
                return new(true, directory, length, modified);
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
            { return new(false, false, 0, DateTime.MinValue); }
        }, null, token);

    internal static async Task<IReadOnlyList<string>> NavigationAsync(string path, CancellationToken token)
    {
        var parent = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(parent)) return new[] { path };
        return await RunBoundedAsync(workerToken => ReadNavigation(parent, path, workerToken), (IReadOnlyList<string>)new[] { path }, token);
    }

    internal static async Task<T> RunBoundedAsync<T>(Func<CancellationToken, T> action, T unavailable,
        CancellationToken token, TimeSpan? waitBudget = null)
    {
        token.ThrowIfCancellationRequested();
        var timeout = waitBudget ?? WaitBudget;
        if (!await Gate.WaitAsync(timeout, token).ConfigureAwait(false)) return unavailable;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var workerToken = cancellation.Token;
        var task = Task.Run(() => action(workerToken), CancellationToken.None);
        try { return await task.WaitAsync(timeout, token).ConfigureAwait(false); }
        catch (TimeoutException) { cancellation.Cancel(); return unavailable; }
        finally
        {
            cancellation.Cancel();
            // Directory enumeration/metadata may be stuck inside an OS call.
            // Hold the gate until that actual call exits, not just until the UI's
            // wait is canceled. Subsequent requests wait asynchronously and time out.
            if (task.IsCompleted)
            {
                _ = task.Exception;
                Gate.Release();
            }
            else _ = task.ContinueWith(completed =>
            {
                _ = completed.Exception;
                Gate.Release();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private static FolderListing ReadFolder(string path, CancellationToken token)
    {
        var entries = new List<FolderPreviewEntry>();
        var watch = Stopwatch.StartNew();
        var truncated = false;
        using var iterator = new DirectoryInfo(path).EnumerateFileSystemInfos().GetEnumerator();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (entries.Count >= MaximumEntries || watch.Elapsed >= ScanBudget) { truncated = true; break; }
            if (!iterator.MoveNext()) break;
            token.ThrowIfCancellationRequested();
            var entry = iterator.Current;
            long? length = null;
            if (entry is FileInfo file)
                try { length = file.Length; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            entries.Add(new(entry.Name, entry is DirectoryInfo, length));
        }
        var sorted = entries.OrderByDescending(entry => entry.IsDirectory)
            .ThenBy(entry => entry.Name, NaturalComparer.Instance).Take(24).ToArray();
        token.ThrowIfCancellationRequested();
        return new(sorted, truncated || entries.Count > 24);
    }

    private static IReadOnlyList<string> ReadNavigation(string parent, string current, CancellationToken token)
    {
        var paths = new List<string>();
        var watch = Stopwatch.StartNew();
        try
        {
            using var iterator = Directory.EnumerateFiles(parent).GetEnumerator();
            while (paths.Count < MaximumEntries && watch.Elapsed < ScanBudget)
            {
                token.ThrowIfCancellationRequested();
                if (!iterator.MoveNext()) break;
                paths.Add(iterator.Current);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        token.ThrowIfCancellationRequested();
        if (!paths.Contains(current, StringComparer.OrdinalIgnoreCase))
        {
            if (paths.Count >= MaximumEntries) paths.RemoveAt(paths.Count - 1);
            paths.Add(current);
        }
        return paths.OrderBy(candidate => Path.GetFileName(candidate) ?? string.Empty, NaturalComparer.Instance).ToArray();
    }

    private sealed class NaturalComparer : IComparer<string>
    {
        internal static readonly NaturalComparer Instance = new();
        public int Compare(string? first, string? second) => StrCmpLogicalW(first ?? "", second ?? "");
    }
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] private static extern int StrCmpLogicalW(string first, string second);
}
