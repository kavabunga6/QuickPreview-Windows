using System.IO;

namespace QuickPreview;

internal static class NativeImagePreviewService
{
    public static Task<RenderedPreview> LoadAsync(string path, CancellationToken token) =>
        IsolatedPreviewWorker.RenderAsync("native-image", path, token);
}

internal static class ExtendedImagePreviewService
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".psd", ".psb", ".heic", ".heif", ".avif", ".tga", ".dds", ".exr", ".hdr", ".jxl",
        ".jp2", ".j2k", ".jpf", ".jpx", ".pcx", ".pbm", ".pgm", ".ppm", ".pnm", ".pam", ".qoi",
        ".dng", ".cr2", ".cr3", ".nef", ".arw", ".raf", ".orf", ".rw2", ".pef", ".srw",
        ".xcf", ".kra", ".ora", ".webp", ".jpe", ".jfif", ".jxr", ".hdp", ".cur", ".icns", ".apng"
    };
    public static bool Supports(string path) => Extensions.Contains(Path.GetExtension(path));
    public static Task<RenderedPreview> LoadAsync(string path, CancellationToken token) =>
        IsolatedPreviewWorker.RenderAsync("image", path, token);
}

internal static class DrawingPreviewService
{
    public static bool Supports(string path) => Path.GetExtension(path).ToLowerInvariant() is ".dxf" or ".dwg";
    public static Task<RenderedPreview> LoadAsync(string path, CancellationToken token) =>
        IsolatedPreviewWorker.RenderAsync("drawing", path, token);
}
