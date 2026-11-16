using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using QuickPreview;

internal static class Program
{
    private static readonly Type ExplorerService = typeof(App).Assembly.GetType("QuickPreview.ExplorerSelectionService", true)!;
    private static readonly List<Exception> DispatcherErrors = new();
    private static int _exitCode, _checks, _visibilityDrops, _nativeHides, _nativeShows, _loadingEntries;
    private static bool _trackWindow;
    private static string? _lastSuccessfulPdfSource;

    [STAThread]
    private static int Main(string[] args)
    {
        var app = new App();
        app.InitializeComponent();
        var startup = typeof(App).GetMethod("App_OnStartup", BindingFlags.Instance | BindingFlags.NonPublic)!;
        app.Startup -= (StartupEventHandler)Delegate.CreateDelegate(typeof(StartupEventHandler), app, startup);
        app.DispatcherUnhandledException += (_, args) =>
        {
            DispatcherErrors.Add(args.Exception); args.Handled = true;
            Console.Error.WriteLine("UNHANDLED DISPATCHER: " + args.Exception);
        };
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                if (args.Contains("--keyboard-fixture", StringComparer.Ordinal)) await HoldKeyboardFixtureAsync();
                else await RunAsync(app);
            }
            catch (Exception error) { Console.Error.WriteLine(error); _exitCode = 1; }
            finally { _trackWindow = false; app.Shutdown(); Dispatcher.ExitAllFrames(); }
        }));
        Dispatcher.Run();
        return _exitCode;
    }

    private static async Task HoldKeyboardFixtureAsync()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "keyboard-fixtures", Guid.NewGuid().ToString("N"));
        var files = Fixtures.Create(directory);
        using var explorer = await NativeExplorerFixture.OpenAsync(directory);
        explorer.UseDetailsView();
        explorer.Select(Path.GetFileName(files[0].Path));
        await explorer.ForegroundAsync();
        var stop = Path.Combine(AppContext.BaseDirectory, "keyboard-stop-" + Guid.NewGuid().ToString("N") + ".flag");
        Console.WriteLine("FIXTURE_FOLDER=" + directory);
        Console.WriteLine("FIXTURE_TITLE=" + Path.GetFileName(directory));
        Console.WriteLine($"FIXTURE_HWND=0x{explorer.Handle.ToInt64():X}");
        Console.WriteLine("FIXTURE_STOP_FILE=" + stop);
        Console.WriteLine("READY: own Explorer with21 PDF/archive/3D files selected at00 PDF; no test preview, keyboard hook, or startup changes. Holding up to180seconds; create STOP_FILE to finish early.");
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(180) && !File.Exists(stop)) await Task.Delay(1000);
        Console.WriteLine("DONE: closing only owned fixture Explorer.");
    }

    private static async Task RunAsync(App app)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "rapid-fixtures", Guid.NewGuid().ToString("N"));
        var files = Fixtures.Create(directory);
        OffsetExchangeFixtures(files);
        using var explorer = await NativeExplorerFixture.OpenAsync(directory);
        explorer.UseDetailsView();
        MainWindow? preview = null;
        DispatcherTimer? selectionTimer = null;
        try
        {
            explorer.Select(Path.GetFileName(files[0].Path));
            await explorer.ForegroundAsync();
            await Task.Delay(60);
            preview = new MainWindow();
            app.MainWindow = preview;
            Field(typeof(App), "_previewWindow").SetValue(app, preview);
            var pdf = Control<WebView2>(preview, "PdfWebView");
            pdf.CoreWebView2InitializationCompleted += (_, e) =>
            {
                if (!e.IsSuccess) return;
                pdf.CoreWebView2.NavigationCompleted += (_, navigation) =>
                {
                    if (navigation.IsSuccess && Uri.TryCreate(pdf.CoreWebView2.Source, UriKind.Absolute, out var uri) && uri.IsFile)
                        _lastSuccessfulPdfSource = uri.LocalPath;
                };
            };
            CoreWebView2Environment.SetLoaderDllFolderPath(AppContext.BaseDirectory);
            Field(typeof(MainWindow), "_webEnvironmentTask").SetValue(preview,
                CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(AppContext.BaseDirectory, "WebView2-test")));
            preview.IsVisibleChanged += (_, e) => { if (_trackWindow && !(bool)e.NewValue) _visibilityDrops++; };
            Control<FrameworkElement>(preview, "LoadingView").IsVisibleChanged += (_, e) => { if (_trackWindow && (bool)e.NewValue) _loadingEntries++; };
            preview.SourceInitialized += (_, _) =>
            {
                HwndSource.FromHwnd(new WindowInteropHelper(preview).Handle)?.AddHook((IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                {
                    if (_trackWindow && message == 0x0018) { if (wParam == IntPtr.Zero) _nativeHides++; else _nativeShows++; }
                    return IntPtr.Zero;
                });
            };

            // Exercise the real Space handler. The fixture app/window is never activated
            // or manually raised: Explorer is foreground before the first open and reopen.
            Toggle(app);
            await AssertOpeningAsync(preview, explorer.Handle, "FIRST Space open");
            await WaitForContentAsync(preview, files[0]);
            preview.HidePreview();
            Check(!(bool)ExplorerService.GetProperty("HasOriginWindow", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!, "Hide forgets origin before Space reopen");
            await explorer.ForegroundAsync();
            Toggle(app);
            await AssertOpeningAsync(preview, explorer.Handle, "Space reopen after Hide/ForgetOrigin");
            await WaitForContentAsync(preview, files[0]);
            Check((string?)ExplorerCall("TryGetSelectedPathInOriginWindow") == files[0].Path, "Space handler reacquires actual Explorer origin selection");

            // Startup is deliberately disabled, so recreate only the production
            // timer after first-open/reopen assertions. Per-arrow Poll calls can
            // be coalesced while a read is pending or before its 120/360ms deadline;
            // the timer must still observe the final selection after keys stop.
            selectionTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(120)
            };
            selectionTimer.Tick += (_, _) => Poll(app);
            selectionTimer.Start();

            preview.Width = 760; preview.Height = 520; preview.Left = 120; preview.Top = 100;
            preview.UpdateLayout(); await Task.Delay(60);
            var handle = new WindowInteropHelper(preview).Handle;
            Check(Native.GetWindowRect(handle, out var baseline), "Native baseline preview bounds available");
            _trackWindow = true;

            foreach (var interval in new[] { 20, 50, 120 })
            {
                explorer.Select(Path.GetFileName(files[0].Path));
                await explorer.ForegroundAsync();
                Poll(app);
                await WaitForContentAsync(preview, files[0]);
                var currentIndex = 0;
                var durations = new List<double>();
                var loadingBefore = _loadingEntries;
                // Each burst includes different file families, then stops on a specific
                // target. Returning backwards catches stale render callbacks and ABA paths.
                foreach (var target in new[] { 20, 14, 15, 16, 20, 19, 16, 17, 18, 19, 20 })
                {
                    while (currentIndex != target)
                    {
                        var started = Stopwatch.GetTimestamp();
                        var offset = Math.Sign(target - currentIndex);
                        currentIndex += offset;
                        var selected = (string?)ExplorerCall("TryNavigateInOriginWindow", Enum.Parse(typeof(App).Assembly.GetType("QuickPreview.NavigationDirection", true)!, offset < 0 ? "Up" : "Down"));
                        Check(string.Equals(selected, files[currentIndex].Path, StringComparison.OrdinalIgnoreCase), "Native Explorer view-order navigation matches target", quiet: true);
                        Poll(app);
                        AssertNative(preview, explorer.Handle, handle, baseline);
                        var elapsed = (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency;
                        durations.Add(elapsed);
                        await Task.Delay(Math.Max(1, interval - (int)elapsed));
                    }
                    var readyWatch = Stopwatch.StartNew();
                    var fingerprint = await WaitForContentAsync(preview, files[target]);
                    AssertNative(preview, explorer.Handle, handle, baseline);
                    // Old reads, worker imports, PDF navigations and WebView callbacks
                    // must not replace the latest selection after it has become ready.
                    await Task.Delay(750);
                    Poll(app);
                    var settled = await ContentFingerprintAsync(preview, files[target]);
                    Check(settled == fingerprint, "Latest content remains correct after old operations settle");
                    if (files[target].Triangles > 0) await AssertModelPixelsAndFramingAsync(preview, files[target]);
                    AssertNative(preview, explorer.Handle, handle, baseline);
                    Console.WriteLine($"PASS: {interval}ms arrows → {Path.GetFileName(files[target].Path)}; ready {readyWatch.ElapsedMilliseconds - 750}ms; content stable");
                }
                Console.WriteLine($"METRIC: requested {interval}ms spacing; native navigation+poll mean {durations.Average():F1}ms; LoadingView entries {_loadingEntries - loadingBefore}; window hides/shows {_nativeHides}/{_nativeShows}");
            }
            Check(_visibilityDrops == 0 && _nativeHides == 0 && _nativeShows == 0, "Rapid navigation never hides/shows or recreates preview window");
            Check(DispatcherErrors.Count == 0, "No stale asynchronous callback raises an unhandled dispatcher exception");
            Console.WriteLine($"PASS: Rapid selection, first/reopen stacking, PDF/archive/3D content identity; {_checks} checks.");
        }
        finally
        {
            selectionTimer?.Stop();
            _trackWindow = false;
            preview?.HidePreview();
            ExplorerCall("ForgetOriginWindow");
        }
    }

    private static async Task AssertOpeningAsync(MainWindow window, IntPtr explorer, string description)
    {
        await WaitAsync(() => window.IsVisible, description + " visible");
        await window.Dispatcher.InvokeAsync(() =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            Check(Native.IsAbove(handle, explorer), description + ": above Explorer at Dispatcher.Loaded");
            Check(Native.GetForegroundWindow() == explorer, description + ": Explorer retains foreground at first frame");
            Check((Native.GetWindowLong(handle, -20) & 0x8) == 0, description + ": no persistent topmost flag");
        }, DispatcherPriority.Loaded);
        await Task.Delay(80);
        var afterFrame = new WindowInteropHelper(window).Handle;
        Check(Native.IsAbove(afterFrame, explorer) && Native.GetForegroundWindow() == explorer, description + ": stacking/focus survives next frame before poll");
        ((App)Application.Current).GetType().GetMethod("SynchronizeExplorerSelection", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Application.Current, null);
        Check(Native.IsAbove(afterFrame, explorer) && Native.GetForegroundWindow() == explorer, description + ": stacking/focus after production poll");
    }

    private static void AssertNative(MainWindow window, IntPtr explorer, IntPtr originalHandle, Native.Rect baseline)
    {
        var handle = new WindowInteropHelper(window).Handle;
        Check(handle == originalHandle && window.IsVisible && Native.IsWindowVisible(handle), "Preview remains visible with the same native HWND", quiet: true);
        Check(Native.GetForegroundWindow() == explorer, "Rapid preview update does not steal Explorer foreground", quiet: true);
        Check(Native.IsAbove(handle, explorer), "Rapid preview remains above Explorer", quiet: true);
        Check((Native.GetWindowLong(handle, -20) & 0x8) == 0, "Rapid preview remains outside topmost band", quiet: true);
        Check(Native.GetWindowRect(handle, out var rect) && rect.Equals(baseline), "Rapid updates preserve native position and size", quiet: true);
    }

    private static async Task<string> WaitForContentAsync(MainWindow window, FixtureFile expected)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(40))
        {
            if (Control<FrameworkElement>(window, "ErrorView").IsVisible)
                throw new Exception($"Unexpected error for {Path.GetFileName(expected.Path)}: {Control<TextBlock>(window, "ErrorText").Text}");
            var fingerprint = await ContentFingerprintAsync(window, expected);
            if (fingerprint is not null)
            {
                if (expected.Triangles > 0) await AssertModelPixelsAndFramingAsync(window, expected);
                return fingerprint;
            }
            await Task.Delay(20);
        }
        throw new TimeoutException($"Latest content {expected.Path}; header={Control<TextBlock>(window, "FileNameText").Text}; current={Value(window, "_currentPath")}");
    }

    private static async Task<string?> ContentFingerprintAsync(MainWindow window, FixtureFile expected)
    {
        if (!string.Equals((string?)Value(window, "_currentPath"), expected.Path, StringComparison.OrdinalIgnoreCase) ||
            Control<TextBlock>(window, "FileNameText").Text != Path.GetFileName(expected.Path)) return null;
        if (expected.ArchiveSentinel is { } sentinel)
        {
            if (!Control<FrameworkElement>(window, "ArchiveView").IsVisible) return null;
            var names = Control<ListView>(window, "ArchiveEntriesList").Items.Cast<object>()
                .Select(item => (string?)item.GetType().GetProperty("Name")?.GetValue(item)).ToArray();
            return names.Length == 1 && names[0] == sentinel ? "archive:" + sentinel : null;
        }
        if (expected.Extension == ".pdf")
        {
            if (Control<FrameworkElement>(window, "NativePdfView").IsVisible)
            {
                var document = Value(window, "_nativePdf");
                if (document is null || Control<Image>(window, "NativePdfImage").Source is null) return null;
                return Convert.ToUInt32(document.GetType().GetProperty("PageCount")!.GetValue(document)) == 2 ? "native-pdf:" + expected.Path : null;
            }
            var pdf = Control<WebView2>(window, "PdfWebView");
            if (!Control<FrameworkElement>(window, "PdfView").IsVisible || pdf.CoreWebView2 is null ||
                !Uri.TryCreate(pdf.CoreWebView2.Source, UriKind.Absolute, out var uri) || !uri.IsFile ||
                !string.Equals(uri.LocalPath, expected.Path, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(_lastSuccessfulPdfSource, expected.Path, StringComparison.OrdinalIgnoreCase)) return null;
            return "web-pdf:" + uri.AbsoluteUri;
        }
        if (!Control<FrameworkElement>(window, "ModelView").IsVisible) return null;
        var session = Value(window, "_modelSession");
        if (session is null || session.GetType().GetProperty("ModelFiles", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)
            is not IReadOnlyDictionary<string, string> modelFiles || !modelFiles.Values.Contains(expected.Path, StringComparer.OrdinalIgnoreCase)) return null;
        var ready = (Task)session.GetType().GetProperty("Ready")!.GetValue(session)!;
        if (!ready.IsCompletedSuccessfully) return null;
        var model = Control<WebView2>(window, "ModelWebView");
        var viewerUri = (string)session.GetType().GetProperty("ViewerUri", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
        if (model.CoreWebView2 is null || !model.CoreWebView2.Source.StartsWith(viewerUri, StringComparison.Ordinal)) return null;
        var triangles = await model.CoreWebView2.ExecuteScriptAsync("window.quickPreviewModel?.triangles ?? -1");
        return triangles == expected.Triangles.ToString(System.Globalization.CultureInfo.InvariantCulture)
            ? "model:" + viewerUri + ":triangles=" + triangles : null;
    }

    private static async Task AssertModelPixelsAndFramingAsync(MainWindow window, FixtureFile expected)
    {
        var browser = Control<WebView2>(window, "ModelWebView");
        // Let production perform its first visible fit; never click Fit/Edges or
        // request a repair render before observing the user's initial camera.
        await browser.CoreWebView2.ExecuteScriptAsync("window.rapidModelSettled=false;requestAnimationFrame(()=>requestAnimationFrame(()=>requestAnimationFrame(()=>window.rapidModelSettled=true)))");
        var frameWait = Stopwatch.StartNew();
        while (await browser.CoreWebView2.ExecuteScriptAsync("window.rapidModelSettled") != "true")
        {
            if (frameWait.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Latest model first visible frames");
            await Task.Delay(15);
        }
        const string boundsScript = @"(()=>{
          const n=window.quickPreviewModel.viewer.GetViewer(),b=n.GetBoundingBox(()=>true),c=document.querySelector('#viewport canvas'),r=c.getBoundingClientRect(),center=b.min.clone().add(b.max).multiplyScalar(.5),p=center.clone().project(n.camera),target=n.GetCamera().center;
          let x=0,y=0,z=0,vertices=0,finite=true;
          n.mainModel.EnumerateMeshes(mesh=>{const a=mesh.geometry.getAttribute('position'),v=b.min.clone();for(let i=0;i<a.count;i++){
            v.fromBufferAttribute(a,i).applyMatrix4(mesh.matrixWorld).project(n.camera);vertices++;
            finite=finite&&Number.isFinite(v.x)&&Number.isFinite(v.y)&&Number.isFinite(v.z);
            x=Math.max(x,Math.abs(v.x));y=Math.max(y,Math.abs(v.y));z=Math.max(z,Math.abs(v.z));
          }});
          return{x,y,z,vertices,finite,centerX:p.x,centerY:p.y,targetError:Math.hypot(target.x-center.x,target.y-center.y,target.z-center.z)/Math.max(1,b.min.distanceTo(b.max)),width:c.width,height:c.height,left:r.left,top:r.top,right:r.right,bottom:r.bottom,innerWidth,innerHeight};
        })()";
        using var json = JsonDocument.Parse(await browser.CoreWebView2.ExecuteScriptAsync(boundsScript));
        var bounds = json.RootElement;
        Check(bounds.GetProperty("width").GetInt32() > 0 && bounds.GetProperty("height").GetInt32() > 0, "Latest model canvas has nonzero render dimensions", quiet: true);
        Check(bounds.GetProperty("finite").GetBoolean() && bounds.GetProperty("vertices").GetInt32() > 0,
            "Latest model contains finite transformed render vertices", quiet: true);
        Check(bounds.GetProperty("x").GetDouble() < 0.99 && bounds.GetProperty("y").GetDouble() < 0.99 && bounds.GetProperty("z").GetDouble() <= 1,
            "Latest model geometry fits final viewport before manual Fit", quiet: true);
        Check(Math.Abs(bounds.GetProperty("centerX").GetDouble()) < 0.02 && Math.Abs(bounds.GetProperty("centerY").GetDouble()) < 0.02 && bounds.GetProperty("targetError").GetDouble() < 0.00001,
            "Latest model is immediately centered on its transformed world bounds", quiet: true);
        await Task.Delay(40);
        using var capture = new MemoryStream();
        await browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, capture);
        capture.Position = 0;
        var frame = BitmapFrame.Create(capture, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var bitmap = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var stride = bitmap.PixelWidth * 4; var pixels = new byte[stride * bitmap.PixelHeight]; bitmap.CopyPixels(pixels, stride, 0);
        var sx = bitmap.PixelWidth / bounds.GetProperty("innerWidth").GetDouble();
        var sy = bitmap.PixelHeight / bounds.GetProperty("innerHeight").GetDouble();
        var left = Math.Clamp((int)(bounds.GetProperty("left").GetDouble() * sx) + 3, 0, bitmap.PixelWidth - 1);
        var right = Math.Clamp((int)(bounds.GetProperty("right").GetDouble() * sx) - 3, left, bitmap.PixelWidth - 1);
        var top = Math.Clamp((int)(bounds.GetProperty("top").GetDouble() * sy) + 3, 0, bitmap.PixelHeight - 1);
        var bottom = Math.Clamp((int)(bounds.GetProperty("bottom").GetDouble() * sy) - 3, top, bitmap.PixelHeight - 1);
        var bg = top * stride + left * 4; var colored = 0;
        for (var y = top; y <= bottom; y += 2) for (var x = left; x <= right; x += 2)
        {
            var pixel = y * stride + x * 4;
            if (Math.Abs(pixels[pixel] - pixels[bg]) + Math.Abs(pixels[pixel+1] - pixels[bg+1]) + Math.Abs(pixels[pixel+2] - pixels[bg+2]) > 40) colored++;
        }
        Check(colored > 100, "Latest 3D canvas contains real non-background model pixels", quiet: true);
        var artifact = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../artifacts")); Directory.CreateDirectory(artifact);
        File.WriteAllBytes(Path.Combine(artifact, Path.GetFileName(expected.Path) + ".png"), capture.ToArray());
        File.WriteAllText(Path.Combine(artifact, Path.GetFileName(expected.Path) + ".framing.json"), json.RootElement.GetRawText());
    }

    private static void OffsetExchangeFixtures(IEnumerable<FixtureFile> files)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var file in files)
        {
            if (file.Extension == ".step")
            {
                var text = File.ReadAllText(file.Path);
                text = Regex.Replace(text, @"CARTESIAN_POINT\('([^']*)',\(([-+\d.Ee]+),([-+\d.Ee]+),([-+\d.Ee]+)\)\)", match =>
                {
                    var x = -420 + double.Parse(match.Groups[2].Value, culture) * 5.3;
                    var y = -80 + double.Parse(match.Groups[3].Value, culture);
                    var z = 310 + double.Parse(match.Groups[4].Value, culture) * 2.65;
                    return $"CARTESIAN_POINT('{match.Groups[1].Value}',({x.ToString("0.0########", culture)},{y.ToString("0.0########", culture)},{z.ToString("0.0########", culture)}))";
                });
                File.WriteAllText(file.Path, text);
            }
            else if (file.Extension == ".obj")
            {
                var text = File.ReadAllText(file.Path);
                text = Regex.Replace(text, @"(?m)^v ([-+\d.Ee]+) ([-+\d.Ee]+) ([-+\d.Ee]+)$", match =>
                {
                    var x = -420 + double.Parse(match.Groups[1].Value, culture) * 5.3;
                    var y = -80 + double.Parse(match.Groups[2].Value, culture);
                    var z = 310 + double.Parse(match.Groups[3].Value, culture) * 2.65;
                    return $"v {x.ToString(culture)} {y.ToString(culture)} {z.ToString(culture)}";
                });
                File.WriteAllText(file.Path, text);
            }
            else if (file.Extension == ".3mf")
            {
                using var zip = ZipFile.Open(file.Path, ZipArchiveMode.Update);
                var part = zip.GetEntry("3D/3dmodel.model")!;
                string text;
                using (var reader = new StreamReader(part.Open())) text = reader.ReadToEnd();
                part.Delete();
                using var writer = new StreamWriter(zip.CreateEntry("3D/3dmodel.model").Open());
                writer.Write(text.Replace("<item objectid=\"1\"/>", "<item objectid=\"1\" transform=\"0 1 0 -2 0 0 0 0 3 120 -75 43\"/>"));
            }
        }
    }

    private static void Toggle(App app) => typeof(App).GetMethod("TogglePreview", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(app, null);
    private static void Poll(App app) => typeof(App).GetMethod("SynchronizeExplorerSelection", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(app, null);
    private static object? ExplorerCall(string name, params object[] args) => ExplorerService.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(null, args);
    private static FieldInfo Field(Type type, string name) => type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static object? Value(MainWindow window, string name) => Field(typeof(MainWindow), name).GetValue(window);
    private static T Control<T>(MainWindow window, string name) where T : FrameworkElement => (T)window.FindName(name);
    private static async Task WaitAsync(Func<bool> predicate, string operation)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate()) { if (watch.Elapsed > TimeSpan.FromSeconds(12)) throw new TimeoutException(operation); await Task.Delay(15); }
    }
    private static void Check(bool condition, string description, bool quiet = false)
    {
        if (!condition) throw new Exception("FAIL: " + description); _checks++; if (!quiet) Console.WriteLine("PASS: " + description);
    }
}
