using System.IO;
using System.IO.Compression;
using System.Text;
using SharpCompress.Common;
using SharpCompress.Writers;
using SharpCompress.Writers.SevenZip;

internal sealed record FixtureFile(string Path, string Extension, string? ArchiveSentinel, int Triangles);

internal static class Fixtures
{
    public static FixtureFile[] Create(string directory)
    {
        Directory.CreateDirectory(directory);
        var extensions = new[] { ".pdf", ".zip", ".STEP", ".stl", ".7z", ".3mf", ".obj" };
        var files = new List<FixtureFile>();
        for (var index = 0; index < 21; index++)
        {
            var extension = extensions[index % extensions.Length];
            var path = System.IO.Path.Combine(directory, $"{index:00}-выбор С ПРОБЕЛАМИ{extension}");
            var sentinel = $"LATEST-{index:00}-ONLY.txt";
            switch (extension)
            {
                case ".pdf": File.Copy(System.IO.Path.Combine(AppContext.BaseDirectory, "seed", "preview.pdf"), path); break;
                case ".STEP": File.Copy(System.IO.Path.Combine(AppContext.BaseDirectory, "seed", "cube.step"), path); break;
                case ".zip":
                    using (var file = File.Create(path)) using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
                    using (var writer = new StreamWriter(zip.CreateEntry(sentinel).Open())) writer.Write("Selected archive " + index);
                    break;
                case ".7z":
                    using (var file = File.Create(path))
                    using (var writer = WriterFactory.OpenWriter(file, ArchiveType.SevenZip, new SevenZipWriterOptions(CompressionType.LZMA2)))
                    using (var payload = new MemoryStream(Encoding.UTF8.GetBytes("Selected archive " + index)))
                        writer.Write(sentinel, payload, DateTime.UtcNow);
                    break;
                case ".stl": File.WriteAllText(path, TetrahedronStl()); break;
                case ".obj": File.WriteAllText(path, "o TwoTriangles\nv 0 0 0\nv 20 0 0\nv 0 20 0\nv 0 0 20\nf 1 2 3\nf 1 4 2\n"); break;
                case ".3mf": CreateThreeMf(path); break;
            }
            files.Add(new FixtureFile(path, extension.ToLowerInvariant(), extension is ".zip" or ".7z" ? sentinel : null,
                extension is ".STEP" or ".3mf" ? 12 : extension == ".stl" ? 4 : extension == ".obj" ? 2 : 0));
        }
        return files.ToArray();
    }

    private static string TetrahedronStl()
    {
        var vertices = new[] { "0 0 0", "20 0 0", "0 20 0", "0 0 20" };
        var output = new StringBuilder("solid Tetrahedron\n");
        foreach (var face in new[] { new[]{0,2,1},new[]{0,1,3},new[]{1,2,3},new[]{2,0,3} })
        {
            output.AppendLine("facet normal 0 0 0\nouter loop");
            foreach (var vertex in face) output.AppendLine("vertex " + vertices[vertex]);
            output.AppendLine("endloop\nendfacet");
        }
        return output.AppendLine("endsolid Tetrahedron").ToString();
    }

    private static void CreateThreeMf(string path)
    {
        using var file = File.Create(path); using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        Write("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"model\" ContentType=\"application/vnd.ms-package.3dmanufacturing-3dmodel+xml\"/></Types>");
        Write("_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Target=\"/3D/3dmodel.model\" Id=\"rel0\" Type=\"http://schemas.microsoft.com/3dmanufacturing/2013/01/3dmodel\"/></Relationships>");
        var xml = new StringBuilder("<model unit=\"millimeter\" xmlns=\"http://schemas.microsoft.com/3dmanufacturing/core/2015/02\"><resources><object id=\"1\" type=\"model\"><mesh><vertices>");
        foreach (var (x,y,z) in new[]{(0,0,0),(20,0,0),(20,20,0),(0,20,0),(0,0,20),(20,0,20),(20,20,20),(0,20,20)}) xml.Append($"<vertex x=\"{x}\" y=\"{y}\" z=\"{z}\"/>");
        xml.Append("</vertices><triangles>");
        foreach (var face in new[]{new[]{0,3,2,1},new[]{4,5,6,7},new[]{0,1,5,4},new[]{1,2,6,5},new[]{2,3,7,6},new[]{3,0,4,7}})
            xml.Append($"<triangle v1=\"{face[0]}\" v2=\"{face[1]}\" v3=\"{face[2]}\"/><triangle v1=\"{face[0]}\" v2=\"{face[2]}\" v3=\"{face[3]}\"/>");
        xml.Append("</triangles></mesh></object></resources><build><item objectid=\"1\"/></build></model>");
        Write("3D/3dmodel.model", xml.ToString());
        void Write(string name, string content) { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(content); }
    }
}
