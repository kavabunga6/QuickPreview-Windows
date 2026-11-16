using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using FontFamily = System.Windows.Media.FontFamily;

namespace QuickPreview;

public partial class MainWindow
{
    private async Task LoadNativeImageAsync(string path, CancellationToken token)
    {
        var preview = await NativeImagePreviewService.LoadAsync(path, token);
        token.ThrowIfCancellationRequested();
        ShowBitmap(preview.Image, preview.Summary);
    }

    private async Task BuildNavigationAsync(string path, CancellationToken token)
    {
        _navigationPaths.Clear(); _navigationIndex = -1;
        var paths = await FolderPreviewService.NavigationAsync(path, token);
        token.ThrowIfCancellationRequested();
        _navigationPaths.AddRange(paths);
        _navigationIndex = _navigationPaths.FindIndex(candidate => string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase));
    }

    private async Task LoadFolderPreviewAsync(string path, CancellationToken token)
    {
        var listing = await FolderPreviewService.LoadAsync(path, token);
        token.ThrowIfCancellationRequested();
        if (!string.Equals(_currentPath, path, StringComparison.OrdinalIgnoreCase)) return;
        var rows = listing.Entries.Select(entry => entry.IsDirectory ? $"📁  {entry.Name}" :
            $"     {entry.Name}   ·   {(entry.Size is { } bytes ? FormatFileSize(bytes) : "—")}");
        var summary = listing.Error ?? (listing.Entries.Count == 0 ? "Папка пуста" : string.Join(Environment.NewLine, rows));
        if (listing.IsTruncated && listing.Error is null)
            summary += "\n\n… Показана часть содержимого папки.";
        InfoThumbnail.Source = null;
        InfoTitle.Text = new DirectoryInfo(path).Name;
        InfoDetails.FontFamily = new FontFamily("Cascadia Mono,Consolas");
        InfoDetails.TextAlignment = TextAlignment.Left;
        InfoDetails.Text = summary;
        FileMetaText.Text = "Папка";
        ShowOnly(InfoView);
        if (listing.Error is not null) return;
        try
        {
            var thumbnail = await IsolatedPreviewWorker.RenderThumbnailAsync(path, token);
            token.ThrowIfCancellationRequested();
            if (string.Equals(_currentPath, path, StringComparison.OrdinalIgnoreCase)) InfoThumbnail.Source = thumbnail.Image;
        }
        catch (Exception error) when (error is NotSupportedException or IOException or System.ComponentModel.Win32Exception)
        { Debug.WriteLine($"QuickPreview folder thumbnail: {error.GetType().Name}"); }
    }
}
