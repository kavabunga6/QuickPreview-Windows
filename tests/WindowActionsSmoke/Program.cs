using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;
using QuickPreview;

internal static class Program
{
    private static MainWindow? _window;
    private static int _exit;
    private static int _checks;

    [STAThread]
    private static int Main()
    {
        var app = new App(); app.InitializeComponent();
        var startup = typeof(App).GetMethod("App_OnStartup", BindingFlags.Instance | BindingFlags.NonPublic)!;
        app.Startup -= (StartupEventHandler)Delegate.CreateDelegate(typeof(StartupEventHandler), app, startup);
        app.DispatcherUnhandledException += (_, e) => { Console.Error.WriteLine(e.Exception); _exit = 1; e.Handled = true; };
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try { await RunAsync(app); }
            catch (Exception error) { Console.Error.WriteLine(error); _exit = 1; }
            finally
            {
                _window?.HidePreview();
                if (_window is not null)
                {
                    typeof(MainWindow).GetMethod("StopOriginLifetimeMonitoring", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_window, null);
                    foreach (var name in new[] { "ModelWebView", "PdfWebView", "SpreadsheetWebView" })
                        (_window.FindName(name) as WebView2)?.Dispose();
                }
                app.Shutdown(); Dispatcher.ExitAllFrames();
            }
        }));
        Dispatcher.Run(); return _exit;
    }

    private static async Task RunAsync(App app)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var text = Path.Combine(directory, "original.txt");
        File.WriteAllText(text, "Original window-action fixture.");
        _window = new MainWindow(); app.MainWindow = _window;
        _window.ShowPreview(text, activateWindow: false);
        await WaitAsync(() => View("TextView").IsVisible, "text loaded");
        var missing = Path.Combine(directory, "removed-original.exe");
        typeof(MainWindow).GetField("_currentPath", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_window, missing);
        typeof(MainWindow).GetMethod("OpenButton_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_window, new object[] { _window, new RoutedEventArgs(Button.ClickEvent) });
        Check(_window.IsVisible && View("ErrorView").IsVisible, "Missing Open target shows an error without hiding or terminating the preview.");
        Check(((TextBlock)_window.FindName("ErrorText")).Text.Contains("Не удалось открыть файл"), "The launch failure gives an actionable message.");

        var shell = typeof(MainWindow).GetMethod("TryShellAction", BindingFlags.Instance | BindingFlags.NonPublic)!;
        shell.Invoke(_window, new object[] { new ProcessStartInfo(missing) { UseShellExecute = true }, "показать файл в папке" });
        Check(_window.IsVisible && View("ErrorView").IsVisible, "Reveal launch errors also stay inside the existing window.");

        var deepXml = Path.Combine(directory, "deep.xml");
        File.WriteAllText(deepXml, string.Concat(Enumerable.Repeat("<node>", 2000)) + "original" + string.Concat(Enumerable.Repeat("</node>", 2000)));
        _window.ShowPreview(deepXml, activateWindow: false);
        await WaitAsync(() => View("TextView").IsVisible && ((TextBlock)_window.FindName("FileNameText")).Text == "deep.xml", "deep XML raw fallback");
        Check(((TextBox)_window.FindName("PreviewText")).Text.Contains("original"), "Excessively nested plain XML remains available as raw text.");
        Check(((TextBlock)_window.FindName("FileMetaText")).Text.Contains("исходный текст"), "A depth-limit failure is explained in the XML status.");

        _window.ShowPreview(text, activateWindow: false);
        await WaitAsync(() => View("TextView").IsVisible && ((TextBox)_window.FindName("PreviewText")).Text == "Original window-action fixture.", "recovery after errors");
        Check(true, "The next preview works after shell and XML failures.");
        Console.WriteLine($"PASS: {_checks} window action and XML fallback checks.");
    }

    private static FrameworkElement View(string name) => (FrameworkElement)_window!.FindName(name);
    private static async Task WaitAsync(Func<bool> ready, string label)
    {
        var watch = Stopwatch.StartNew();
        while (!ready())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException(label);
            await Task.Delay(20);
        }
    }
    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException("FAIL: " + message);
        _checks++; Console.WriteLine("PASS: " + message);
    }
}
