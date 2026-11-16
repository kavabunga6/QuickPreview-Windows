using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using CSMath;
using ImageMagick;
using QuickPreview;

internal static class Program
{
    [STAThread]
    private static int Main() { try { RunAsync().GetAwaiter().GetResult(); return 0; } catch (Exception e) { Console.Error.WriteLine(e); return 1; } }

    private static async Task RunAsync()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "fixtures");
        Directory.CreateDirectory(directory);
        var formats = new[] { ("psd", MagickFormat.Psd), ("tga", MagickFormat.Tga), ("dds", MagickFormat.Dds),
            ("exr", MagickFormat.Exr), ("hdr", MagickFormat.Hdr),
            ("avif", MagickFormat.Avif), ("jp2", MagickFormat.Jp2), ("jxl", MagickFormat.Jxl),
            ("qoi", MagickFormat.Qoi), ("ppm", MagickFormat.Ppm), ("pcx", MagickFormat.Pcx), ("webp", MagickFormat.WebP) };
        foreach (var (extension, format) in formats)
        {
            var path = Path.Combine(directory, "raster." + extension);
            using (var image = new MagickImage(MagickColors.OrangeRed, 128, 96)) image.Write(path, format);
            var hash = Hash(path);
            var result = await IsolatedPreviewWorker.RenderAsync("image", path, CancellationToken.None);
            Check(result.Image.PixelWidth == 128 && result.Image.PixelHeight == 96, extension + " real isolated decoder");
            Check(Hash(path) == hash, extension + " input unchanged");
        }
        var heicPath = Path.Combine(directory, "example.heic");
        var heic = await IsolatedPreviewWorker.RenderAsync("image", heicPath, CancellationToken.None);
        Check(heic.Image.PixelWidth > 0 && heic.Image.PixelHeight > 0, "HEIC official libheif fixture decoded");
        foreach (var extension in new[] { "kra", "ora" })
        {
            var path = Path.Combine(directory, "raster." + extension);
            if (File.Exists(path)) File.Delete(path);
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            using (var stream = zip.CreateEntry("mergedimage.png").Open())
            using (var image = new MagickImage(MagickColors.DodgerBlue, 150, 110)) image.Write(stream, MagickFormat.Png);
            var result = await IsolatedPreviewWorker.RenderAsync("image", path, CancellationToken.None);
            Check(result.Image.PixelWidth == 150 && result.Image.PixelHeight == 110, extension + " merged image");
        }
        var document = Drawing(1);
        foreach (var extension in new[] { "dxf", "dwg" })
        {
            var path = Path.Combine(directory, "drawing." + extension);
            if (extension == "dxf") DxfWriter.Write(path, document); else DwgWriter.Write(path, document);
            var hash = Hash(path);
            var result = await IsolatedPreviewWorker.RenderAsync("drawing", path, CancellationToken.None);
            Check(result.Image.PixelWidth == 1800 && result.Summary.Contains("объектов"), extension + " plan rendered");
            var bytes = new byte[result.Image.PixelHeight * result.Image.PixelWidth * 4];
            result.Image.CopyPixels(bytes, result.Image.PixelWidth * 4, 0);
            Check(bytes.Count(b => b < 200) > 2000, extension + " geometry contains actual strokes");
            Check(Hash(path) == hash, extension + " drawing unchanged");
            Save(result.Image, Path.Combine(directory, "drawing-" + extension + ".png"));
            var scaledPath = Path.Combine(directory, "drawing-large-units." + extension);
            if (extension == "dxf") DxfWriter.Write(scaledPath, Drawing(100_000));
            else DwgWriter.Write(scaledPath, Drawing(100_000));
            var scaledHash = Hash(scaledPath);
            var scaled = await IsolatedPreviewWorker.RenderAsync("drawing", scaledPath, CancellationToken.None);
            var scaledBytes = new byte[scaled.Image.PixelHeight * scaled.Image.PixelWidth * 4];
            scaled.Image.CopyPixels(scaledBytes, scaled.Image.PixelWidth * 4, 0);
            Check(scaledBytes.SequenceEqual(bytes), extension + " equivalent large CAD units preserve text and geometry pixel-for-pixel");
            Check(Hash(scaledPath) == scaledHash, extension + " large-unit drawing unchanged");
        }
        var corrupt = Path.Combine(directory, "broken.dwg"); File.WriteAllText(corrupt, "not a drawing");
        try { await IsolatedPreviewWorker.RenderAsync("drawing", corrupt, CancellationToken.None); throw new Exception("Corrupt input accepted"); }
        catch (NotSupportedException) { Console.WriteLine("PASS: corrupt drawing bounded error"); }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await IsolatedPreviewWorker.RenderAsync("image", corrupt, cancelled.Token); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { Console.WriteLine("PASS: isolated worker cancellation"); }
        Console.WriteLine("PASS: graphics isolated process, formats and CAD fixtures.");
    }
    private static CadDocument Drawing(double units)
    {
        var document = new CadDocument();
        document.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(120 * units, 0, 0)));
        document.Entities.Add(new Line(new XYZ(120 * units, 0, 0), new XYZ(120 * units, 80 * units, 0)));
        document.Entities.Add(new Circle { Center = new XYZ(60 * units, 40 * units, 0), Radius = 25 * units });
        document.Entities.Add(new TextEntity { InsertPoint = new XYZ(8 * units, 75 * units, 0), Height = 5 * units, Value = "QuickPreview CAD" });
        return document;
    }
    private static string Hash(string path) { using var stream = File.OpenRead(path); using var hash = SHA256.Create(); return Convert.ToHexString(hash.ComputeHash(stream)); }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine("PASS: " + message); }
    private static void Save(BitmapSource image, string path) { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(path); encoder.Save(stream); }
}
