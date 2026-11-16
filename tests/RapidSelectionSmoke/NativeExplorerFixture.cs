using System.Diagnostics;
using System.Runtime.InteropServices;

internal sealed class NativeExplorerFixture : IDisposable
{
    private object? _shell, _windows, _explorer, _document, _folder;
    private bool _ownsWindow;
    public IntPtr Handle { get; private set; }

    public static async Task<NativeExplorerFixture> OpenAsync(string directory)
    {
        var fixture = new NativeExplorerFixture();
        try
        {
            fixture._shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
            fixture._windows = ((dynamic)fixture._shell!).Windows();
            var existing = new HashSet<IntPtr>();
            for (var index = 0; index < (int)((dynamic)fixture._windows!).Count; index++)
            {
                object? candidate = null;
                try { candidate = ((dynamic)fixture._windows).Item(index); existing.Add(new IntPtr(Convert.ToInt64(((dynamic)candidate!).HWND))); }
                catch (COMException) { }
                finally { Release(candidate); }
            }
            Process.Start(new ProcessStartInfo("explorer.exe", $"/n,\"{directory}\"") { UseShellExecute = true });
            var watch = Stopwatch.StartNew();
            while (fixture._explorer is null && watch.Elapsed < TimeSpan.FromSeconds(15))
            {
                for (var index = 0; index < (int)((dynamic)fixture._windows).Count; index++)
                {
                    object? candidate = null, document = null, folder = null, self = null;
                    try
                    {
                        candidate = ((dynamic)fixture._windows).Item(index);
                        document = ((dynamic)candidate!).Document;
                        folder = ((dynamic)document).Folder;
                        self = ((dynamic)folder).Self;
                        if (string.Equals((string)((dynamic)self).Path, directory, StringComparison.OrdinalIgnoreCase))
                        {
                            fixture.Handle = new IntPtr(Convert.ToInt64(((dynamic)candidate).HWND));
                            fixture._ownsWindow = !existing.Contains(fixture.Handle);
                            if (!fixture._ownsWindow) throw new InvalidOperationException("Windows reused a pre-existing Explorer HWND; fixture will not close it.");
                            fixture._explorer = candidate; candidate = null; break;
                        }
                    }
                    catch (COMException) { }
                    finally { Release(self); Release(folder); Release(document); Release(candidate); }
                }
                if (fixture._explorer is null) await Task.Delay(80);
            }
            if (fixture._explorer is null) throw new TimeoutException("Dedicated Explorer fixture was not found.");
            fixture._document = ((dynamic)fixture._explorer).Document;
            fixture._folder = ((dynamic)fixture._document).Folder;
            ((dynamic)fixture._document).SortColumns = "prop:System.ItemNameDisplay;";
            await Task.Delay(120);
            await fixture.ForegroundAsync();
            return fixture;
        }
        catch { fixture.Dispose(); throw; }
    }

    public void UseDetailsView() => ((dynamic)_document!).CurrentViewMode = 4;

    public void Select(string fileName)
    {
        object? item = null;
        try { item = ((dynamic)_folder!).ParseName(fileName); ((dynamic)_document!).SelectItem(item, 0x1 | 0x4 | 0x8 | 0x10); }
        finally { Release(item); }
    }

    public async Task ForegroundAsync()
    {
        if (Native.GetForegroundWindow() == Handle) return;
        _ = Native.SetForegroundWindow(Handle);
        if (Native.GetForegroundWindow() != Handle)
        {
            var foregroundThread = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), IntPtr.Zero);
            var targetThread = Native.GetWindowThreadProcessId(Handle, IntPtr.Zero);
            var currentThread = Native.GetCurrentThreadId();
            var attached = foregroundThread != 0 && foregroundThread != currentThread && Native.AttachThreadInput(currentThread, foregroundThread, true);
            var attachedTarget = targetThread != 0 && targetThread != currentThread && targetThread != foregroundThread && Native.AttachThreadInput(currentThread, targetThread, true);
            try { _ = Native.ShowWindow(Handle, 9); _ = Native.BringWindowToTop(Handle); _ = Native.SetActiveWindow(Handle); _ = Native.SetForegroundWindow(Handle); }
            finally
            {
                if (attachedTarget) _ = Native.AttachThreadInput(currentThread, targetThread, false);
                if (attached) _ = Native.AttachThreadInput(currentThread, foregroundThread, false);
            }
            if (Native.GetForegroundWindow() != Handle) Native.SwitchToThisWindow(Handle, true);
        }
        await Task.Delay(50);
        if (Native.GetForegroundWindow() != Handle) throw new InvalidOperationException("Could not establish foreground for own Explorer fixture.");
    }

    public void Dispose()
    {
        if (_ownsWindow && _explorer is not null) { try { ((dynamic)_explorer).Quit(); } catch (COMException) { } }
        Release(_folder); Release(_document); Release(_explorer); Release(_windows); Release(_shell);
        _folder = _document = _explorer = _windows = _shell = null;
    }
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
}

internal static class Native
{
    internal static bool IsAbove(IntPtr above, IntPtr below)
    {
        var visited = new HashSet<IntPtr>();
        for (var current = GetWindow(below, 3); current != IntPtr.Zero && visited.Add(current); current = GetWindow(current, 3))
            if (current == above) return true;
        return false;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] internal static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] internal static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, IntPtr processId);
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] internal static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] internal static extern bool BringWindowToTop(IntPtr window);
    [DllImport("user32.dll")] internal static extern IntPtr SetActiveWindow(IntPtr window);
    [DllImport("user32.dll")] internal static extern void SwitchToThisWindow(IntPtr window, bool altTab);
}
