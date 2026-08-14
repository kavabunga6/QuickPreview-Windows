using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;

namespace QuickPreview;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstance;
    private GlobalKeyboardHook? _keyboardHook;
    private MainWindow? _previewWindow;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private System.Drawing.Icon? _applicationIcon;
    private ThemeService? _themeService;
    private SettingsWindow? _settingsWindow;

    private void App_OnStartup(object sender, StartupEventArgs e)
    {
        var mutexName = Environment.GetEnvironmentVariable("QUICKPREVIEW_TEST_MUTEX")
                        ?? "QuickPreview.Windows.SingleInstance";
        _singleInstance = new Mutex(true, mutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            Shutdown();
            return;
        }

        try
        {
            StartupService.NormalizeEnabledExecutablePath();
        }
        catch
        {
            // Startup settings must never prevent the previewer from launching.
        }

        _themeService = new ThemeService();
        _themeService.Start();

        _previewWindow = new MainWindow();
        _themeService.RegisterWindow(_previewWindow);
        MainWindow = _previewWindow;

        CreateTrayIcon();
        _keyboardHook = new GlobalKeyboardHook();
        _keyboardHook.SpacePressed += TogglePreview;
        _keyboardHook.Install();

        var requestedPath = e.Args.FirstOrDefault();
        if (string.Equals(requestedPath, "--settings", StringComparison.OrdinalIgnoreCase))
        {
            Dispatcher.BeginInvoke(new Action(ShowSettings));
        }
        else if (requestedPath is not null && (File.Exists(requestedPath) || Directory.Exists(requestedPath)))
        {
            Dispatcher.BeginInvoke(new Action(() => _previewWindow.ShowPreview(Path.GetFullPath(requestedPath))));
        }
    }

    private void TogglePreview()
    {
        if (_previewWindow is null)
            return;

        if (_previewWindow.IsVisible)
        {
            _previewWindow.HidePreview();
            return;
        }

        var selectedPath = ExplorerSelectionService.TryGetSelectedPath();
        if (!string.IsNullOrWhiteSpace(selectedPath))
            _previewWindow.ShowPreview(selectedPath);
    }

    private void CreateTrayIcon()
    {
        var resource = GetResourceStream(new Uri("pack://application:,,,/assets/quickpreview-tray.ico"));
        if (resource is not null)
        {
            using var icon = new System.Drawing.Icon(resource.Stream, System.Windows.Forms.SystemInformation.SmallIconSize);
            _applicationIcon = (System.Drawing.Icon)icon.Clone();
        }

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Настройки…", null, (_, _) => Dispatcher.BeginInvoke(new Action(ShowSettings)));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("О QuickPreview", null, (_, _) =>
            System.Windows.MessageBox.Show(
                "Выделите файл в Проводнике и нажмите Space.\n\nEsc — закрыть\nEnter — открыть файл\nCtrl+Shift+E — показать в папке",
                "QuickPreview", MessageBoxButton.OK, MessageBoxImage.Information));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => Shutdown());

        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = _applicationIcon ?? System.Drawing.SystemIcons.Application,
            Text = "QuickPreview — Space для предпросмотра",
            Visible = true,
            ContextMenuStrip = menu
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.BeginInvoke(new Action(ShowSettings));
    }

    private void ShowSettings()
    {
        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow();
        _themeService?.RegisterWindow(_settingsWindow);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _keyboardHook?.Dispose();
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }

        _singleInstance?.Dispose();
        _applicationIcon?.Dispose();
        _themeService?.Dispose();
        base.OnExit(e);
    }
}
