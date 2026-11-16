using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using QuickPreview;
using Color = System.Windows.Media.Color;
using TabControl = System.Windows.Controls.TabControl;
using TextBox = System.Windows.Controls.TextBox;

internal static class Program
{
    private static readonly string ProjectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
    private static readonly List<object> Manifest = new();
    private static MainWindow? _preview;
    private static Window? _backdrop;
    private static int _exitCode;

    [STAThread]
    private static int Main()
    {
        var app = new App(); app.InitializeComponent();
        var startup = typeof(App).GetMethod("App_OnStartup", BindingFlags.Instance | BindingFlags.NonPublic)!;
        app.Startup -= (StartupEventHandler)Delegate.CreateDelegate(typeof(StartupEventHandler), app, startup);
        app.DispatcherUnhandledException += (_, e) => { Console.Error.WriteLine(e.Exception); e.Handled = true; _exitCode = 1; };
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try { await RunAsync(app); }
            catch (Exception error) { Console.Error.WriteLine(error); _exitCode = 1; }
            finally
            {
                if (_preview is not null)
                {
                    _preview.HidePreview();
                    typeof(MainWindow).GetMethod("StopOriginLifetimeMonitoring", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_preview, null);
                    foreach (var name in new[] { "ModelWebView", "PdfWebView", "SpreadsheetWebView" })
                        (_preview.FindName(name) as WebView2)?.Dispose();
                    _preview.Topmost = false;
                }
                _backdrop?.Close();
                Environment.SetEnvironmentVariable("QUICKPREVIEW_TEST_THEME", null);
                app.Shutdown(); Dispatcher.ExitAllFrames();
            }
        }));
        Dispatcher.Run(); return _exitCode;
    }

    private static async Task RunAsync(App app)
    {
        if (!File.Exists(Path.Combine(ProjectRoot, "QuickPreview.csproj"))) throw new InvalidOperationException("Unexpected screenshot project location.");
        var fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "generated-demo-files");
        Directory.CreateDirectory(fixtureDirectory);
        var fixtures = DemoFixtures.Create(fixtureDirectory);
        var allowedPrefix = Path.GetFullPath(fixtureDirectory) + Path.DirectorySeparatorChar;
        foreach (var path in fixtures.Values)
            if (!Path.GetFullPath(path).StartsWith(allowedPrefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                throw new InvalidOperationException("Only newly generated demo files may be opened.");
        var hashes = fixtures.ToDictionary(pair => pair.Key, pair => Hash(pair.Value));
        var output = Path.Combine(ProjectRoot, "assets", "screenshots");
        Directory.CreateDirectory(output);

        var work = SystemParameters.WorkArea;
        _backdrop = new Window
        {
            Left = work.Left, Top = work.Top, Width = work.Width, Height = work.Height,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, Topmost = true, Background = new SolidColorBrush(Color.FromRgb(225, 229, 235)),
            Title = "QuickPreview generated screenshot backdrop"
        };
        _backdrop.Show();
        var themeType = typeof(App).Assembly.GetType("QuickPreview.ThemeService", true)!;
        using var theme = (IDisposable)Activator.CreateInstance(themeType, nonPublic: true)!;
        _preview = new MainWindow { Topmost = true };
        app.MainWindow = _preview;
        themeType.GetMethod("RegisterWindow")!.Invoke(theme, new object[] { _preview });
        CoreWebView2Environment.SetLoaderDllFolderPath(AppContext.BaseDirectory);
        var profile = Path.Combine(AppContext.BaseDirectory, "profiles", Guid.NewGuid().ToString("N"));
        Field("_webEnvironmentTask").SetValue(_preview, CoreWebView2Environment.CreateAsync(userDataFolder: profile));

        var scenarios = new[]
        {
            new Scenario("3d-model", "model", "light", "ModelView"),
            new Scenario("spreadsheet", "spreadsheet", "light", "SpreadsheetView"),
            new Scenario("pdf", "pdf", "light", "PdfView"),
            new Scenario("archive", "archive", "dark", "ArchiveView"),
            new Scenario("source-code", "json", "dark", "TextView"),
            new Scenario("image", "image", "dark", "ImageView"),
            new Scenario("word", "word", "light", "TextView"),
            new Scenario("step-model", "step", "light", "ModelView"),
            new Scenario("step-source", "step", "dark", "ModelView", Source: true)
        };
        foreach (var scenario in scenarios)
        {
            Environment.SetEnvironmentVariable("QUICKPREVIEW_TEST_THEME", scenario.Theme);
            themeType.GetMethod("ApplyTheme", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(theme, null);
            var path = fixtures[scenario.Fixture];
            _preview.ShowPreview(path);
            _preview.Topmost = false; _preview.Topmost = true; _preview.Activate();
            await WaitAsync(() => Control<FrameworkElement>(scenario.View).IsVisible && !(bool)Field("_previewLoading").GetValue(_preview)!, scenario.Name);
            _preview.Width = Math.Min(1040, work.Width - 48);
            _preview.Height = Math.Min(720, work.Height - 48);
            _preview.Left = work.Left + (work.Width - _preview.Width) / 2;
            _preview.Top = work.Top + (work.Height - _preview.Height) / 2;
            _preview.UpdateLayout();
            await Task.Delay(180);

            if (scenario.View == "ModelView")
            {
                var session = Field("_modelSession").GetValue(_preview)!;
                await ((Task)session.GetType().GetProperty("Ready")!.GetValue(session)!).WaitAsync(TimeSpan.FromSeconds(45));
                await SettleBrowserAsync(Control<WebView2>("ModelWebView"));
                if (scenario.Name == "3d-model")
                {
                    // Choose a closer camera just as a user can, without editing the image.
                    await Control<WebView2>("ModelWebView").CoreWebView2.ExecuteScriptAsync("(()=>{document.getElementById('viewport').dispatchEvent(new Event('pointerdown'));const n=window.quickPreviewModel.viewer.GetViewer(),c=n.GetCamera().Clone();for(const axis of ['x','y','z'])c.eye[axis]=c.center[axis]+(c.eye[axis]-c.center[axis])/1.45;n.SetCamera(c);})()");
                    await SettleBrowserAsync(Control<WebView2>("ModelWebView"));
                }
            }
            else if (scenario.View == "SpreadsheetView") await SettleBrowserAsync(Control<WebView2>("SpreadsheetWebView"));
            else if (scenario.View == "PdfView") await Task.Delay(800);
            if (scenario.Source)
            {
                Control<TabControl>("PreviewTabs").SelectedIndex = 1;
                await WaitAsync(() => Control<Grid>("SourceView").IsVisible && Control<TextBox>("SourcePreviewText").Text.Length > 0, "STEP source");
            }
            _preview.Topmost = true; _preview.Activate();
            await _preview.Dispatcher.InvokeAsync(_preview.UpdateLayout, DispatcherPriority.ApplicationIdle);
            await Task.Delay(150);
            var destination = Path.Combine(output, scenario.Name + ".png");
            var dimensions = CaptureOwnWindow(destination);
            if (Hash(path) != hashes[scenario.Fixture]) throw new InvalidOperationException("A demo file changed during preview.");
            Manifest.Add(new { Screenshot = Path.GetFileName(destination), DemoFile = Path.GetFileName(path),
                Theme = scenario.Theme, dimensions.Width, dimensions.Height, FixtureSha256 = hashes[scenario.Fixture], ScreenshotSha256 = Hash(destination) });
            Console.WriteLine($"Captured {scenario.Name}: {Path.GetFileName(path)}, {dimensions.Width}×{dimensions.Height}");
        }
        var manifestPath = Path.Combine(ProjectRoot, "tests", "GithubScreenshots", "artifacts", "manifest.json");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(new { DemoFilesOnly = true, Screenshots = Manifest }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"DONE: {Manifest.Count} native window screenshots; all demo inputs unchanged. {output}");
    }

    private static async Task WaitAsync(Func<bool> ready, string operation)
    {
        var clock = Stopwatch.StartNew();
        while (!ready())
        {
            if (Control<Grid>("ErrorView").IsVisible) throw new InvalidOperationException(operation + ": " + Control<TextBlock>("ErrorText").Text);
            if (clock.Elapsed > TimeSpan.FromSeconds(50)) throw new TimeoutException(operation);
            await Task.Delay(25);
        }
    }

    private static async Task SettleBrowserAsync(WebView2 browser)
    {
        await browser.CoreWebView2.ExecuteScriptAsync("window.galleryReady=false;requestAnimationFrame(()=>requestAnimationFrame(()=>window.galleryReady=true))");
        var clock = Stopwatch.StartNew();
        while (await browser.CoreWebView2.ExecuteScriptAsync("window.galleryReady") != "true")
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Browser drawing did not settle.");
            await Task.Delay(20);
        }
    }

    private static (int Width, int Height) CaptureOwnWindow(string destination)
    {
        var handle = new WindowInteropHelper(_preview!).Handle;
        if (DwmGetWindowAttribute(handle, 9, out var bounds, Marshal.SizeOf<NativeRect>()) != 0 && !GetWindowRect(handle, out bounds))
            throw new InvalidOperationException("Cannot read the preview's own window bounds.");
        var width = bounds.Right - bounds.Left; var height = bounds.Bottom - bounds.Top;
        var screen = System.Windows.Forms.Screen.FromHandle(handle).Bounds;
        var rectangle = new System.Drawing.Rectangle(bounds.Left, bounds.Top, width, height);
        if (width <= 0 || height <= 0 || !screen.Contains(rectangle)) throw new InvalidOperationException("Preview is not fully inside its monitor.");
        // Windows may deny activation of a background process. Actual visibility,
        // rather than foreground ownership, determines whether this frame is safe.
        for (var above = GetWindow(handle, 3); above != IntPtr.Zero; above = GetWindow(above, 3))
        {
            if (!IsWindowVisible(above) || !GetWindowRect(above, out var other)) continue;
            if (rectangle.IntersectsWith(new System.Drawing.Rectangle(other.Left, other.Top,
                Math.Max(0, other.Right - other.Left), Math.Max(0, other.Bottom - other.Top))))
                throw new InvalidOperationException("Another window overlaps the screenshot area.");
        }
        using var bitmap = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bitmap.Size, System.Drawing.CopyPixelOperation.SourceCopy);
        bitmap.Save(destination, ImageFormat.Png);
        return (width, height);
    }

    private sealed record Scenario(string Name, string Fixture, string Theme, string View, bool Source = false);
    private static T Control<T>(string name) where T : FrameworkElement => (T)_preview!.FindName(name);
    private static FieldInfo Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { internal int Left, Top, Right, Bottom; }
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out NativeRect value, int size);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
}
