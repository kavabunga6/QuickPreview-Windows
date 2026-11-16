using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using ImageMagick;
using QuickPreview;

internal static class Program
{
    private static int _checks;
    [STAThread] private static int Main()
    {
        try { RunAsync().GetAwaiter().GetResult(); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static async Task RunAsync()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            var native = Path.Combine(root, "Original native image.png");
            using (var image = new MagickImage(MagickColors.CornflowerBlue, 4000, 1000)) image.Write(native);
            var hash = Hash(native);
            var picture = await IsolatedPreviewWorker.RenderAsync("native-image", native, CancellationToken.None);
            Check(picture.Image.PixelWidth == 4000 && picture.Image.PixelHeight == 1000, "Within-budget native image preserves original resolution above 2400px.");
            Check(picture.Image.IsFrozen && Hash(native) == hash, "Transport is frozen and original native image is unchanged.");

            var oriented = Path.Combine(root, "Original oriented image.jpg");
            using (var image = new MagickImage(MagickColors.OrangeRed, 40, 20))
            {
                var exif = new ExifProfile(); exif.SetValue(ExifTag.Orientation, (ushort)6); image.SetProfile(exif);
                image.Orientation = Orientation.RightTop; image.Write(oriented);
            }
            using (var metadata = new MagickImage(oriented))
                Check(metadata.GetExifProfile()?.GetValue(ExifTag.Orientation)?.Value == 6, "Original JPEG fixture really contains EXIF orientation 6.");
            var orientedHash = Hash(oriented);
            var rotated = await IsolatedPreviewWorker.RenderAsync("native-image", oriented, CancellationToken.None);
            Check(rotated.Image.PixelWidth == 20 && rotated.Image.PixelHeight == 40, "Native JPEG applies EXIF rotation before showing pixels.");
            Check(Hash(oriented) == orientedHash, "EXIF source bytes remain unchanged.");

            var photo = Path.Combine(root, "Original 24MP photo.jpg");
            using (var image = new MagickImage(MagickColors.SeaGreen, 6000, 4000)) image.Write(photo);
            var photoHash = Hash(photo);
            var downsampled = await IsolatedPreviewWorker.RenderAsync("native-image", photo, CancellationToken.None);
            Check((long)downsampled.Image.PixelWidth * downsampled.Image.PixelHeight <= 16_000_000 && downsampled.Image.PixelWidth < 6000,
                "Ordinary 24MP photo receives a bounded reduced preview.");
            Check(downsampled.Summary.Contains("уменьшено"), "Reduced preview is explicitly identified.");
            Check(Hash(photo) == photoHash, "Downsampling does not change the original photo.");

            var icon = Path.Combine(root, "Original multiple sizes.ico");
            using (var icons = new MagickImageCollection())
            {
                icons.Add(new MagickImage(MagickColors.Blue, 16, 16)); icons.Add(new MagickImage(MagickColors.Red, 64, 64));
                icons.Write(icon);
            }
            var largest = await IsolatedPreviewWorker.RenderAsync("native-image", icon, CancellationToken.None);
            Check(largest.Image.PixelWidth == 64 && largest.Image.PixelHeight == 64, "ICO retains the largest available frame.");

            var oversized = Path.Combine(root, "Original huge header.png");
            using (var image = new MagickImage(MagickColors.White, 1, 1)) image.Write(oversized);
            var bytes = File.ReadAllBytes(oversized);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), 20_000);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), 20_000);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(29), Crc(bytes.AsSpan(12, 17)));
            File.WriteAllBytes(oversized, bytes);
            var watch = Stopwatch.StartNew();
            try
            {
                await IsolatedPreviewWorker.RenderAsync("native-image", oversized, CancellationToken.None);
                throw new InvalidOperationException("Huge-header PNG was accepted.");
            }
            catch (NotSupportedException error)
            {
                Check(error.Message.Contains("64 Мп"), "A 400MP PNG is rejected specifically by the metadata pixel budget.");
            }
            Check(watch.Elapsed < TimeSpan.FromSeconds(8), "Huge-header rejection finishes inside the decoder time budget.");
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                await ExpectAsync<OperationCanceledException>(() => IsolatedPreviewWorker.RenderAsync("native-image", native, canceled.Token), "Canceled image request never starts a decoder.");
            }

            var folder = Path.Combine(root, "Original directory"); Directory.CreateDirectory(folder);
            Directory.CreateDirectory(Path.Combine(folder, "Folder"));
            foreach (var name in new[] { "file10.txt", "file2.txt", "file1.txt" }) File.WriteAllText(Path.Combine(folder, name), "Original test content.");
            var listing = await FolderPreviewService.LoadAsync(folder, CancellationToken.None);
            var fileInfo = await FolderPreviewService.ReadPathInfoAsync(Path.Combine(folder, "file2.txt"), CancellationToken.None);
            var directoryInfo = await FolderPreviewService.ReadPathInfoAsync(folder, CancellationToken.None);
            var missingInfo = await FolderPreviewService.ReadPathInfoAsync(Path.Combine(folder, "Missing.txt"), CancellationToken.None);
            Check(fileInfo is { Exists: true, IsDirectory: false, Length: > 0 } && directoryInfo is { Exists: true, IsDirectory: true }, "File and directory metadata are read by the background service.");
            Check(missingInfo is { Exists: false }, "Missing metadata is not mistaken for a directory.");
            Check(listing.Error is null && listing.Entries.Count == 4 && listing.Entries[0].IsDirectory, "Folder sample retains directories first.");
            Check(listing.Entries.Skip(1).Select(entry => entry.Name).SequenceEqual(new[] { "file1.txt", "file2.txt", "file10.txt" }), "Bounded folder sample keeps natural name order.");
            var navigation = await FolderPreviewService.NavigationAsync(Path.Combine(folder, "file2.txt"), CancellationToken.None);
            Check(navigation.Count == 3 && Path.GetFileName(navigation[1]) == "file2.txt", "Standalone navigation is asynchronous and naturally ordered.");
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                await ExpectAsync<OperationCanceledException>(() => FolderPreviewService.LoadAsync(folder, canceled.Token), "Folder cancellation is checked before enumeration.");
            }
            for (var index = 0; index < 4100; index++) File.WriteAllText(Path.Combine(folder, $"generated-{index:0000}.txt"), "");
            var bounded = await FolderPreviewService.LoadAsync(folder, CancellationToken.None);
            Check(bounded.IsTruncated && bounded.Entries.Count == 24, "A large directory returns a bounded sample and reports truncation.");
            await CheckBlockedReaderAsync();
            Console.WriteLine($"PASS Native content lifecycle and budgets: {_checks} checks.");
        }
        finally
        {
            var fixtureRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "fixtures")) + Path.DirectorySeparatorChar;
            if (root.StartsWith(fixtureRoot, StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true);
        }
    }

    private static async Task CheckBlockedReaderAsync()
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var started = 0;
        var first = FolderPreviewService.RunBoundedAsync(_ =>
        {
            Interlocked.Increment(ref started); entered.Set(); release.Wait(TimeSpan.FromSeconds(3)); return 1;
        }, -1, CancellationToken.None, TimeSpan.FromMilliseconds(80));
        try
        {
            Check(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(1))), "Original simulated blocked OS read starts.");
            Check(await first == -1, "Blocked directory caller receives a bounded timeout.");
            var second = await FolderPreviewService.RunBoundedAsync(_ => { Interlocked.Increment(ref started); return 2; }, -1,
                CancellationToken.None, TimeSpan.FromMilliseconds(80));
            Check(second == -1 && started == 1, "A timed-out OS read retains the gate and cannot accumulate new workers.");
        }
        finally { release.Set(); }
        var third = await FolderPreviewService.RunBoundedAsync(_ => 3, -1, CancellationToken.None, TimeSpan.FromSeconds(1));
        Check(third == 3, "Reader gate reopens after the actual blocked operation exits.");
    }

    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        var value = uint.MaxValue;
        foreach (var item in bytes)
        {
            value ^= item;
            for (var bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
        }
        return ~value;
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("FAIL: " + message); _checks++; Console.WriteLine("PASS: " + message); }
    private static async Task ExpectAsync<T>(Func<Task> action, string message) where T : Exception
    { try { await action(); } catch (T) { Check(true, message); return; } throw new InvalidOperationException("FAIL: " + message); }
}
