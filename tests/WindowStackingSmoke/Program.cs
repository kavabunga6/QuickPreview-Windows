using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using QuickPreview;

internal static class Program
{
    private static int _exitCode;
    private static int _assertions;
    private static readonly Type ExplorerService = typeof(App).Assembly.GetType("QuickPreview.ExplorerSelectionService", true)!;
    private const uint NoMoveNoSizeNoActivate = 0x0001 | 0x0002 | 0x0010;

    [STAThread]
    private static int Main()
    {
        var application = new App();
        application.InitializeComponent();
        var startup = typeof(App).GetMethod("App_OnStartup", BindingFlags.Instance | BindingFlags.NonPublic)!;
        application.Startup -= (StartupEventHandler)Delegate.CreateDelegate(typeof(StartupEventHandler), application, startup);
        application.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try { await RunAsync(application); }
            catch (Exception error) { Console.Error.WriteLine(error); _exitCode = 1; }
            finally { application.Shutdown(); Dispatcher.ExitAllFrames(); }
        }));
        Dispatcher.Run();
        return _exitCode;
    }

    private static async Task RunAsync(App application)
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "stacking-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        var names = new[] { "1-first.txt", "2-second.txt", "3-third.txt" };
        var contents = names.Select(name => "Window stacking fixture: " + name).ToArray();
        for (var index = 0; index < names.Length; index++) File.WriteAllText(Path.Combine(fixture, names[index]), contents[index]);

        object? shell = null, shellWindows = null, fixtureExplorer = null, document = null, folder = null;
        var ownsExplorer = false;
        MainWindow? preview = null;
        Window? other = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
            shellWindows = ((dynamic)shell!).Windows();
            var preExistingHandles = ExplorerHandles(shellWindows!);
            Process.Start(new ProcessStartInfo("explorer.exe", $"/n,\"{fixture}\"") { UseShellExecute = true });
            fixtureExplorer = await FindFixtureExplorerAsync(shellWindows!, fixture);
            var explorerHandle = new IntPtr(Convert.ToInt64(((dynamic)fixtureExplorer).HWND));
            ownsExplorer = !preExistingHandles.Contains(explorerHandle);
            Assert(ownsExplorer, "Fixture Explorer uses a new HWND; existing user windows will not be closed");
            document = ((dynamic)fixtureExplorer).Document;
            folder = ((dynamic)document).Folder;
            ((dynamic)document).SortColumns = "prop:System.ItemNameDisplay;";
            Select(document, folder, names[0]);
            await ForegroundAsync(explorerHandle);
            Assert((string?)CallExplorer("TryGetSelectedPath") == Path.Combine(fixture, names[0]), "Origin captured through production Explorer selection service");

            preview = new MainWindow();
            application.MainWindow = preview;
            SetField(application, "_previewWindow", preview);
            preview.ShowPreview(Path.Combine(fixture, names[0]), activateWindow: false);
            await WaitAsync(() => CurrentText(preview) == contents[0], "initial text preview");
            preview.Width = 640; preview.Height = 420; preview.Left = 120; preview.Top = 120;
            preview.UpdateLayout();
            await Task.Delay(60);
            var previewHandle = new WindowInteropHelper(preview).Handle;
            _ = SetWindowPos(explorerHandle, IntPtr.Zero, 80, 80, 900, 650, 0x0010);
            Assert(GetWindowRect(previewHandle, out var initialRect), "Preview native bounds available");
            Assert(GetWindowRect(explorerHandle, out var explorerRect) && Overlaps(initialRect, explorerRect), "Fixture Explorer geometrically overlaps preview");

            // Reproduce the bug with the same selected item: Explorer is foreground and
            // above the still-visible preview. The production poll must repair stacking
            // even though no new path needs to be loaded.
            await CoverPreviewWithExplorerAsync(explorerHandle, previewHandle);
            var generation = GetField(preview, "_representationGeneration");
            Poll(application);
            await Task.Delay(60);
            AssertStacking(previewHandle, explorerHandle, "same-item poll");
            Assert(Equals(generation, GetField(preview, "_representationGeneration")), "Same-item reactivation does not reload preview");
            AssertBounds(previewHandle, initialRect, "Same-item reactivation preserves preview geometry");

            // Changed selections exercise both raising before async loading and keeping
            // the resized/moved preview above Explorer after its content is replaced.
            foreach (var index in new[] { 1, 2, 0 })
            {
                Select(document, folder, names[index]);
                await CoverPreviewWithExplorerAsync(explorerHandle, previewHandle);
                Poll(application);
                await WaitAsync(() => CurrentText(preview) == contents[index], "updated text " + names[index]);
                AssertStacking(previewHandle, explorerHandle, "selection " + names[index]);
                AssertBounds(previewHandle, initialRect, "Selection preserves preview position and size");
            }

            other = new Window { Title = "WindowStackingSmoke unrelated window", Width = 500, Height = 300,
                Left = 150, Top = 150, Content = new TextBlock { Text = "Unrelated foreground fixture", Margin = new Thickness(20) } };
            other.Show();
            var otherHandle = new WindowInteropHelper(other).Handle;
            await ForegroundAsync(otherHandle);
            _ = SetWindowPos(otherHandle, IntPtr.Zero, 0, 0, 0, 0, NoMoveNoSizeNoActivate);
            Assert(IsAbove(otherHandle, previewHandle), "Unrelated foreground window initially covers preview");
            KeepAbove(preview);
            Poll(application);
            await Task.Delay(50);
            Assert(GetForegroundWindow() == otherHandle && IsAbove(otherHandle, previewHandle), "Unrelated foreground app is not covered or deactivated");
            Assert((GetWindowLong(previewHandle, -20) & 0x8) == 0, "Preview remains outside topmost band when another app is active");
            other.Close(); other = null;

            preview.HidePreview();
            await ForegroundAsync(explorerHandle);
            _ = CallExplorer("TryGetSelectedPath");
            KeepAbove(preview);
            Poll(application);
            await Task.Delay(50);
            Assert(!preview.IsVisible && !IsWindowVisible(previewHandle), "Hidden preview is not restored by stacking guard");
            Assert(GetForegroundWindow() == explorerHandle, "Hidden guard preserves Explorer foreground");

            preview.ShowPreview(Path.Combine(fixture, names[0]), activateWindow: false);
            await WaitAsync(() => CurrentText(preview) == contents[0], "preview before minimization");
            preview.WindowState = WindowState.Minimized;
            await ForegroundAsync(explorerHandle);
            _ = CallExplorer("TryGetSelectedPath");
            Assert(IsIconic(previewHandle), "Preview is natively minimized before guard");
            KeepAbove(preview);
            Poll(application);
            await Task.Delay(60);
            Assert(IsIconic(previewHandle) && preview.WindowState == WindowState.Minimized, "Minimized preview is not restored by stacking guard");
            Assert(GetForegroundWindow() == explorerHandle, "Minimized guard preserves Explorer foreground");
            Console.WriteLine($"PASS: Real native Z-order/foreground stacking regression, {_assertions} assertions.");
        }
        finally
        {
            other?.Close();
            preview?.HidePreview();
            CallExplorer("ForgetOriginWindow");
            if (ownsExplorer && fixtureExplorer is not null)
            {
                try { ((dynamic)fixtureExplorer).Quit(); } catch (COMException) { }
            }
            Release(folder); Release(document); Release(fixtureExplorer); Release(shellWindows); Release(shell);
        }
    }

    private static async Task CoverPreviewWithExplorerAsync(IntPtr explorer, IntPtr preview)
    {
        await ForegroundAsync(explorer);
        _ = SetWindowPos(explorer, IntPtr.Zero, 0, 0, 0, 0, NoMoveNoSizeNoActivate);
        await Task.Delay(40);
        Assert(GetForegroundWindow() == explorer && IsAbove(explorer, preview), "Reproduced foreground Explorer covering preview");
    }

    private static void AssertStacking(IntPtr preview, IntPtr explorer, string description)
    {
        Assert(IsAbove(preview, explorer), description + ": preview above Explorer in actual native Z-order");
        Assert(GetForegroundWindow() == explorer, description + ": Explorer keeps keyboard focus");
        Assert((GetWindowLong(preview, -20) & 0x8) == 0, description + ": preview is not topmost");
    }

    private static void AssertBounds(IntPtr preview, NativeRect expected, string description) =>
        Assert(GetWindowRect(preview, out var actual) && actual.Equals(expected), description);
    private static bool Overlaps(NativeRect a, NativeRect b) => a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;

    private static bool IsAbove(IntPtr above, IntPtr below)
    {
        var current = below;
        for (var index = 0; index < 10_000; index++)
        {
            current = GetWindow(current, 3); // GW_HWNDPREV: higher in native Z-order.
            if (current == IntPtr.Zero) return false;
            if (current == above) return true;
        }
        throw new InvalidOperationException("Native Z-order enumeration did not terminate.");
    }

    private static async Task ForegroundAsync(IntPtr target)
    {
        if (GetForegroundWindow() == target) return;
        _ = SetForegroundWindow(target);
        if (GetForegroundWindow() != target)
        {
            // Test-only focus setup. Attach briefly to the foreground input queue so
            // Windows foreground-lock policy cannot make this test nondeterministic.
            var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
            var targetThread = GetWindowThreadProcessId(target, IntPtr.Zero);
            var currentThread = GetCurrentThreadId();
            var attached = foregroundThread != 0 && foregroundThread != currentThread && AttachThreadInput(currentThread, foregroundThread, true);
            var attachedTarget = targetThread != 0 && targetThread != currentThread && targetThread != foregroundThread && AttachThreadInput(currentThread, targetThread, true);
            try
            {
                _ = ShowWindow(target, 9);
                _ = BringWindowToTop(target);
                _ = SetActiveWindow(target);
                _ = SetForegroundWindow(target);
            }
            finally
            {
                if (attachedTarget) _ = AttachThreadInput(currentThread, targetThread, false);
                if (attached) _ = AttachThreadInput(currentThread, foregroundThread, false);
            }
            if (GetForegroundWindow() != target) SwitchToThisWindow(target, true);
        }
        await Task.Delay(60);
        Console.WriteLine($"Foreground setup: target=0x{target.ToInt64():X}, actual=0x{GetForegroundWindow().ToInt64():X}");
        Assert(GetForegroundWindow() == target, "Native foreground fixture setup succeeds");
    }

    private static async Task<object> FindFixtureExplorerAsync(object windows, string path)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(15))
        {
            for (var index = 0; index < (int)((dynamic)windows).Count; index++)
            {
                object? candidate = null, document = null, folder = null, self = null;
                try
                {
                    candidate = ((dynamic)windows).Item(index);
                    document = ((dynamic)candidate!).Document;
                    folder = ((dynamic)document).Folder;
                    self = ((dynamic)folder).Self;
                    if (string.Equals((string)((dynamic)self).Path, path, StringComparison.OrdinalIgnoreCase))
                    {
                        var result = candidate!; candidate = null; return result;
                    }
                }
                catch (COMException) { }
                finally { Release(self); Release(folder); Release(document); Release(candidate); }
            }
            await Task.Delay(80);
        }
        throw new TimeoutException("Dedicated fixture Explorer window was not found.");
    }

    private static HashSet<IntPtr> ExplorerHandles(object windows)
    {
        var handles = new HashSet<IntPtr>();
        for (var index = 0; index < (int)((dynamic)windows).Count; index++)
        {
            object? window = null;
            try { window = ((dynamic)windows).Item(index); handles.Add(new IntPtr(Convert.ToInt64(((dynamic)window!).HWND))); }
            catch (COMException) { }
            finally { Release(window); }
        }
        return handles;
    }

    private static void Select(object document, object folder, string name)
    {
        object? item = null;
        try { item = ((dynamic)folder).ParseName(name); ((dynamic)document).SelectItem(item, 0x1 | 0x4 | 0x8 | 0x10); }
        finally { Release(item); }
    }

    private static string CurrentText(MainWindow window) => ((TextBox)window.FindName("PreviewText")).Text;
    private static object? GetField(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
    private static void SetField(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static void Poll(App application) => typeof(App).GetMethod("SynchronizeExplorerSelection", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(application, null);
    private static void KeepAbove(MainWindow window) => typeof(MainWindow).GetMethod("KeepAboveOriginExplorer", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
    private static object? CallExplorer(string name) => ExplorerService.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null);
    private static async Task WaitAsync(Func<bool> predicate, string operation)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException(operation);
            await Task.Delay(20);
        }
    }
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
    private static void Assert(bool condition, string description) { if (!condition) throw new Exception("FAIL: " + description); _assertions++; Console.WriteLine("PASS: " + description); }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, IntPtr processId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr SetActiveWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern void SwitchToThisWindow(IntPtr window, bool altTab);
}
