using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32.SafeHandles;
using QuickPreview;

internal static class Program
{
    private static readonly Stopwatch Duration = Stopwatch.StartNew();
    private static readonly List<string> Checks = new();
    private static readonly List<string> Errors = new();
    private static MainWindow? _window;
    private static WebView2? _model;
    private static WebView2? _pdf;
    private static int _exit;
    private const string CameraScript = "(()=>{const c=window.quickPreviewModel.viewer.GetViewer().GetCamera();return [c.eye.x,c.eye.y,c.eye.z,c.center.x,c.center.y,c.center.z,c.up.x,c.up.y,c.up.z];})()";
    private static readonly string TestRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../.."));

    [STAThread]
    private static int Main()
    {
        var app = new App();
        app.InitializeComponent();
        var startup = typeof(App).GetMethod("App_OnStartup", BindingFlags.Instance | BindingFlags.NonPublic)!;
        app.Startup -= (StartupEventHandler)Delegate.CreateDelegate(typeof(StartupEventHandler), app, startup);
        app.DispatcherUnhandledException += (_, args) => { Errors.Add(args.Exception.ToString()); _exit = 1; args.Handled = true; };
        var watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(28) };
        watchdog.Tick += (_, _) =>
        {
            watchdog.Stop();
            Errors.Add("Lifecycle suite exceeded its 28-second wall-clock budget.");
            _exit = 1;
            Cleanup();
            app.Shutdown();
            Dispatcher.ExitAllFrames();
        };
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            watchdog.Start();
            try { await RunAsync(app); }
            catch (Exception error) { Console.Error.WriteLine(error); Errors.Add(error.ToString()); _exit = 1; }
            finally
            {
                watchdog.Stop();
                Cleanup();
                var report = new { Passed = _exit == 0, DurationSeconds = Duration.Elapsed.TotalSeconds, Checks, Errors };
                Directory.CreateDirectory(Path.Combine(TestRoot, "artifacts"));
                File.WriteAllText(Path.Combine(TestRoot, "artifacts", "result.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"Lifecycle result: {Checks.Count} passed checks in {Duration.Elapsed.TotalSeconds:F1}s; errors={Errors.Count}.");
                app.Shutdown();
                Dispatcher.ExitAllFrames();
            }
        }));
        Dispatcher.Run();
        return _exit;
    }

    private static async Task RunAsync(App app)
    {
        CheckDecoderJob();
        var fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures");
        var step = Path.Combine(fixtures, "cube.step");
        var stl = Path.Combine(fixtures, "cube.stl");
        _window = new MainWindow();
        app.MainWindow = _window;
        _model = Control<WebView2>("ModelWebView");
        _pdf = Control<WebView2>("PdfWebView");
        CoreWebView2Environment.SetLoaderDllFolderPath(AppContext.BaseDirectory);
        var profile = Path.Combine(TestRoot, "artifacts", "profiles", Guid.NewGuid().ToString("N"));
        var environment = CoreWebView2Environment.CreateAsync(userDataFolder: profile);
        Field("_webEnvironmentTask").SetValue(_window, environment);

        _window.ShowPreview(step, activateWindow: false);
        await ModelReadyAsync();
        var firstSession = Session()!;
        var navigationCount = 0;
        _model.CoreWebView2.NavigationStarting += (_, _) => navigationCount++;
        await _model.CoreWebView2.ExecuteScriptAsync("window.lifecycleCacheProbe='preserved';document.querySelector('#viewport canvas').dispatchEvent(new WheelEvent('wheel',{deltaY:-160,bubbles:true,cancelable:true}));");
        await Task.Delay(100);
        var firstCamera = await CameraAsync();
        var baselineNavigations = navigationCount;

        Tabs.SelectedIndex = 1;
        await WaitAsync(() => _model.CoreWebView2.IsSuspended && Control<Grid>("SourceView").IsVisible, "Text tab suspends loaded model browser");
        Check(ReferenceEquals(firstSession, Session()), "Text tab retains the loaded model session.");
        Tabs.SelectedIndex = 0;
        await WaitAsync(() => !_model.CoreWebView2.IsSuspended && Control<Grid>("ModelView").IsVisible, "Visual tab resumes model browser");
        Check(ReferenceEquals(firstSession, Session()) && await CachePreservedAsync(), "Text to Visual preserves session and browser cache.");
        Check(SameCamera(firstCamera, await CameraAsync()) && navigationCount == baselineNavigations, "Text to Visual preserves deliberate camera zoom without browser navigation.");

        _window.WindowState = WindowState.Minimized;
        await WaitAsync(() => _model.CoreWebView2.IsSuspended, "Loaded minimized model browser suspends");
        Check(ReferenceEquals(firstSession, Session()) && !CurrentToken.IsCancellationRequested, "Minimizing a ready model keeps its session and live token.");
        _window.WindowState = WindowState.Normal;
        await WaitAsync(() => !_model.CoreWebView2.IsSuspended && Control<Grid>("ModelView").IsVisible, "Loaded model resumes after restore");
        Check(ReferenceEquals(firstSession, Session()) && await CachePreservedAsync() && SameCamera(firstCamera, await CameraAsync()), "Restoring a loaded model preserves session, cache and camera.");

        // Hold initialization after CreateSessionAsync, without letting the session
        // attach to WebView. Minimize must cancel that pending load, not finish it hidden.
        var originalInitialization = Field("_modelInitializationTask").GetValue(_window);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Field("_modelInitializationTask").SetValue(_window, gate.Task);
        try
        {
            _window.ShowPreview(stl, activateWindow: false);
            await WaitAsync(() => Flag("_modelLoading") && Session() is null && Tabs.IsEnabled, "Pending model initialization is gated");
            Tabs.SelectedIndex = 1;
            await WaitAsync(() => Control<Grid>("SourceView").IsVisible && Control<TextBox>("SourcePreviewText").Text.Contains("solid"), "Source remains usable while model initialization waits");
            var pendingToken = CurrentToken;
            _window.WindowState = WindowState.Minimized;
            Check(pendingToken.IsCancellationRequested, "Minimizing a pending model cancels its request token.");
            await WaitAsync(() => !Flag("_modelLoading") && Session() is null, "Pending model session is not retained while minimized");
            gate.TrySetResult();
            await Task.Delay(150);
            Check(Session() is null && _model.CoreWebView2.Source == "about:blank", "Releasing old initialization cannot attach or finish a hidden model.");
            _window.WindowState = WindowState.Normal;
            await ModelReadyAsync();
            Check(!CurrentToken.IsCancellationRequested && Tabs.SelectedIndex == 1 && Control<Grid>("SourceView").IsVisible, "Restore reloads a cancelled request and restores the Text tab.");
            await WaitAsync(() => _model.CoreWebView2.IsSuspended, "Reloaded model stays suspended behind restored Text tab");
            Tabs.SelectedIndex = 0;
            await WaitAsync(() => !_model.CoreWebView2.IsSuspended && Control<Grid>("ModelView").IsVisible, "Reloaded visual model can resume");
        }
        finally
        {
            gate.TrySetResult();
            Field("_modelInitializationTask").SetValue(_window, originalInitialization);
        }

        // Exact regression window: minimization in the 80ms coalescing delay,
        // before either browser-specific loading flag has been set.
        Method("UpdatePreviewFromExplorer").Invoke(_window, new object[] { step });
        var delayedToken = CurrentToken;
        Check(Flag("_previewLoading") && !Flag("_modelLoading") && Session() is null, "New selection is still in the pre-parser coalescing delay.");
        _window.WindowState = WindowState.Minimized;
        Check(delayedToken.IsCancellationRequested, "Immediate minimize cancels the coalescing request.");
        await Task.Delay(180);
        Check(Session() is null && _model.CoreWebView2.Source == "about:blank", "No model attaches after the cancelled coalescing delay.");
        _window.WindowState = WindowState.Normal;
        await ModelReadyAsync();
        Check(!CurrentToken.IsCancellationRequested && Control<Grid>("ModelView").IsVisible, "Restore reloads the pre-parser-cancelled selection.");

        var beforeHide = Session()!;
        _window.HidePreview();
        await WaitAsync(() => Session() is null && _model.CoreWebView2.Source == "about:blank" && _model.CoreWebView2.IsSuspended, "Hide disposes model, blanks its document and suspends browser");
        Check((bool)beforeHide.GetType().GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(beforeHide)!, "Hidden model session is actually disposed.");

        await CheckPdfAsync(Path.Combine(fixtures, "preview.pdf"), fixtures);
    }

    private static async Task CheckPdfAsync(string pdfPath, string fixtureDirectory)
    {
        _window!.ShowPreview(pdfPath, activateWindow: false);
        await WaitAsync(() => Control<Grid>("PdfView").IsVisible || Control<Grid>("NativePdfView").IsVisible, "PDF becomes ready", 6000);
        if (Control<Grid>("NativePdfView").IsVisible)
        {
            Console.WriteLine("SKIP: browser PDF lifecycle uses the Windows native PDF fallback on this runtime.");
            _window.HidePreview();
            return;
        }
        Check(_pdf!.CoreWebView2.Source.StartsWith("file:", StringComparison.OrdinalIgnoreCase), "PDF browser displays the selected local document.");
        var text = Path.Combine(fixtureDirectory, "local.txt");
        File.WriteAllText(text, "Only the local lifecycle test owns this fixture.");
        _window.ShowPreview(text, activateWindow: false);
        await WaitAsync(() => Control<Grid>("TextView").IsVisible && _pdf.CoreWebView2.Source == "about:blank" && _pdf.CoreWebView2.IsSuspended, "Leaving PDF blanks and suspends its browser");
        _window.ShowPreview(pdfPath, activateWindow: false);
        await WaitAsync(() => Control<Grid>("PdfView").IsVisible && !_pdf.CoreWebView2.IsSuspended, "PDF resumes for another document request");
        _window.HidePreview();
        await WaitAsync(() => _pdf.CoreWebView2.Source == "about:blank" && _pdf.CoreWebView2.IsSuspended, "Hiding PDF releases document and suspends browser");
    }

    private static async Task ModelReadyAsync()
    {
        await WaitAsync(() => Session() is { } session && Ready(session).IsCompleted, "Model reaches Ready", 8000);
        await Ready(Session()!);
        await WaitAsync(() => !Flag("_previewLoading") && !Flag("_modelLoading"), "Model loading flags clear after Ready");
    }

    private static async Task WaitAsync(Func<bool> condition, string description, int timeout = 4000)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (Duration.Elapsed > TimeSpan.FromSeconds(26) || elapsed.ElapsedMilliseconds >= timeout)
                throw new TimeoutException(description + "; state=" + State());
            if (_window is not null && Control<Grid>("ErrorView").IsVisible)
                throw new InvalidOperationException(description + ": " + Control<TextBlock>("ErrorText").Text);
            await Task.Delay(20);
        }
        Check(true, description + ".");
    }

    private static async Task<double[]> CameraAsync() =>
        JsonSerializer.Deserialize<double[]>(await _model!.CoreWebView2.ExecuteScriptAsync(CameraScript).WaitAsync(TimeSpan.FromSeconds(2)))!;
    private static async Task<bool> CachePreservedAsync() =>
        await _model!.CoreWebView2.ExecuteScriptAsync("window.lifecycleCacheProbe").WaitAsync(TimeSpan.FromSeconds(2)) == "\"preserved\"";
    private static bool SameCamera(double[] left, double[] right) => left.Length == right.Length && left.Zip(right).All(values => Math.Abs(values.First - values.Second) < 0.000001);
    private static TabControl Tabs => Control<TabControl>("PreviewTabs");
    private static object? Session() => Field("_modelSession").GetValue(_window);
    private static Task Ready(object session) => (Task)session.GetType().GetProperty("Ready")!.GetValue(session)!;
    private static CancellationToken CurrentToken => ((CancellationTokenSource)Field("_previewCancellation").GetValue(_window)!).Token;
    private static bool Flag(string name) => (bool)Field(name).GetValue(_window)!;
    private static FieldInfo Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new MissingFieldException(name);
    private static MethodInfo Method(string name) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new MissingMethodException(name);
    private static T Control<T>(string name) where T : class => (T)_window!.FindName(name);
    private static string State() => _window is null ? "no window" : $"visible={_window.IsVisible},state={_window.WindowState},previewLoading={Flag("_previewLoading")},modelLoading={Flag("_modelLoading")},session={Session() is not null},source={_model?.CoreWebView2?.Source}";

    private static void CheckDecoderJob()
    {
        using var host = Process.GetCurrentProcess();
        var priority = host.PriorityClass;
        var type = typeof(App).Assembly.GetType("QuickPreview.IsolatedPreviewWorker+PreviewJob", throwOnError: true)!;
        using var job = (IDisposable)Activator.CreateInstance(type, nonPublic: true)!;
        var handle = (SafeFileHandle)type.GetField("_handle", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(job)!;
        if (!QueryCpu(handle, 15, out var cpu, (uint)Marshal.SizeOf<CpuLimits>(), out _))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        Check(cpu.Flags == 5 && cpu.Rate == 2500, "Kernel decoder job enforces ENABLE|HARD_CAP with 25% CPU rate.");
        if (!QueryExtended(handle, 9, out var limits, (uint)Marshal.SizeOf<ExtendedLimits>(), out _))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        Check((limits.Basic.Flags & (0x2000 | 0x100 | 0x8 | 0x20)) == (0x2000 | 0x100 | 0x8 | 0x20) &&
            limits.Basic.PriorityClass == 0x4000 && limits.Basic.ActiveProcessLimit == 1 &&
            limits.ProcessMemoryLimit.ToUInt64() == 1024UL * 1024 * 1024,
            "Kernel decoder job stores BelowNormal, one-process, 1GiB and kill-on-close limits.");
        host.Refresh();
        Check(host.PriorityClass == priority, "Creating/querying decoder job does not change host process priority.");
    }

    private static void Cleanup()
    {
        try { _window?.HidePreview(); } catch (Exception error) { Errors.Add(error.GetType().Name); }
        try { _model?.Dispose(); _pdf?.Dispose(); } catch (Exception error) { Errors.Add(error.GetType().Name); }
    }
    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception("FAIL: " + message);
        Checks.Add(message);
        Console.WriteLine("PASS: " + message);
    }

    [StructLayout(LayoutKind.Sequential)] private struct CpuLimits { public uint Flags, Rate; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    {
        public long ProcessTime, JobTime; public uint Flags;
        public UIntPtr MinWorkingSet, MaxWorkingSet; public uint ActiveProcessLimit;
        public UIntPtr Affinity; public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
    {
        public BasicLimits Basic; public IoCounters Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryCpu(SafeFileHandle job, int informationClass, out CpuLimits limits, uint size, out uint returned);
    [DllImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryExtended(SafeFileHandle job, int informationClass, out ExtendedLimits limits, uint size, out uint returned);
}
