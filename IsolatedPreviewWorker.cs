using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Media.Imaging;
using Microsoft.Win32.SafeHandles;

namespace QuickPreview;

internal sealed record RenderedPreview(BitmapSource Image, string Summary);
internal sealed record PreviewWorkerResult(bool Success, string Summary, string? Error = null);

// Complex native decoders run in a disposable process. A corrupt drawing or image
// cannot retain the preview UI, and selection changes terminate outstanding work.
internal static class IsolatedPreviewWorker
{
    private static readonly SemaphoreSlim Gate = new(1);
    public static Task<RenderedPreview> RenderAsync(string kind, string path, CancellationToken token) =>
        RenderCoreAsync(kind, path, TimeSpan.FromSeconds(20), token);

    internal static Task<RenderedPreview> RenderThumbnailAsync(string path, CancellationToken token) =>
        RenderCoreAsync("thumbnail", path, TimeSpan.FromSeconds(2), token);

    private static async Task<RenderedPreview> RenderCoreAsync(string kind, string path, TimeSpan timeout, CancellationToken token)
    {
        await Gate.WaitAsync(token);
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "QuickPreview", "renders"));
        var directory = Path.GetFullPath(Path.Combine(root, Guid.NewGuid().ToString("N")));
        try
        {
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(directory);
            var executable = Path.Combine(AppContext.BaseDirectory, "QuickPreview.exe");
            if (!File.Exists(executable))
                throw new FileNotFoundException("Не найден модуль предпросмотра. Переустановите QuickPreview.");
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = AppContext.BaseDirectory
            };
            foreach (var argument in new[] { "--preview-worker", kind, Path.GetFullPath(path), directory })
                start.ArgumentList.Add(argument);
            using var job = new PreviewJob();
            using var process = Process.Start(start) ?? throw new IOException("Не удалось запустить модуль предпросмотра.");
            try
            {
                job.Assign(process);
                await process.WaitForExitAsync(token).WaitAsync(timeout, token);
                var resultPath = Path.Combine(directory, "result.json");
                if (!File.Exists(resultPath) || new FileInfo(resultPath).Length > 64 * 1024)
                    throw new NotSupportedException("Обработчик не смог прочитать файл в пределах памяти предпросмотра.");
                var result = JsonSerializer.Deserialize<PreviewWorkerResult>(await File.ReadAllTextAsync(resultPath, token));
                if (result is not { Success: true })
                    throw new NotSupportedException(result?.Error ?? "Формат изображения или чертежа не поддерживается.");
                var imagePath = Path.Combine(directory, "preview.png");
                if (!File.Exists(imagePath) || new FileInfo(imagePath).Length > 80 * 1024 * 1024)
                    throw new IOException("Обработчик не вернул изображение предпросмотра.");
                // Even the bounded PNG transport can contain millions of pixels.
                // Decode it away from WPF's input/keyboard-hook dispatcher.
                var bitmap = await Task.Run(() => DecodeTransport(imagePath, token), token);
                return new RenderedPreview(bitmap, result.Summary);
            }
            catch (TimeoutException)
            {
                throw new NotSupportedException("Файл слишком сложный для быстрого предпросмотра. Откройте его в профильном приложении.");
            }
            finally
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                finally { job.Dispose(); }
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        finally
        {
            // The only recursive cleanup target is this generated, verified session directory.
            if (directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                try { Directory.Delete(directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            Gate.Release();
        }
    }

    private static BitmapSource DecodeTransport(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var file = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(file, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 ||
            (long)frame.PixelWidth * frame.PixelHeight > 16_000_000 ||
            frame.PixelWidth > 16384 || frame.PixelHeight > 16384)
            throw new NotSupportedException("Изображение превышает предел памяти предпросмотра.");
        file.Position = 0;
        var bitmap = new BitmapImage();
        bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = file; bitmap.EndInit();
        bitmap.Freeze();
        token.ThrowIfCancellationRequested();
        return bitmap;
    }

    private sealed class PreviewJob : IDisposable
    {
        private readonly SafeFileHandle _handle;
        public PreviewJob()
        {
            _handle = CreateJobObject(IntPtr.Zero, null);
            if (_handle.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var limits = new ExtendedLimits
            {
                Basic = new BasicLimits { Flags = 0x2000 | 0x100 | 0x8 | 0x20, ActiveProcessLimit = 1, PriorityClass = 0x4000 },
                ProcessMemoryLimit = (UIntPtr)(1024UL * 1024 * 1024)
            };
            if (!SetInformationJobObject(_handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            // At most one decoder, below-normal priority, with a 25% system CPU
            // ceiling. Memory/time/cancellation limits remain independent.
            var cpu = new CpuRateLimits { Flags = 0x1 | 0x4, Rate = 2500 };
            if (!SetCpuInformationJobObject(_handle, 15, ref cpu, (uint)Marshal.SizeOf<CpuRateLimits>()))
                Debug.WriteLine($"QuickPreview decoder CPU cap unavailable: {Marshal.GetLastWin32Error()}");
        }
        public void Assign(Process process)
        {
            if (!AssignProcessToJobObject(_handle, process.Handle))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        public void Dispose() => _handle.Dispose();
        [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
        {
            public long ProcessUserTime, JobUserTime; public uint Flags;
            public UIntPtr MinimumWorkingSet, MaximumWorkingSet; public uint ActiveProcessLimit;
            public UIntPtr Affinity; public uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)] private struct IoCounters
        { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
        [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
        {
            public BasicLimits Basic; public IoCounters Io;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemory, PeakJobMemory;
        }
        [StructLayout(LayoutKind.Sequential)] private struct CpuRateLimits { public uint Flags, Rate; }
        [DllImport("kernel32.dll", EntryPoint = "SetInformationJobObject", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetCpuInformationJobObject(SafeFileHandle job, int informationClass, ref CpuRateLimits limits, uint size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref ExtendedLimits limits, uint size);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    }
}
