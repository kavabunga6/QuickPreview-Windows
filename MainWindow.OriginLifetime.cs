using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace QuickPreview;

public partial class MainWindow
{
    private const uint OriginDestroyEvent = 0x8001;
    private DispatcherTimer? _originLifetimeTimer;
    private ExplorerOriginIdentity? _monitoredOrigin;
    private IntPtr _originLifetimeHook;
    private OriginLifetimeEventProc? _originLifetimeCallback;
    private readonly List<OriginLifetimeEventProc> _retainedOriginLifetimeCallbacks = new();
    private bool _originLifetimeMonitoringStopped;
    private bool _originLifetimeCloseQueued;

    internal event Action? OriginClosed;

    private void InitializeOriginLifetimeMonitoring()
    {
        _originLifetimeTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _originLifetimeTimer.Tick += CheckMonitoredOriginLifetime;
        IsVisibleChanged += OriginLifetimeVisibilityChanged;
        ExplorerSelectionService.OriginIdentityChanged += OriginLifetimeIdentityChanged;
    }

    internal void StopOriginLifetimeMonitoring()
    {
        if (_originLifetimeMonitoringStopped) return;
        _originLifetimeMonitoringStopped = true;
        ExplorerSelectionService.OriginIdentityChanged -= OriginLifetimeIdentityChanged;
        IsVisibleChanged -= OriginLifetimeVisibilityChanged;
        _originLifetimeTimer?.Stop();
        if (_originLifetimeTimer is not null)
            _originLifetimeTimer.Tick -= CheckMonitoredOriginLifetime;
        DetachOriginLifetimeHook();
    }

    private void OriginLifetimeVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        RefreshOriginLifetimeMonitoring();

    private void OriginLifetimeIdentityChanged()
    {
        if (_originLifetimeMonitoringStopped || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            return;
        if (Dispatcher.CheckAccess()) RefreshOriginLifetimeMonitoring();
        else
        {
            try { _ = Dispatcher.BeginInvoke(new Action(RefreshOriginLifetimeMonitoring)); }
            catch (InvalidOperationException) { /* The application dispatcher has stopped. */ }
        }
    }

    private void RefreshOriginLifetimeMonitoring()
    {
        if (_originLifetimeMonitoringStopped) return;
        var identity = IsVisible ? ExplorerSelectionService.OriginIdentity : null;
        if (ReferenceEquals(identity, _monitoredOrigin)) return;

        _originLifetimeTimer?.Stop();
        DetachOriginLifetimeHook();
        _monitoredOrigin = identity;
        _originLifetimeCloseQueued = false;
        if (identity is null) return;

        // Each delegate captures its binding. A callback queued before unhooking
        // cannot close a later preview, including one using a recycled HWND.
        _originLifetimeCallback = (hook, eventType, window, objectId, childId, eventThread, eventTime) =>
            OriginLifetimeWindowDestroyed(identity, eventType, window, objectId, childId, eventThread, eventTime);
        _originLifetimeHook = SetOriginLifetimeWinEventHook(OriginDestroyEvent, OriginDestroyEvent,
            IntPtr.Zero, _originLifetimeCallback, identity.ProcessId, 0, 0x0002);
        _originLifetimeTimer?.Start();
        CheckMonitoredOriginLifetime(this, EventArgs.Empty);
    }

    private void DetachOriginLifetimeHook()
    {
        if (_originLifetimeHook != IntPtr.Zero)
        {
            // Keep the delegate alive if Windows could not remove the hook.
            // Its captured binding still makes all subsequent callbacks harmless.
            if (!UnhookOriginLifetimeWinEvent(_originLifetimeHook) && _originLifetimeCallback is not null)
                _retainedOriginLifetimeCallbacks.Add(_originLifetimeCallback);
            _originLifetimeHook = IntPtr.Zero;
        }
        _originLifetimeCallback = null;
        _monitoredOrigin = null;
    }

    private void CheckMonitoredOriginLifetime(object? sender, EventArgs e)
    {
        if (_originLifetimeMonitoringStopped || !IsVisible || _monitoredOrigin is not { } identity)
            return;
        // Native HWND identity checks also cover missed events and hook failure.
        // They run while minimized or another application has the foreground.
        if (ExplorerSelectionService.CheckOriginLifetime(identity) == OriginLifetimeState.Closed)
            QueueOriginClosed(identity);
    }

    private void OriginLifetimeWindowDestroyed(ExplorerOriginIdentity identity, uint eventType,
        IntPtr window, int objectId, int childId, uint eventThread, uint eventTime)
    {
        try
        {
            if (_originLifetimeMonitoringStopped || eventType != OriginDestroyEvent || objectId != 0 || childId != 0 ||
                unchecked((int)(eventTime - identity.CapturedAt)) < 0)
                return;
            var isFrame = window == identity.FrameWindow && eventThread == identity.FrameThreadId;
            var isTab = window == identity.TabWindow && eventThread == identity.TabThreadId;
            if (!isFrame && !isTab) return;

            // A matching destroy event is proof even if the numeric handle has
            // already been reused. Do not query ancestors of a destroyed HWND.
            QueueOriginClosed(identity);
        }
        catch (Exception exception)
        {
            // Managed exceptions must never escape into a native callback.
            Debug.WriteLine($"QuickPreview origin notification: {exception.GetType().Name}");
        }
    }

    private void QueueOriginClosed(ExplorerOriginIdentity identity)
    {
        if (_originLifetimeMonitoringStopped || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            return;
        if (!Dispatcher.CheckAccess())
        {
            try { _ = Dispatcher.BeginInvoke(new Action(() => QueueOriginClosed(identity))); }
            catch (InvalidOperationException) { /* The application dispatcher has stopped. */ }
            return;
        }
        if (_originLifetimeCloseQueued || !IsVisible || !ReferenceEquals(identity, _monitoredOrigin) ||
            !ReferenceEquals(identity, ExplorerSelectionService.OriginIdentity)) return;
        _originLifetimeCloseQueued = true;
        // Even an out-of-context event delivered on the WPF thread must finish
        // before hiding releases WebView resources or removes the native hook.
        // Recheck the binding when the queued action runs, after any rebind.
        try { _ = Dispatcher.BeginInvoke(new Action(() => ClosePreviewForMonitoredOrigin(identity))); }
        catch (InvalidOperationException) { _originLifetimeCloseQueued = false; }
    }

    private void ClosePreviewForMonitoredOrigin(ExplorerOriginIdentity identity)
    {
        if (_originLifetimeMonitoringStopped || !IsVisible || !ReferenceEquals(identity, _monitoredOrigin) ||
            !ReferenceEquals(identity, ExplorerSelectionService.OriginIdentity)) return;
        try { OriginClosed?.Invoke(); }
        finally { HidePreviewWithoutRestoringOriginFocus(); }
    }

    private delegate void OriginLifetimeEventProc(IntPtr hook, uint eventType, IntPtr window,
        int objectId, int childId, uint eventThread, uint eventTime);

    [DllImport("user32.dll", EntryPoint = "SetWinEventHook")]
    private static extern IntPtr SetOriginLifetimeWinEventHook(uint eventMin, uint eventMax, IntPtr module,
        OriginLifetimeEventProc callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll", EntryPoint = "UnhookWinEvent")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookOriginLifetimeWinEvent(IntPtr hook);
}
