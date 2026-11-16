using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using SharpVectors.Converters;
using SharpVectors.Renderers.Wpf;

namespace QuickPreview;

/// <summary>Renders a sanitized SVG entirely from memory, without external resources or scripts.</summary>
internal static class SvgPreviewService
{
    private const int MaxBytes = 8 * 1024 * 1024;
    private static readonly HashSet<string> ExcludedElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "foreignObject", "image", "feImage", "iframe", "object", "embed", "audio", "video",
        "animate", "animateTransform", "animateMotion", "set", "handler", "listener"
    };
    private static readonly Regex CssComments = new(@"/\*.*?\*/", RegexOptions.Singleline, TimeSpan.FromMilliseconds(200));
    private static readonly Regex CssUrls = new(@"url\s*\([^)]*\)", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
    private static readonly Regex LocalCssUrl = new("^url\\s*\\(\\s*(['\"]?)#[A-Za-z_][A-Za-z0-9_.:-]*\\1\\s*\\)$",
        RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));

    public static bool Supports(string path) => Path.GetExtension(path).Equals(".svg", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".svgz", StringComparison.OrdinalIgnoreCase);

    public static Task<BitmapSource> LoadAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() => Render(path, cancellationToken), cancellationToken);

    private static BitmapSource Render(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (file.Length > MaxBytes) throw new NotSupportedException("SVG слишком большой для быстрого предпросмотра (более 8 МБ).");
        using var gzip = Path.GetExtension(path).Equals(".svgz", StringComparison.OrdinalIgnoreCase)
            ? new GZipStream(file, CompressionMode.Decompress, leaveOpen: true) : null;
        var document = SafeXmlDocument.Load(gzip ?? (Stream)file, MaxBytes, token);
        token.ThrowIfCancellationRequested();
        var root = document.Root;
        if (root is null || root.Name.LocalName != "svg" || root.Name.NamespaceName != "http://www.w3.org/2000/svg")
            throw new InvalidDataException("Файл не содержит изображение SVG.");
        var elements = root.DescendantsAndSelf().Take(25_001).ToList();
        if (elements.Count > 25_000) throw new NotSupportedException("SVG содержит слишком много элементов для быстрого предпросмотра.");
        foreach (var element in elements)
        {
            token.ThrowIfCancellationRequested();
            if (element != root && (element.Name.NamespaceName != root.Name.NamespaceName || ExcludedElements.Contains(element.Name.LocalName)))
            { element.Remove(); continue; }
            if (element.Name.LocalName == "style")
            {
                var css = SafeCss(element.Value);
                if (css is null) { element.Remove(); continue; }
                element.Value = css;
            }
            foreach (var attribute in element.Attributes().ToList())
            {
                if (attribute.IsNamespaceDeclaration) continue;
                var name = attribute.Name.LocalName;
                if (name.StartsWith("on", StringComparison.OrdinalIgnoreCase) || name.Equals("base", StringComparison.OrdinalIgnoreCase))
                { attribute.Remove(); continue; }
                if (name.Equals("href", StringComparison.OrdinalIgnoreCase))
                {
                    if (!attribute.Value.StartsWith('#')) attribute.Remove();
                    continue;
                }
                var safe = SafeCss(attribute.Value);
                if (safe is null) attribute.Remove();
                else attribute.Value = safe;
            }
        }
        RejectReferenceCycles(root, token);
        // XmlReader over a string has no base URI; only fragment references survive sanitization.
        using var sanitized = document.CreateReader();
        var settings = new WpfDrawingSettings { IncludeRuntime = false, TextAsGeometry = true };
        using var converter = new FileSvgReader(settings) { SaveXaml = false, SaveZaml = false };
        var drawing = converter.Read(sanitized) ?? throw new InvalidDataException("Не удалось отрисовать SVG.");
        token.ThrowIfCancellationRequested();
        var bounds = drawing.Bounds;
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0 || !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height))
            throw new InvalidDataException("SVG не содержит видимых объектов.");
        var ratio = Math.Min(1, Math.Min(2400 / bounds.Width, 1800 / bounds.Height));
        var width = Math.Max(1, (int)Math.Ceiling(bounds.Width * ratio));
        var height = Math.Max(1, (int)Math.Ceiling(bounds.Height * ratio));
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.PushTransform(new ScaleTransform(ratio, ratio));
            context.PushTransform(new TranslateTransform(-bounds.X, -bounds.Y));
            context.DrawDrawing(drawing);
            context.Pop();
            context.Pop();
        }
        token.ThrowIfCancellationRequested();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static string? SafeCss(string value)
    {
        var plain = CssComments.Replace(value, string.Empty);
        if (plain.Contains('@') || plain.Contains('\\')) return null;
        foreach (Match match in CssUrls.Matches(plain))
            if (!LocalCssUrl.IsMatch(match.Value)) return null;
        // Malformed/unclosed URL syntax is discarded instead of being repaired by the renderer.
        var withoutUrls = CssUrls.Replace(plain, string.Empty);
        return withoutUrls.Contains("url", StringComparison.OrdinalIgnoreCase) ? null : plain;
    }

    private static void RejectReferenceCycles(XElement root, CancellationToken token)
    {
        var elements = root.DescendantsAndSelf().ToList();
        var ids = elements.Where(e => e.Attribute("id") is not null)
            .GroupBy(e => e.Attribute("id")!.Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var visited = new Dictionary<XElement, byte>();
        var stack = new Stack<(XElement Element, bool Exit, int Depth)>();
        stack.Push((root, false, 0));
        while (stack.TryPop(out var next))
        {
            token.ThrowIfCancellationRequested();
            if (next.Exit) { visited[next.Element] = 2; continue; }
            if (visited.TryGetValue(next.Element, out var state))
            {
                if (state == 1) throw new InvalidDataException("SVG содержит циклические ссылки.");
                continue;
            }
            if (next.Depth > 128) throw new NotSupportedException("Структура SVG слишком сложная для быстрого предпросмотра.");
            visited[next.Element] = 1;
            stack.Push((next.Element, true, next.Depth));
            foreach (var child in next.Element.Elements()) stack.Push((child, false, next.Depth + 1));
            foreach (var attribute in next.Element.Attributes().Where(a => a.Name.LocalName == "href" && a.Value.StartsWith('#')))
                if (ids.TryGetValue(attribute.Value[1..], out var target)) stack.Push((target, false, next.Depth + 1));
            foreach (var attribute in next.Element.Attributes().Where(a => !a.IsNamespaceDeclaration))
                foreach (Match match in CssUrls.Matches(attribute.Value))
                {
                    var fragment = match.Value[(match.Value.IndexOf('#') + 1)..].TrimEnd(')', ' ', '\t', '\r', '\n', '\'', '"');
                    if (ids.TryGetValue(fragment, out var target)) stack.Push((target, false, next.Depth + 1));
                }
        }
    }
}
