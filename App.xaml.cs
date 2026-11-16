using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

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
    private DispatcherTimer? _selectionTimer;
    private DispatcherTimer? _selectionWakeTimer;
    private IntPtr _selectionEventHook;
    private WinEventProc? _selectionEventCallback;
    private readonly Stopwatch _selectionClock = Stopwatch.StartNew();
    private bool _selectionReadInFlight;
    private bool _selectionEventPending;
    private bool _originWasForeground;
    private long _nextSelectionReadAt;
    private long _lastSelectionReadAt = -120;
    private long _lastSelectionChangeAt;
    private string? _lastObservedSelection;
    private int _visibilityEpoch;
    private bool _openingPreview;
    private bool _openRequested;
    private int _openRequestVersion;
    private IntPtr _requestedOpenForeground;
    private IntPtr _requestedOpenTab;
    private bool _shuttingDown;

    private void App_OnStartup(object sender, StartupEventArgs e)
    {
        if (e.Args.FirstOrDefault() == "--preview-worker")
        {
            Environment.Exit(PreviewWorker.Run(e.Args.Skip(1).ToArray()));
            return;
        }
        var testMutex = Environment.GetEnvironmentVariable("QUICKPREVIEW_TEST_MUTEX");
        var mutexName = string.IsNullOrWhiteSpace(testMutex)
            ? "QuickPreview.Windows.SingleInstance"
            : testMutex;
        _singleInstance = new Mutex(true, mutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            Shutdown();
            return;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(testMutex))
                StartupService.NormalizeEnabledExecutablePath();
        }
        catch
        {
            // Startup settings must never prevent the previewer from launching.
        }

        _themeService = new ThemeService();
        _themeService.Start();

        _previewWindow = new MainWindow();
        _previewWindow.IsVisibleChanged += PreviewVisibilityChanged;
        _previewWindow.OriginClosed += PreviewOriginClosed;
        _themeService.RegisterWindow(_previewWindow);
        MainWindow = _previewWindow;

        CreateTrayIcon();
        _keyboardHook = new GlobalKeyboardHook();
        _keyboardHook.SpacePressed += TogglePreview;
        _keyboardHook.NavigateRequested += direction => _previewWindow?.Navigate(direction);
        _keyboardHook.CloseRequested += () => _previewWindow?.HidePreview();
        _keyboardHook.Install();
        _selectionTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        _selectionTimer.Tick += (_, _) => SynchronizeExplorerSelection();
        _selectionWakeTimer = new DispatcherTimer(DispatcherPriority.Background);
        _selectionWakeTimer.Tick += (_, _) =>
        {
            _selectionWakeTimer.Stop();
            ScheduleSelectionWake();
        };
        _selectionEventCallback = ExplorerSelectionChanged;
        // Selection notifications only wake a bounded read; no accessibility
        // objects, input injection or activation are used inside this callback.
        _selectionEventHook = SetWinEventHook(0x8006, 0x8009, IntPtr.Zero, _selectionEventCallback,
            0, 0, 0x0002); // OUTOFCONTEXT | SKIPOWNPROCESS

        var requestedPath = e.Args.FirstOrDefault();
        if (string.Equals(requestedPath, "--settings", StringComparison.OrdinalIgnoreCase))
        {
            Dispatcher.BeginInvoke(new Action(ShowSettings));
        }
        else if (!string.IsNullOrWhiteSpace(requestedPath))
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try { _previewWindow.ShowPreview(Path.GetFullPath(requestedPath)); }
                catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
                { Debug.WriteLine($"QuickPreview startup path: {exception.GetType().Name}"); }
            }));
        }
    }

    private async void TogglePreview()
    {
        if (_previewWindow is null || _shuttingDown)
            return;

        var gesture = _keyboardHook?.DispatchedSpaceGesture;
        if (gesture is null)
        {
            var foreground = GetForegroundWindow();
            gesture = new ExplorerSpaceGesture(_previewWindow.IsVisible, foreground,
                ExplorerSelectionService.GetActiveExplorerTab(foreground));
        }
        // A close gesture queued before Explorer was destroyed must not become
        // an opening gesture after automatic hiding has already completed.
        if (gesture.PreviewWasVisible && !_previewWindow.IsVisible) return;

        _openRequestVersion++;
        if (_previewWindow.IsVisible)
        {
            _openRequested = false;
            _previewWindow.HidePreview();
            return;
        }

        // A second Space cancels an opening that is still waiting for Explorer.
        // Further presses only change the desired state; at most one read is queued.
        _openRequested = !_openRequested;
        if (_openRequested)
        {
            _requestedOpenForeground = gesture.FrameWindow;
            _requestedOpenTab = gesture.TabWindow;
        }
        else ExplorerSelectionService.CancelPendingNavigation();
        if (_openingPreview || !_openRequested) return;
        _openingPreview = true;
        OpenSelection? attachment = null;
        try
        {
            while (_openRequested && !_shuttingDown && !_previewWindow.IsVisible)
            {
                var request = _openRequestVersion;
                var foreground = _requestedOpenForeground;
                var tab = _requestedOpenTab;
                var generation = ExplorerSelectionService.OriginGeneration;
                var visibility = _visibilityEpoch;
                var selection = await ExplorerStaWorker.RunAsync(() =>
                {
                    if (foreground == IntPtr.Zero || tab == IntPtr.Zero || GetForegroundWindow() != foreground ||
                        ExplorerSelectionService.GetActiveExplorerTab(foreground) != tab ||
                        ExplorerSelectionService.OriginGeneration != generation)
                        return new OpenSelection(null, generation, false);
                    var path = ExplorerSelectionService.TryGetSelectedPathInTab(foreground, tab);
                    var resultGeneration = ExplorerSelectionService.OriginGeneration;
                    var attached = !string.IsNullOrWhiteSpace(path);
                    if (!attached || GetForegroundWindow() != foreground || !ExplorerSelectionService.IsOriginWindow(foreground) ||
                        !ExplorerSelectionService.IsOriginTabActive || ExplorerSelectionService.GetActiveExplorerTab(foreground) != tab ||
                        resultGeneration != unchecked(generation + 1))
                        path = null;
                    return new OpenSelection(path, resultGeneration, attached);
                });
                attachment = selection;

                if (request != _openRequestVersion)
                {
                    ForgetUnshownOrigin(selection);
                    continue;
                }
                if (!CanShowOpenedSelection(selection, foreground, tab, visibility))
                {
                    ForgetUnshownOrigin(selection);
                    return;
                }
                // The preview validates filesystem metadata through its bounded
                // reader. A duplicate unbounded SMB stat must not hold opening.
                _openRequested = false;
                _previewWindow.ShowPreview(selection.Path!, activateWindow: false);
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"QuickPreview opening: {exception.Message}");
        }
        finally
        {
            _openingPreview = false;
            _openRequested = false;
            if (attachment is not null) ForgetUnshownOrigin(attachment);
        }
    }

    private bool CanShowOpenedSelection(OpenSelection selection, IntPtr foreground, IntPtr tab, int visibility) =>
        !_shuttingDown && _openRequested && _previewWindow is { IsVisible: false } &&
        visibility == _visibilityEpoch && !string.IsNullOrWhiteSpace(selection.Path) &&
        selection.Generation == ExplorerSelectionService.OriginGeneration &&
        GetForegroundWindow() == foreground && ExplorerSelectionService.IsOriginWindow(foreground) &&
        ExplorerSelectionService.IsOriginTabActive && ExplorerSelectionService.GetActiveExplorerTab(foreground) == tab;

    private void PreviewOriginClosed()
    {
        _openRequested = false;
        _openRequestVersion++;
        _requestedOpenForeground = IntPtr.Zero;
        _requestedOpenTab = IntPtr.Zero;
    }

    private void ForgetUnshownOrigin(OpenSelection selection)
    {
        if (selection.Attached && _previewWindow is { IsVisible: false } &&
            selection.Generation == ExplorerSelectionService.OriginGeneration)
            ExplorerSelectionService.ForgetOriginWindow();
    }

    private void PreviewVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        _visibilityEpoch++;
        _originWasForeground = false;
        _lastObservedSelection = null;
        _lastSelectionChangeAt = _selectionClock.ElapsedMilliseconds;
        _lastSelectionReadAt = _lastSelectionChangeAt - 120;
        _nextSelectionReadAt = 0;
        _selectionEventPending = false;
        _selectionWakeTimer?.Stop();
        if (_shuttingDown || _previewWindow is not { IsVisible: true }) _selectionTimer?.Stop();
        else _selectionTimer?.Start();
    }

    private void SynchronizeExplorerSelection()
    {
        if (_shuttingDown || _previewWindow is not { IsVisible: true })
            return;
        if (!ExplorerSelectionService.IsOriginForeground)
        {
            _originWasForeground = false;
            _selectionWakeTimer?.Stop();
            return;
        }

        // This native z-order check is cheap and stays on the UI thread even
        // while the slower Shell selection read is pending or idle-throttled.
        _previewWindow.KeepAboveOriginExplorer();
        var startedAt = _selectionClock.ElapsedMilliseconds;
        if (!_originWasForeground)
        {
            _originWasForeground = true;
            _lastSelectionChangeAt = startedAt;
            _nextSelectionReadAt = 0;
        }
        if (_selectionReadInFlight || startedAt < _nextSelectionReadAt) return;
        _selectionReadInFlight = true;
        _selectionEventPending = false;
        _lastSelectionReadAt = startedAt;
        _selectionWakeTimer?.Stop();
        _ = ReadExplorerSelectionAsync(GetForegroundWindow(), ExplorerSelectionService.OriginGeneration,
            _visibilityEpoch, startedAt);
    }

    private async Task ReadExplorerSelectionAsync(IntPtr foreground, int generation, int visibility, long startedAt)
    {
        try
        {
            var selectedPath = await ExplorerStaWorker.RunAsync(() =>
            {
                if (generation != ExplorerSelectionService.OriginGeneration || GetForegroundWindow() != foreground ||
                    !ExplorerSelectionService.IsOriginWindow(foreground) || !ExplorerSelectionService.IsOriginTabActive) return null;
                var path = ExplorerSelectionService.TryGetSelectedPathInOriginWindow();
                return generation == ExplorerSelectionService.OriginGeneration && GetForegroundWindow() == foreground &&
                    ExplorerSelectionService.IsOriginWindow(foreground) && ExplorerSelectionService.IsOriginTabActive ? path : null;
            }, DispatcherPriority.Background);
            if (!CanApplySelection(foreground, generation, visibility)) return;
            if (!string.Equals(selectedPath, _lastObservedSelection, StringComparison.OrdinalIgnoreCase))
            {
                _lastObservedSelection = selectedPath;
                _lastSelectionChangeAt = _selectionClock.ElapsedMilliseconds;
            }
            // Keep fast following for 600ms after a change; unchanged browsing
            // idles at 360ms, while z-order protection still runs every120ms.
            _nextSelectionReadAt = startedAt +
                (_selectionClock.ElapsedMilliseconds - _lastSelectionChangeAt < 600 ? 120 : 360);
            if (string.IsNullOrWhiteSpace(selectedPath)) return;
            if (CanApplySelection(foreground, generation, visibility))
                _previewWindow!.UpdatePreviewFromExplorer(selectedPath);
        }
        catch (Exception exception)
        {
            _nextSelectionReadAt = startedAt + 360;
            Debug.WriteLine($"QuickPreview selection read: {exception.Message}");
        }
        finally
        {
            _selectionReadInFlight = false;
            if (_selectionEventPending) ScheduleSelectionWake();
        }
    }

    private void ExplorerSelectionChanged(IntPtr hook, uint eventType, IntPtr window, int objectId,
        int childId, uint eventThread, uint eventTime)
    {
        try
        {
            if (_shuttingDown || _previewWindow is not { IsVisible: true } ||
                !ExplorerSelectionService.IsOriginForeground ||
                !ExplorerSelectionService.IsOriginWindow(GetAncestor(window, 2))) return;
            _lastSelectionChangeAt = _selectionClock.ElapsedMilliseconds;
            _selectionEventPending = true;
            _nextSelectionReadAt = Math.Max(_lastSelectionChangeAt, _lastSelectionReadAt + 120);
            ScheduleSelectionWake();
        }
        catch (Exception exception)
        {
            // Managed exceptions must not cross a native event callback boundary.
            Debug.WriteLine($"QuickPreview selection notification: {exception.GetType().Name}");
        }
    }

    private void ScheduleSelectionWake()
    {
        if (_shuttingDown || !_selectionEventPending || _selectionReadInFlight ||
            _previewWindow is not { IsVisible: true } || !ExplorerSelectionService.IsOriginForeground)
            return;
        var remaining = _nextSelectionReadAt - _selectionClock.ElapsedMilliseconds;
        if (remaining <= 0) SynchronizeExplorerSelection();
        else if (_selectionWakeTimer is not null)
        {
            _selectionWakeTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, remaining));
            _selectionWakeTimer.Start();
        }
    }

    private bool CanApplySelection(IntPtr foreground, int generation, int visibility) =>
        !_shuttingDown && _previewWindow is { IsVisible: true } && visibility == _visibilityEpoch &&
        generation == ExplorerSelectionService.OriginGeneration && GetForegroundWindow() == foreground &&
        ExplorerSelectionService.IsOriginWindow(foreground) && ExplorerSelectionService.IsOriginTabActive;

    private sealed record OpenSelection(string? Path, int Generation, bool Attached);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr window, int objectId,
        int childId, uint eventThread, uint eventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module,
        WinEventProc callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

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
        _shuttingDown = true;
        _openRequested = false;
        _openRequestVersion++;
        _visibilityEpoch++;
        _keyboardHook?.Dispose();
        var preview = _previewWindow ?? MainWindow as QuickPreview.MainWindow;
        if (preview is not null)
        {
            preview.OriginClosed -= PreviewOriginClosed;
            preview.StopOriginLifetimeMonitoring();
        }
        _selectionTimer?.Stop();
        _selectionWakeTimer?.Stop();
        if (_selectionEventHook != IntPtr.Zero) _ = UnhookWinEvent(_selectionEventHook);
        _selectionEventHook = IntPtr.Zero;
        ExplorerStaWorker.Shutdown();
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
