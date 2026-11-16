using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace QuickPreview;

/// <summary>Uses the Windows document filter for text from legacy Word and PowerPoint files.</summary>
internal static class LegacyOfficePreviewService
{
    private const int EndOfChunks = unchecked((int)0x80041700);
    private const int NoMoreText = unchecked((int)0x80041701);
    private const int NoText = unchecked((int)0x80041705);
    private const int EmbeddingUnavailable = unchecked((int)0x80041707);
    private const int LinkUnavailable = unchecked((int)0x80041708);
    private const int PasswordRequired = unchecked((int)0x8004170B);
    private const int LastText = 0x00041709;
    private const int MaxCharacters = 500_000;
    private static readonly SemaphoreSlim WorkerGate = new(1, 1);
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".doc", ".dot", ".ppt", ".pps", ".pot" };

    public static bool Supports(string path) => Extensions.Contains(Path.GetExtension(path));

    public static async Task<OfficePreviewResult> LoadAsync(string path, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!await WorkerGate.WaitAsync(TimeSpan.FromSeconds(2), token).ConfigureAwait(false))
            throw new NotSupportedException("Системный обработчик Office ещё завершает предыдущий просмотр. Попробуйте снова через несколько секунд.");

        var completion = new TaskCompletionSource<OfficePreviewResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Observe a late worker failure even if navigation has already canceled the caller's wait.
        _ = completion.Task.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        var worker = new Thread(() =>
        {
            try { completion.TrySetResult(ReadContent(path, token)); }
            catch (OperationCanceledException) { completion.TrySetCanceled(token); }
            catch (Exception exception) { completion.TrySetException(exception); }
            finally { WorkerGate.Release(); }
        }) { IsBackground = true, Name = "QuickPreview Office filter" };
        try
        {
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
        }
        catch
        {
            WorkerGate.Release();
            throw;
        }

        // Native filters cannot be interrupted safely. A stalled filter holds the gate so repeated
        // selections cannot accumulate threads; the UI still gets cancellation or a bounded timeout.
        try { return await completion.Task.WaitAsync(TimeSpan.FromSeconds(12), token).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            throw new NotSupportedException("Системный обработчик Office не успел подготовить предпросмотр. Откройте файл в офисном приложении.");
        }
    }

    private static OfficePreviewResult ReadContent(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Supports(path)) throw new NotSupportedException("Этот формат не поддерживается системным просмотром Office.");
        if (new FileInfo(path).Length > 256L * 1024 * 1024)
            throw new NotSupportedException("Документ слишком большой для быстрого предпросмотра (более 256 МБ).");

        IFilter? filter = null;
        var watch = Stopwatch.StartNew();
        try
        {
            var status = LoadIFilter(path, IntPtr.Zero, out filter);
            token.ThrowIfCancellationRequested();
            if (watch.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException();
            ThrowFilterError(status);
            if (filter is null) throw new NotSupportedException("В Windows отсутствует обработчик этого формата Office.");
            // The built-in legacy Office filter returns no content with newer Init flags.
            // Use its default contents property and never request IFILTER_INIT_SEARCH_LINKS.
            status = filter.Init(0, 0, IntPtr.Zero, out _);
            token.ThrowIfCancellationRequested();
            if (watch.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException();
            ThrowFilterError(status);

            var output = new StringBuilder();
            var buffer = new char[4096];
            var truncated = false;
            var failures = 0;
            var chunks = 0;
            while (chunks++ < 20_000)
            {
                token.ThrowIfCancellationRequested();
                if (watch.Elapsed > TimeSpan.FromSeconds(10)) { truncated = true; break; }
                status = filter.GetChunk(out var chunk);
                if (status == EndOfChunks) break;
                if (status is EmbeddingUnavailable or LinkUnavailable) continue;
                ThrowFilterError(status);
                if ((chunk.Flags & 1) == 0) continue;
                if (output.Length > 0 && chunk.BreakType != 0) output.AppendLine();

                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    if (watch.Elapsed > TimeSpan.FromSeconds(10)) { truncated = true; break; }
                    uint length = (uint)buffer.Length;
                    status = filter.GetText(ref length, buffer);
                    if (status is NoMoreText or NoText) break;
                    ThrowFilterError(status);
                    var characters = Math.Min((int)Math.Min(length, (uint)buffer.Length), MaxCharacters - output.Length);
                    if (characters > 0) output.Append(buffer, 0, characters);
                    if (output.Length >= MaxCharacters) { truncated = true; break; }
                    if (status == LastText) break;
                    // Broken third-party handlers must not cause an endless successful empty read.
                    if (length == 0 && ++failures >= 8) { truncated = true; break; }
                }
                if (truncated) break;
            }
            if (chunks > 20_000) truncated = true;
            var text = output.ToString().Replace('\u2029', '\n').Replace('\u2028', '\n')
                .Replace("\0", string.Empty).Trim(' ', '\r', '\n', '\t');
            if (text.Length == 0)
                throw new NotSupportedException("Системный обработчик не вернул текст документа. Используйте «Открыть» для просмотра в офисном приложении.");
            if (truncated) text += "\n\n… Показана часть содержимого. Откройте файл для просмотра целиком.";
            var extension = Path.GetExtension(path);
            var format = extension.Equals(".doc", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".dot", StringComparison.OrdinalIgnoreCase) ? "Word" : "PowerPoint";
            return new OfficePreviewResult(Path.GetFileName(path), text, $"{format} · текст документа · системный обработчик Windows", truncated);
        }
        catch (DllNotFoundException)
        {
            throw new NotSupportedException("В Windows отсутствует системный обработчик документов Office.");
        }
        finally
        {
            if (filter is not null && Marshal.IsComObject(filter)) Marshal.FinalReleaseComObject(filter);
        }
    }

    private static void ThrowFilterError(int status)
    {
        if (status == PasswordRequired)
            throw new NotSupportedException("Документ защищён паролем. Откройте его в Office для ввода пароля.");
        if (status < 0)
            throw new NotSupportedException("Системный обработчик Windows не смог прочитать этот документ Office. Файл может быть повреждён или его формат не поддерживается.");
    }

    [DllImport("query.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int LoadIFilter(string path, IntPtr outer, [MarshalAs(UnmanagedType.Interface)] out IFilter? filter);

    [ComImport, Guid("89BCB740-6119-101A-BCB7-00DD010655AF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFilter
    {
        [PreserveSig] int Init(uint flags, uint attributeCount, IntPtr attributes, out uint outputFlags);
        [PreserveSig] int GetChunk(out StatChunk chunk);
        [PreserveSig] int GetText(ref uint size, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] char[] buffer);
        [PreserveSig] int GetValue(ref IntPtr value);
        [PreserveSig] int BindRegion(FilterRegion region, ref Guid interfaceId, out IntPtr unknown);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertySpec { public uint Kind; public IntPtr Value; }
    [StructLayout(LayoutKind.Sequential)]
    private struct FullPropertySpec { public Guid PropertySet; public PropertySpec Property; }
    [StructLayout(LayoutKind.Sequential)]
    private struct StatChunk
    {
        public uint Id;
        public uint BreakType;
        public uint Flags;
        public uint Locale;
        public FullPropertySpec Attribute;
        public uint SourceId;
        public uint SourceStart;
        public uint SourceLength;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FilterRegion { public uint ChunkId; public uint Start; public uint Extent; }
}
