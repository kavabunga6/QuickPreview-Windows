using System.IO;
using System.IO.Compression;
using System.Text.Json;
using ImageMagick;
using ImageMagick.Configuration;
using ImageMagick.Formats;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuickPreview;

internal static class PreviewWorker
{
    public static int Run(string[] arguments)
    {
        if (arguments.Length != 3 || arguments[0] is not ("image" or "native-image" or "thumbnail" or "drawing")) return 2;
        var outputDirectory = Path.GetFullPath(arguments[2]);
        if (!Directory.Exists(outputDirectory)) return 2;
        PreviewWorkerResult result;
        try
        {
            var path = Path.GetFullPath(arguments[1]);
            if (arguments[0] != "thumbnail" && new FileInfo(path).Length > 256L * 1024 * 1024)
                throw new NotSupportedException("Файл больше 256 МБ. Откройте его в профильном приложении.");
            result = arguments[0] switch
            {
                "drawing" => CadDrawingRenderer.Render(path, Path.Combine(outputDirectory, "preview.png")),
                "thumbnail" => RenderThumbnail(path, outputDirectory),
                _ => RenderImage(path, outputDirectory, arguments[0] == "native-image")
            };
        }
        catch (Exception exception)
        {
            result = new PreviewWorkerResult(false, string.Empty,
                exception is NotSupportedException ? exception.Message : "Не удалось прочитать изображение или чертёж. Файл может быть повреждён или использовать неподдерживаемую версию формата.");
        }
        File.WriteAllText(Path.Combine(outputDirectory, "result.json"), JsonSerializer.Serialize(result));
        return result.Success ? 0 : 1;
    }

    private static PreviewWorkerResult RenderImage(string path, string outputDirectory, bool preserveResolution)
    {
        var configuration = ConfigurationFiles.Default;
        configuration.Policy.Data = @"
            <policymap>
              <policy domain=""delegate"" rights=""none"" pattern=""*"" />
              <policy domain=""filter"" rights=""none"" pattern=""*"" />
              <policy domain=""path"" rights=""none"" pattern=""@*"" />
              <policy domain=""coder"" rights=""none"" pattern=""*"" />
              <policy domain=""coder"" rights=""read|write"" pattern=""{PNG,APNG,JPEG,JPG,JPE,GIF,BMP,BMP2,BMP3,TIFF,TIF,WEBP,ICO,CUR,ICON,ICNS,PSD,PSB,HEIC,HEIF,AVIF,TGA,DDS,EXR,HDR,JXL,JP2,J2K,JPC,PCX,PBM,PGM,PPM,PNM,PAM,QOI,DNG,CR2,CR3,NEF,ARW,RAF,ORF,RW2,PEF,SRW,XCF,JXR,WDP,DCM}"" />
            </policymap>
            ";
        var configurationPath = Path.Combine(outputDirectory, "decoder-config");
        Directory.CreateDirectory(configurationPath);
        MagickNET.Initialize(configuration, configurationPath);
        ResourceLimits.Memory = (preserveResolution ? 512UL : 256UL) * 1024 * 1024;
        ResourceLimits.Disk = 0;
        ResourceLimits.Width = 32768;
        ResourceLimits.Height = 32768;
        ResourceLimits.ListLength = 256;
        ResourceLimits.Thread = 2;
        var settings = new MagickReadSettings { FrameIndex = 0, FrameCount = 1 };
        // These raster formats have weak/no magic signatures; identify by the selected
        // file's extension while still passing a stream rather than a Magick filename.
        var extension = Path.GetExtension(path).TrimStart('.');
        if (extension.Equals("tga", StringComparison.OrdinalIgnoreCase)) settings.Format = MagickFormat.Tga;
        if (preserveResolution)
        {
            try { return RenderNativeImage(path, outputDirectory, settings); }
            catch (MagickException)
            {
                // Windows WIC codecs (including ICO/DIB/JPEG-XR) remain usable inside the
                // same disposable, memory/time-limited decoder process.
                return RenderNativeWic(path, outputDirectory);
            }
        }
        using var stream = OpenImageStream(path);
        using var image = new MagickImage(stream, settings);
        image.AutoOrient();
        var width = image.Width; var height = image.Height;
        if (width == 0 || height == 0) throw new NotSupportedException("Изображение не содержит пикселей.");
        image.Resize(new MagickGeometry(2400, 2400) { Greater = true });
        image.Strip();
        image.Format = MagickFormat.Png;
        image.Write(Path.Combine(outputDirectory, "preview.png"));
        return new PreviewWorkerResult(true, $"{width} × {height} · {Path.GetExtension(path).TrimStart('.').ToUpperInvariant()}");
    }

    private static PreviewWorkerResult RenderNativeImage(string path, string directory, MagickReadSettings settings)
    {
        using var input = OpenImageStream(path);
        using var headers = new MagickImageCollection();
        // Ping does not allocate the full raster. It also bounds multi-frame
        // containers before selecting the first frame (or the largest ICO).
        var headerSettings = new MagickReadSettings();
        if (Path.GetExtension(path).Equals(".ico", StringComparison.OrdinalIgnoreCase))
            headerSettings.Format = settings.Format = MagickFormat.Ico;
        headers.Ping(input, headerSettings);
        if (headers.Count is 0 or > 256) throw new NotSupportedException("В изображении слишком много кадров для быстрого предпросмотра.");
        var index = Path.GetExtension(path).Equals(".ico", StringComparison.OrdinalIgnoreCase)
            ? Enumerable.Range(0, headers.Count).OrderByDescending(i => (ulong)headers[i].Width * headers[i].Height).First() : 0;
        var header = headers[index];
        ValidateNativeDimensions(header.Width, header.Height);
        var originalWidth = header.Width; var originalHeight = header.Height;
        var pixels = (ulong)originalWidth * originalHeight;
        settings.FrameIndex = (uint)index; settings.FrameCount = 1;
        if (header.Format == MagickFormat.Jpeg && pixels > 16_000_000)
        {
            var scale = Math.Sqrt(16_000_000d / pixels);
            settings.Defines = new JpegReadDefines { Size = new MagickGeometry((uint)(originalWidth * scale), (uint)(originalHeight * scale)) };
        }
        input.Position = 0;
        using var image = new MagickImage(input, settings);
        image.AutoOrient();
        ValidateNativeDimensions(image.Width, image.Height);
        var orientedWidth = image.Width; var orientedHeight = image.Height;
        var ratio = Math.Min(1, Math.Min(Math.Sqrt(16_000_000d / ((double)image.Width * image.Height)),
            16384d / Math.Max(image.Width, image.Height)));
        if (ratio < 1) image.Resize((uint)Math.Max(1, Math.Floor(image.Width * ratio)), (uint)Math.Max(1, Math.Floor(image.Height * ratio)));
        image.Strip(); image.Format = MagickFormat.Png;
        image.Write(Path.Combine(directory, "preview.png"));
        var reduced = pixels > 16_000_000 || ratio < 1;
        return new PreviewWorkerResult(true, $"{(reduced ? originalWidth : orientedWidth)} × {(reduced ? originalHeight : orientedHeight)}" +
            (reduced ? $" · уменьшено для предпросмотра до {image.Width} × {image.Height}" : string.Empty));
    }

    private static void ValidateNativeDimensions(uint width, uint height)
    {
        if (width == 0 || height == 0 || width > 32768 || height > 32768 || (ulong)width * height > 64_000_000)
            throw new NotSupportedException("Изображение слишком большое для быстрого предпросмотра (более 64 Мп или 32768 пикселей по стороне).");
    }

    private static PreviewWorkerResult RenderNativeWic(string path, string directory)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        if (decoder.Frames.Count is 0 or > 256) throw new NotSupportedException("В изображении слишком много кадров для быстрого предпросмотра.");
        var frame = decoder.Frames.OrderByDescending(item => (long)item.PixelWidth * item.PixelHeight).First();
        ValidateNativeDimensions((uint)frame.PixelWidth, (uint)frame.PixelHeight);
        ushort orientation = 1;
        if (frame.Metadata is BitmapMetadata metadata)
            try { if (metadata.GetQuery("/app1/ifd/{ushort=274}") is { } value) orientation = Convert.ToUInt16(value); }
            catch (Exception error) when (error is NotSupportedException or InvalidOperationException or FormatException) { }
        var transforms = new TransformGroup();
        switch (orientation)
        {
            case 2: transforms.Children.Add(new ScaleTransform(-1, 1)); break;
            case 3: transforms.Children.Add(new RotateTransform(180)); break;
            case 4: transforms.Children.Add(new ScaleTransform(1, -1)); break;
            case 5: transforms.Children.Add(new ScaleTransform(-1, 1)); transforms.Children.Add(new RotateTransform(270)); break;
            case 6: transforms.Children.Add(new RotateTransform(90)); break;
            case 7: transforms.Children.Add(new ScaleTransform(-1, 1)); transforms.Children.Add(new RotateTransform(90)); break;
            case 8: transforms.Children.Add(new RotateTransform(270)); break;
        }
        BitmapSource oriented = transforms.Children.Count == 0 ? frame : new TransformedBitmap(frame, transforms);
        var ratio = Math.Min(1, Math.Min(Math.Sqrt(16_000_000d / ((double)oriented.PixelWidth * oriented.PixelHeight)),
            16384d / Math.Max(oriented.PixelWidth, oriented.PixelHeight)));
        if (ratio < 1) ratio *= 0.9999; // WIC rounds transformed dimensions up.
        BitmapSource image = ratio < 1 ? new TransformedBitmap(oriented, new ScaleTransform(ratio, ratio)) : oriented;
        WritePng(image, Path.Combine(directory, "preview.png"));
        return new PreviewWorkerResult(true, $"{oriented.PixelWidth} × {oriented.PixelHeight}" +
            (ratio < 1 ? $" · уменьшено для предпросмотра до {image.PixelWidth} × {image.PixelHeight}" : string.Empty));
    }

    private static PreviewWorkerResult RenderThumbnail(string path, string directory)
    {
        var image = ShellThumbnailService.TryGetThumbnail(path, 256);
        if (image is null) return new PreviewWorkerResult(false, string.Empty, "Эскиз папки недоступен.");
        if ((long)image.PixelWidth * image.PixelHeight > 1_000_000) throw new NotSupportedException("Эскиз слишком большой.");
        WritePng(image, Path.Combine(directory, "preview.png"));
        return new PreviewWorkerResult(true, "Эскиз папки");
    }

    private static void WritePng(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path); encoder.Save(output);
    }

    private static Stream OpenImageStream(string path)
    {
        if (Path.GetExtension(path).ToLowerInvariant() is not (".kra" or ".ora"))
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var file = File.OpenRead(path);
        using var package = new ZipArchive(file, ZipArchiveMode.Read);
        var entry = package.GetEntry("mergedimage.png") ?? package.GetEntry("Thumbnails/thumbnail.png");
        if (entry is null || entry.Length > 32 * 1024 * 1024)
            throw new NotSupportedException("В документе нет доступного объединённого изображения.");
        using var input = entry.Open();
        var data = new MemoryStream(); input.CopyTo(data); data.Position = 0;
        return data;
    }
}
