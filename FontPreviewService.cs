using System.IO;
using System.Windows.Media;
using FontFamily = System.Windows.Media.FontFamily;

namespace QuickPreview;

internal sealed record FontPreviewResult(FontFamily Family, string Name, string Details);

internal static class FontPreviewService
{
    public static bool Supports(string path) => Path.GetExtension(path).ToLowerInvariant() is ".ttf" or ".otf" or ".ttc";
    public static FontPreviewResult Load(string path)
    {
        if (new FileInfo(path).Length > 32 * 1024 * 1024)
            throw new NotSupportedException("Шрифт слишком большой для быстрого предпросмотра.");
        var fullPath = Path.GetFullPath(path);
        var directory = new Uri(Path.GetDirectoryName(fullPath)! + Path.DirectorySeparatorChar);
        var glyph = new GlyphTypeface(new Uri(fullPath));
        var name = glyph.FamilyNames.Values.FirstOrDefault() ?? Path.GetFileNameWithoutExtension(path);
        // FontFamily parses its own location syntax and URI-escapes the filename.
        var family = new FontFamily(directory, "./" + Path.GetFileName(path) + "#" + name);
        var faces = family.GetTypefaces().ToArray();
        var styles = string.Join(", ", faces.Select(face => face.FaceNames.Values.FirstOrDefault()).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().Take(12));
        return new FontPreviewResult(family, name, string.IsNullOrWhiteSpace(styles) ? "Образец шрифта" : styles);
    }
}
