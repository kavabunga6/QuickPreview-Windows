using System.IO;
using System.Windows.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace QuickPreview;

// Windows' PDF renderer also works when Edge/WebView2 is unavailable.
internal sealed class NativePdfPreview
{
    private readonly PdfDocument _document;
    public uint PageCount => _document.PageCount;

    private NativePdfPreview(PdfDocument document) => _document = document;

    public static async Task<NativePdfPreview> OpenAsync(string path, CancellationToken cancellationToken)
    {
        var file = await StorageFile.GetFileFromPathAsync(path).AsTask(cancellationToken);
        var document = await PdfDocument.LoadFromFileAsync(file).AsTask(cancellationToken);
        return new NativePdfPreview(document);
    }

    public async Task<BitmapSource> RenderAsync(uint pageIndex, CancellationToken cancellationToken)
    {
        using var page = _document.GetPage(pageIndex);
        using var buffer = new InMemoryRandomAccessStream();
        var size = page.Size;
        var scale = Math.Min(2, 1800 / Math.Max(size.Width, size.Height));
        var options = new PdfPageRenderOptions
        {
            DestinationWidth = (uint)Math.Max(1, Math.Round(size.Width * scale)),
            DestinationHeight = (uint)Math.Max(1, Math.Round(size.Height * scale))
        };
        await page.RenderToStreamAsync(buffer, options).AsTask(cancellationToken);
        using var stream = buffer.AsStreamForRead();
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
