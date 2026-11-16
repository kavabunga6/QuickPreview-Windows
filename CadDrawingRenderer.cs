using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using CSMath;
using MediaColor = System.Windows.Media.Color;
using MediaPoint = System.Windows.Point;
using MediaSize = System.Windows.Size;
using Line = ACadSharp.Entities.Line;
using Brushes = System.Windows.Media.Brushes;
using Pen = System.Windows.Media.Pen;
using FlowDirection = System.Windows.FlowDirection;

namespace QuickPreview;

// Renders the model-space plan. This intentionally does not advertise AutoCAD
// layouts, ACIS solids or every proprietary/custom entity as fully supported.
internal static class CadDrawingRenderer
{
    private const int MaxEntities = 30_000;
    private const int MaxSegments = 150_000;
    private sealed record Stroke(MediaPoint[] Points, MediaColor Color);
    private sealed record Label(MediaPoint Origin, string Text, double Height, MediaColor Color);

    public static PreviewWorkerResult Render(string path, string destination)
    {
        EncodingRegistration();
        var document = Path.GetExtension(path).Equals(".dwg", StringComparison.OrdinalIgnoreCase)
            ? DwgReader.Read(path) : DxfReader.Read(path);
        var strokes = new List<Stroke>();
        var labels = new List<Label>();
        var visited = 0; var segments = 0; var unsupported = 0;
        foreach (var entity in document.Entities)
        {
            Visit(entity, 0);
            if (visited >= MaxEntities || segments >= MaxSegments) break;
        }
        var labelBounds = labels.SelectMany(label =>
        {
            // CAD heights are world units, not WPF font sizes. Measuring at a
            // fixed em size keeps equivalent drawings independent of their unit
            // scale and avoids FormattedText's finite font-size ceiling.
            const double measurementSize = 100;
            var text = new FormattedText(label.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), measurementSize, Brushes.Black, 1);
            var unitsPerEm = label.Height / measurementSize;
            return new[] { label.Origin, new MediaPoint(label.Origin.X + text.WidthIncludingTrailingWhitespace * unitsPerEm,
                label.Origin.Y - text.Height * unitsPerEm) };
        });
        var allPoints = strokes.SelectMany(s => s.Points).Concat(labelBounds).ToArray();
        if (allPoints.Length == 0)
            throw new NotSupportedException("В чертеже нет доступной 2D-геометрии. Для 3D-тел и специальных объектов откройте файл в CAD-приложении.");
        var minX = allPoints.Min(p => p.X); var minY = allPoints.Min(p => p.Y);
        var maxX = allPoints.Max(p => p.X); var maxY = allPoints.Max(p => p.Y);
        var width = Math.Max(1e-9, maxX - minX); var height = Math.Max(1e-9, maxY - minY);
        const int pixels = 1800; const double margin = 70;
        var scale = Math.Min((pixels - margin * 2) / width, (pixels - margin * 2) / height);
        var offsetX = (pixels - width * scale) / 2;
        var offsetY = (pixels - height * scale) / 2;
        MediaPoint Project(MediaPoint p) => new(offsetX + (p.X - minX) * scale,
            pixels - offsetY - (p.Y - minY) * scale);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, pixels, pixels));
            foreach (var stroke in strokes)
            {
                if (stroke.Points.Length == 0) continue;
                var geometry = new StreamGeometry();
                using (var drawing = geometry.Open())
                {
                    drawing.BeginFigure(Project(stroke.Points[0]), false, false);
                    drawing.PolyLineTo(stroke.Points.Skip(1).Select(Project).ToArray(), true, false);
                }
                geometry.Freeze();
                context.DrawGeometry(null, new Pen(new SolidColorBrush(stroke.Color), 1.4), geometry);
            }
            foreach (var label in labels)
            {
                var text = new FormattedText(label.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), Math.Clamp(label.Height * scale, 0.1, 160), new SolidColorBrush(label.Color), 1);
                context.DrawText(text, Project(label.Origin));
            }
        }
        var bitmap = new RenderTargetBitmap(pixels, pixels, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(destination)) encoder.Save(file);
        var limitations = unsupported > 0 ? $" · специальных объектов пропущено: {unsupported:N0}" : string.Empty;
        if (visited >= MaxEntities || segments >= MaxSegments) limitations += " · показана часть чертежа";
        return new PreviewWorkerResult(true, $"2D-план · объектов: {Math.Min(visited, MaxEntities):N0}{limitations}");

        void Add(IEnumerable<XYZ> points, MediaColor color, bool closed = false)
        {
            if (segments >= MaxSegments) return;
            var array = points.Take(Math.Max(0, MaxSegments - segments)).Where(p =>
                double.IsFinite(p.X) && double.IsFinite(p.Y) && Math.Abs(p.X) < 1e15 && Math.Abs(p.Y) < 1e15)
                .Select(p => new MediaPoint(p.X, p.Y)).ToList();
            if (closed && array.Count > 1) array.Add(array[0]);
            if (array.Count < 2) return;
            strokes.Add(new Stroke(array.ToArray(), color)); segments += array.Count - 1;
        }
        void Visit(Entity entity, int depth)
        {
            if (depth > 12 || visited++ >= MaxEntities || segments >= MaxSegments) return;
            if (entity.IsInvisible || entity.Layer?.IsOn == false) return;
            var color = ResolveColor(entity);
            try
            {
                switch (entity)
                {
                    case Line line: Add(new[] { line.StartPoint, line.EndPoint }, color); break;
                    case Arc arc: Add(arc.PolygonalVertexes(96), color); break;
                    case Circle circle: Add(circle.PolygonalVertexes(128), color, true); break;
                    case Ellipse ellipse: Add(ellipse.PolygonalVertexes(128), color, ellipse.IsFullEllipse); break;
                    case Spline spline: Add(spline.PolygonalVertexes(160), color, spline.IsClosed); break;
                    case IPolyline polyline:
                        var vertices = polyline.Vertices.Take(10_000).ToArray();
                        for (var i = 0; i < vertices.Length - (polyline.IsClosed ? 0 : 1); i++)
                        {
                            var first = vertices[i]; var second = vertices[(i + 1) % vertices.Length];
                            var firstPoint = new XYZ(first.Location[0], first.Location[1], 0);
                            var secondPoint = new XYZ(second.Location[0], second.Location[1], 0);
                            if (Math.Abs(first.Bulge) > 1e-8)
                                Add(Arc.CreateFromBulge(new XY(firstPoint.X, firstPoint.Y),
                                    new XY(secondPoint.X, secondPoint.Y), first.Bulge).PolygonalVertexes(32), color);
                            else Add(new[] { firstPoint, secondPoint }, color);
                        }
                        break;
                    case Insert insert:
                        foreach (var part in insert.Explode().Take(MaxEntities)) Visit(part, depth + 1);
                        break;
                    case Hatch hatch:
                        foreach (var part in hatch.Explode().Take(MaxEntities)) Visit(part, depth + 1);
                        break;
                    case Dimension dimension when dimension.Block is not null:
                        foreach (var part in dimension.Block.Entities.Take(MaxEntities)) Visit(part, depth + 1);
                        break;
                    case TextEntity text: AddText(text.InsertPoint, text.Value, text.Height, color); break;
                    case MText text: AddText(text.InsertPoint, text.PlainText, text.Height, color); break;
                    case Face3D face: Add(new[] { face.FirstCorner, face.SecondCorner, face.ThirdCorner, face.FourthCorner }, color, true); break;
                    case Solid solid: Add(new[] { solid.FirstCorner, solid.SecondCorner, solid.FourthCorner, solid.ThirdCorner }, color, true); break;
                    default: unsupported++; break;
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException) { unsupported++; }
        }
        void AddText(XYZ point, string? value, double textHeight, MediaColor color)
        {
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || Math.Abs(point.X) >= 1e15 ||
                Math.Abs(point.Y) >= 1e15 || string.IsNullOrWhiteSpace(value)) return;
            labels.Add(new Label(new MediaPoint(point.X, point.Y), value[..Math.Min(value.Length, 2000)],
                double.IsFinite(textHeight) ? Math.Max(0, textHeight) : 1, color));
        }
    }

    private static MediaColor ResolveColor(Entity entity)
    {
        var color = entity.GetActiveColor();
        if (!color.IsTrueColor)
            return color.Index switch
            {
                1 => MediaColor.FromRgb(190, 40, 40), 2 => MediaColor.FromRgb(150, 115, 0),
                3 => MediaColor.FromRgb(25, 125, 50), 4 => MediaColor.FromRgb(0, 125, 145),
                5 => MediaColor.FromRgb(35, 80, 190), 6 => MediaColor.FromRgb(160, 35, 155),
                _ => MediaColor.FromRgb(25, 30, 40)
            };
        return color.R + color.G + color.B > 650
            ? MediaColor.FromRgb(25, 30, 40) : MediaColor.FromRgb(color.R, color.G, color.B);
    }

    private static void EncodingRegistration() => System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
}
