using System.IO.Compression;
using System.Security.Cryptography;
using QuickPreview;

var fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures");
Directory.CreateDirectory(fixtures);
var fixture = Path.Combine(fixtures, "sample.zip");
using (var file = File.Create(fixture))
using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
{
    using var writer = new StreamWriter(zip.CreateEntry("folder/привет.txt").Open());
    writer.Write("hello");
}
var result = await ArchivePreviewService.LoadAsync(fixture);
Assert(result.Error is null && result.Entries.Count == 1 && result.Entries[0].Path == "folder/привет.txt" && result.Entries[0].Size == 5, "ZIP Unicode name and uncompressed size");

using (var file = File.Create(Path.Combine(fixtures, "empty.zip")))
using (var zip = new ZipArchive(file, ZipArchiveMode.Create)) { }
result = await ArchivePreviewService.LoadAsync(Path.Combine(fixtures, "empty.zip"));
Assert(result.Error is null && result.Summary == "Архив пуст", "Empty ZIP");

File.WriteAllText(Path.Combine(fixtures, "broken.zip"), "not an archive");
result = await ArchivePreviewService.LoadAsync(Path.Combine(fixtures, "broken.zip"));
Assert(result.Error is not null, "Damaged archive returns useful error");

using (var file = File.Create(Path.Combine(fixtures, "large.zip")))
using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
    for (var index = 0; index < 1002; index++) zip.CreateEntry($"{index}.txt");
result = await ArchivePreviewService.LoadAsync(Path.Combine(fixtures, "large.zip"));
Assert(result.Error is null && result.IsTruncated && result.Entries.Count == 1000, "Entry limit");

using var cancellation = new CancellationTokenSource();
cancellation.Cancel();
try
{
    await ArchivePreviewService.LoadAsync(fixture, cancellation.Token);
    throw new Exception("Cancelled request was not cancelled.");
}
catch (OperationCanceledException) { Console.WriteLine("PASS: Cancellation"); }

foreach (var path in Directory.EnumerateFiles(Path.Combine(fixtures, "archives")).Concat(args))
{
    var originalHash = SHA256.HashData(File.ReadAllBytes(path));
    result = await ArchivePreviewService.LoadAsync(path);
    Console.WriteLine($"{Path.GetFileName(path)}: {result.Summary}; error={result.Error}");
    foreach (var entry in result.Entries.Take(4)) Console.WriteLine($"  {entry.Path}: {entry.Size}");
    if (Path.GetFileName(path).StartsWith("encrypted-headers")) Assert(result.Error is not null, "Encrypted archive headers handled");
    else if (Path.GetFileName(path).StartsWith("encrypted-entries")) Assert(result.Error is null && result.Entries.Any(entry => entry.IsEncrypted) && result.Entries.Any(entry => entry.Path == "nested/привет.txt"), "Encrypted ZIP legacy CP866 names visible");
    else Assert(result.Error is null && result.Entries.Count > 0, Path.GetExtension(path) + " listing");
    if (Path.GetFileName(path).StartsWith("sample.") && !path.EndsWith(".rar"))
        Assert(result.Entries.Any(entry => entry.Path == "nested/привет.txt" && entry.Size == 11), "Unicode name and uncompressed size preserved");
    Assert(originalHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))), "Source archive unmodified");
}

static void Assert(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
}
