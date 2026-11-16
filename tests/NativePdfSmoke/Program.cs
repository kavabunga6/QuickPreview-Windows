using System.IO;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;
using QuickPreview;

var path = Path.Combine(AppContext.BaseDirectory, "preview.pdf");
var document = await NativePdfPreview.OpenAsync(path, CancellationToken.None);
Assert(document.PageCount == 2, "Native PDF page count");
var first = await document.RenderAsync(0, CancellationToken.None);
var second = await document.RenderAsync(1, CancellationToken.None);
Assert(first.IsFrozen && second.IsFrozen, "Rendered pages can cross threads");
Assert(first.PixelWidth > 0 && first.PixelHeight > 0 && Math.Max(first.PixelWidth, first.PixelHeight) <= 1800,
    "First page dimensions bounded");
Assert(second.PixelWidth > 0 && second.PixelHeight > 0 && Math.Max(second.PixelWidth, second.PixelHeight) <= 1800,
    "Second page dimensions bounded");
Assert(first.PixelWidth < first.PixelHeight && second.PixelWidth > second.PixelHeight,
    "Portrait and landscape orientation preserved");
Assert(!Hash(first).SequenceEqual(Hash(second)), "Distinct PDF pages render distinct pixels");

using var cancellation = new CancellationTokenSource();
cancellation.Cancel();
try
{
    await NativePdfPreview.OpenAsync(path, cancellation.Token);
    throw new Exception("Cancelled native PDF load did not cancel.");
}
catch (OperationCanceledException) { Console.WriteLine("PASS: Native PDF cancellation"); }

static byte[] Hash(BitmapSource source)
{
    var stride = (source.PixelWidth * source.Format.BitsPerPixel + 7) / 8;
    var pixels = new byte[stride * source.PixelHeight];
    source.CopyPixels(pixels, stride, 0);
    return SHA256.HashData(pixels);
}

static void Assert(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
}
