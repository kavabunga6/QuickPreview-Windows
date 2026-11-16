using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using QuickPreview;

internal static class Program
{
    private static int _exitCode;
    private static bool _baseline;
    private static MainWindow? _window;
    private static readonly List<object> Measurements = new();
    private static readonly List<string> Failures = new();

    [STAThread]
    private static int Main(string[] arguments)
    {
        _baseline = arguments.Contains("--baseline");
        var app = new App();
        app.InitializeComponent();
        var startup = typeof(App).GetMethod("App_OnStartup", BindingFlags.Instance | BindingFlags.NonPublic)!;
        app.Startup -= (StartupEventHandler)Delegate.CreateDelegate(typeof(StartupEventHandler), app, startup);
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try { await RunAsync(app); }
            catch (Exception error) { Console.Error.WriteLine(error); _exitCode = 1; }
            finally { app.Shutdown(); Dispatcher.ExitAllFrames(); }
        }));
        Dispatcher.Run();
        return _exitCode;
    }

    private static async Task RunAsync(App app)
    {
        var screenshots = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../screenshots", _baseline ? "before" : "after"));
        Directory.CreateDirectory(screenshots);
        var svg = Path.Combine(AppContext.BaseDirectory, "button-layout.svg");
        File.WriteAllText(svg, "<svg xmlns='http://www.w3.org/2000/svg' width='640' height='360'><rect width='640' height='360' rx='24' fill='#195ea6'/><circle cx='470' cy='145' r='75' fill='#8fe3df'/><path d='M55 285 L210 80 L350 285 Z' fill='#f9c66a'/></svg>");
        var step = Path.Combine(AppContext.BaseDirectory, "fixtures/cube.step");
        var plain = Path.Combine(AppContext.BaseDirectory, "Очень длинное имя файла для проверки места заголовка рядом с переключателем и кнопкой закрытия.txt");
        File.WriteAllText(plain, "Single-representation fixture: the switcher stays visible and disabled.");
        var themeType = typeof(App).Assembly.GetType("QuickPreview.ThemeService", true)!;
        using var theme = (IDisposable)Activator.CreateInstance(themeType, nonPublic: true)!;
        var window = new MainWindow();
        _window = window;
        app.MainWindow = window;
        themeType.GetMethod("RegisterWindow")!.Invoke(theme, new object[] { window });
        CoreWebView2Environment.SetLoaderDllFolderPath(AppContext.BaseDirectory);
        var environment = CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(AppContext.BaseDirectory, "WebView2-button-layout"));
        Field("_webEnvironmentTask").SetValue(window, environment);

        foreach (var themeName in new[] { "light", "dark" })
        {
            Environment.SetEnvironmentVariable("QUICKPREVIEW_TEST_THEME", themeName);
            themeType.GetMethod("ApplyTheme", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(theme, null);
            foreach (var (path, label, view) in new[] { (svg, "svg", "ImageView"), (step, "step", "ModelView"), (plain, "plain", "TextView") })
            {
                window.ShowPreview(path, activateWindow: false);
                if (label == "step")
                {
                    await WaitAsync(() => Field("_modelSession").GetValue(window) is not null, "STEP session");
                    var session = Field("_modelSession").GetValue(window)!;
                    await ((Task)session.GetType().GetProperty("Ready")!.GetValue(session)!).WaitAsync(TimeSpan.FromSeconds(45));
                }
                await WaitAsync(() => Control<Border>(window, "RepresentationBar").IsVisible && Control<Grid>(window, view).IsVisible && Control<TabControl>(window, "PreviewTabs").IsEnabled == (label != "plain"), label + " visual");
                foreach (var (width, height) in new[] { (1040d, 700d), (520d, 500d) })
                foreach (var scale in new[] { 1d, 1.25d })
                {
                    window.Width = width; window.Height = height;
                    await FinishLayoutAsync(window);
                    using (ScaleText(window, scale))
                    {
                        await FinishLayoutAsync(window);
                        var suffix = $"{themeName}-{width:0}-{scale * 100:0}";
                        MeasureWpf(window, label + "-visual-" + suffix);
                        Capture(window, Path.Combine(screenshots, $"{label}-visual-{suffix}.png"));
                        if (label == "step")
                        {
                            var browser = Control<WebView2>(window, "ModelWebView");
                            await browser.CoreWebView2.ExecuteScriptAsync($"document.body.style.fontSize='{(13 * scale).ToString(CultureInfo.InvariantCulture)}px'");
                            await browser.CoreWebView2.ExecuteScriptAsync("new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)))");
                            var json = await browser.CoreWebView2.ExecuteScriptAsync(DomMeasurements);
                            using var document = JsonDocument.Parse(json);
                            Measurements.Add(new { scenario = "model-toolbar-" + suffix, dom = document.RootElement.Clone() });
                            foreach (var button in document.RootElement.EnumerateArray())
                            {
                                Check(Math.Abs(button.GetProperty("dx").GetDouble()) <= 1, "HTML label centered horizontally: " + suffix);
                                Check(Math.Abs(button.GetProperty("dy").GetDouble()) <= 1, "HTML label centered vertically: " + suffix);
                                Check(button.GetProperty("inside").GetBoolean(), "HTML label fits button: " + suffix);
                            }
                            using var png = File.Create(Path.Combine(screenshots, $"step-canvas-{suffix}.png"));
                            await browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, png);
                        }
                        var tabs = Control<TabControl>(window, "PreviewTabs");
                        if (!tabs.IsEnabled) continue;
                        tabs.SelectedIndex = 1;
                        await WaitAsync(() => Control<Grid>(window, "SourceView").IsVisible && Control<TextBox>(window, "SourcePreviewText").Text.Length > 0, "source text");
                        await FinishLayoutAsync(window);
                        MeasureWpf(window, label + "-text-" + suffix);
                        Capture(window, Path.Combine(screenshots, $"{label}-text-{suffix}.png"));
                        tabs.SelectedIndex = 0;
                        await FinishLayoutAsync(window);
                    }
                }
            }
        }
        File.WriteAllText(Path.Combine(screenshots, "measurements.json"), JsonSerializer.Serialize(Measurements, new JsonSerializerOptions { WriteIndented = true }));
        window.HidePreview();
        Environment.SetEnvironmentVariable("QUICKPREVIEW_TEST_THEME", null);
        if (Failures.Count > 0 && !_baseline) throw new Exception(string.Join(Environment.NewLine, Failures));
        Console.WriteLine($"{(_baseline ? "BASELINE" : "PASS")}: {Measurements.Count} control measurements, {Failures.Count} layout defects; captures: {screenshots}");
        foreach (var failure in Failures.Distinct()) Console.WriteLine(failure);
    }

    private const string DomMeasurements = @"(()=>[...document.querySelectorAll('.actions button')].map(button=>{
      const box=button.getBoundingClientRect(),range=document.createRange();range.selectNodeContents(button);const text=range.getBoundingClientRect();
      return {label:button.textContent,box:{x:box.x,y:box.y,width:box.width,height:box.height},text:{x:text.x,y:text.y,width:text.width,height:text.height},
        dx:text.x+text.width/2-box.x-box.width/2,dy:text.y+text.height/2-box.y-box.height/2,
        inside:text.left>=box.left&&text.right<=box.right&&text.top>=box.top&&text.bottom<=box.bottom};}))()";

    private static void MeasureWpf(MainWindow window, string scenario)
    {
        var controls = Descendants<Control>((DependencyObject)window.Content)
            .Where(control => control.IsVisible && control is Button or TabItem).ToArray();
        foreach (var control in controls)
        {
            var chrome = Descendants<Border>(control).FirstOrDefault(border => border.Name is "Chrome" or "TabChrome");
            var content = control is TabItem item ? item.Header as FrameworkElement : Descendants<TextBlock>(control).FirstOrDefault();
            if (chrome is null || content is null) continue;
            var chromeBounds = Bounds(chrome, control);
            var textBounds = Bounds(content, control);
            var clip = LayoutInformation.GetLayoutClip(control)?.Bounds;
            var visibleBounds = new Rect(control.RenderSize);
            if (clip is { } clipping) visibleBounds.Intersect(clipping);
            var dx = textBounds.Left + textBounds.Width / 2 - visibleBounds.Left - visibleBounds.Width / 2;
            var dy = textBounds.Top + textBounds.Height / 2 - visibleBounds.Top - visibleBounds.Height / 2;
            var name = control is TabItem ? System.Windows.Automation.AutomationProperties.GetName(control) : ((Button)control).Content?.ToString();
            Measurements.Add(new { scenario, name, control = RectValue(new Rect(control.RenderSize)), chrome = RectValue(chromeBounds), visible = RectValue(visibleBounds), text = RectValue(textBounds), dx, dy });
            Check(chromeBounds.Left >= visibleBounds.Left - 0.5 && chromeBounds.Right <= visibleBounds.Right + 0.5 && chromeBounds.Top >= visibleBounds.Top - 0.5 && chromeBounds.Bottom <= visibleBounds.Bottom + 0.5, scenario + ": full border visible for " + name);
            Check(Math.Abs(dx) <= 1 && Math.Abs(dy) <= 1, scenario + ": label centered in visible control for " + name);
            Check(textBounds.Left >= visibleBounds.Left && textBounds.Right <= visibleBounds.Right && textBounds.Top >= visibleBounds.Top && textBounds.Bottom <= visibleBounds.Bottom, scenario + ": text fits for " + name);
        }
        var first = Bounds(Control<TabItem>(window, "VisualPreviewTab"), window);
        var second = Bounds(Control<TabItem>(window, "SourcePreviewTab"), window);
        Check(Math.Abs(first.Top - second.Top) <= 1 && Math.Abs(first.Bottom - second.Bottom) <= 1, scenario + ": tab top/bottom aligned");
        Check(Math.Abs(first.Width - second.Width) <= 1, scenario + ": icon segments have equal widths");
        Check(second.Left - first.Right >= -0.5 && second.Left - first.Right <= 1, scenario + ": icon segments form one switcher");
        var bar = Bounds(Control<Border>(window, "RepresentationBar"), window);
        var close = Descendants<Button>((DependencyObject)window.Content).Single(button => button.ToolTip?.ToString()?.StartsWith("Закрыть", StringComparison.Ordinal) == true);
        var closeBounds = Bounds(close, window);
        var fileName = Bounds(Control<TextBlock>(window, "FileNameText"), window);
        Check(Math.Abs(bar.Height - 40) <= 1 && Math.Abs(closeBounds.Height - 40) <= 1, scenario + ": header controls retain matching 40px heights");
        Check(Math.Abs(bar.Top - closeBounds.Top) <= 1 && Math.Abs(bar.Bottom - closeBounds.Bottom) <= 1, scenario + ": switcher and close share top/bottom bounds");
        Check(Math.Abs(closeBounds.Left - bar.Right - 8) <= 1, scenario + ": switcher has 8px separation from close");
        Check(fileName.Right <= bar.Left - 7 && closeBounds.Right <= window.ActualWidth - 7, scenario + ": filename and header controls fit without overlap");
        Check(first.Left >= bar.Left && second.Right <= bar.Right && first.Top >= bar.Top && second.Bottom <= bar.Bottom, scenario + ": both icon segments fit inside the switcher");
        var ownWindow = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        foreach (var name in new[] { "VisualPreviewTab", "SourcePreviewTab" })
        {
            var segment = Control<TabItem>(window, name);
            var center = segment.PointToScreen(new Point(segment.ActualWidth / 2, segment.ActualHeight / 2));
            var position = unchecked((ushort)(int)center.X | ((ushort)(int)center.Y << 16));
            var hit = SendMessage(ownWindow, 0x0084, IntPtr.Zero, new IntPtr(position)).ToInt64(); // WM_NCHITTEST
            Check(ownWindow != IntPtr.Zero && hit == 1, scenario + ": " + name + " is a client interaction rather than caption drag"); // HTCLIENT
        }
        Measurements.Add(new { scenario, header = new { switcher = RectValue(bar), close = RectValue(closeBounds), fileName = RectValue(fileName), enabled = Control<TabControl>(window, "PreviewTabs").IsEnabled } });
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    private static object RectValue(Rect rect) => new { x = rect.X, y = rect.Y, width = rect.Width, height = rect.Height };
    private static void Check(bool success, string name) { if (!success) Failures.Add("FAIL: " + name); }
    private static FieldInfo Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static T Control<T>(MainWindow window, string name) where T : FrameworkElement => (T)window.FindName(name);
    private static Rect Bounds(FrameworkElement element, Visual ancestor) => element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));

    private static async Task WaitAsync(Func<bool> condition, string operation)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (_window is { } window && Control<Grid>(window, "ErrorView").IsVisible)
                throw new Exception(operation + ": " + Control<TextBlock>(window, "ErrorText").Text);
            if (timer.Elapsed > TimeSpan.FromSeconds(45)) throw new TimeoutException(operation);
            await Task.Delay(20);
        }
    }

    private static async Task FinishLayoutAsync(MainWindow window)
    {
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
        await Task.Delay(60);
    }

    private static IDisposable ScaleText(MainWindow window, double factor)
    {
        var values = Descendants<FrameworkElement>((DependencyObject)window.Content).Select(element => element switch
        {
            TextBlock text => (Element: (DependencyObject)text, Property: TextBlock.FontSizeProperty, Size: text.FontSize),
            Control control => (Element: (DependencyObject)control, Property: System.Windows.Controls.Control.FontSizeProperty, Size: control.FontSize),
            _ => default
        }).Where(value => value.Element is not null).ToArray();
        foreach (var value in values) value.Element.SetCurrentValue(value.Property, value.Size * factor);
        return new RestoreScope(values.Select(value => new Action(() => value.Element.SetCurrentValue(value.Property, value.Size))).ToArray());
    }

    private sealed class RestoreScope : IDisposable
    {
        private readonly Action[] _restore;
        public RestoreScope(Action[] restore) => _restore = restore;
        public void Dispose() { foreach (var restore in _restore) restore(); }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static void Capture(MainWindow window, string path)
    {
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
