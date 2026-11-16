using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace QuickPreview;

public partial class MainWindow
{
    private bool _modelLoading;
    private bool _pdfLoading;
    private bool _previewLoading;
    private bool _browserResourceUpdateQueued;
    private string? _reloadAfterMinimize;
    private bool _restoreSourceAfterMinimize;
    private CoreWebView2Environment? _budgetEnvironment;
    private readonly HashSet<CoreWebView2> _suspendingBrowsers = new();

    private void InitializePreviewResourcePolicy()
    {
        IsVisibleChanged += (_, _) => QueueBrowserResourceUpdate();
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized && _previewLoading)
            {
                _reloadAfterMinimize = _currentPath;
                _restoreSourceAfterMinimize = _sourceSelected;
                _previewCancellation?.Cancel();
                DisposeModelPreview();
                ReleaseSpreadsheetDocument();
                PdfWebView.CoreWebView2?.Stop();
                _modelLoading = _pdfLoading = _spreadsheetLoading = false;
                ShowOnly(LoadingView);
            }
            else if (WindowState != WindowState.Minimized && IsVisible && _reloadAfterMinimize is { } path)
            {
                var preferSource = _restoreSourceAfterMinimize;
                _reloadAfterMinimize = null;
                if (string.Equals(path, _currentPath, StringComparison.OrdinalIgnoreCase))
                    _ = ShowPreviewAsync(path, rebuildNavigation: false, activateWindow: false,
                        preserveWindow: true, preferSource: preferSource);
            }
            QueueBrowserResourceUpdate();
        };
    }

    private void ResetBrowserLoadingState()
    {
        _modelLoading = _pdfLoading = _spreadsheetLoading = false;
        _reloadAfterMinimize = null;
    }

    private void ReleasePdfDocument()
    {
        var core = PdfWebView.CoreWebView2;
        if (core is null) return;
        try
        {
            core.Stop();
            if (core.Source != "about:blank") core.Navigate("about:blank");
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or ObjectDisposedException)
        { Debug.WriteLine($"QuickPreview PDF release: {exception.GetType().Name}"); }
    }

    private bool BrowserWorkNeeded(WebView2 browser) => IsVisible && WindowState != WindowState.Minimized &&
        (ReferenceEquals(browser, ModelWebView) ? _modelLoading || ModelView.IsVisible :
         ReferenceEquals(browser, SpreadsheetWebView) ? _spreadsheetLoading || SpreadsheetView.IsVisible :
         _pdfLoading || PdfView.IsVisible);

    private void QueueBrowserResourceUpdate()
    {
        if (_browserResourceUpdateQueued || Dispatcher.HasShutdownStarted) return;
        _browserResourceUpdateQueued = true;
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            _browserResourceUpdateQueued = false;
            await UpdateBrowserResourcesAsync(ModelWebView);
            await UpdateBrowserResourcesAsync(PdfWebView);
            await UpdateBrowserResourcesAsync(SpreadsheetWebView);
        }), DispatcherPriority.Background);
    }

    private async Task UpdateBrowserResourcesAsync(WebView2 browser)
    {
        var core = browser.CoreWebView2;
        if (core is null) return;
        try
        {
            if (BrowserWorkNeeded(browser))
            {
                ResumeBrowser(browser);
                return;
            }
            // Hidden retains layout and the cached model/camera. It also sets the
            // native controller invisible, which TrySuspendAsync requires.
            browser.Visibility = Visibility.Hidden;
            if (!_suspendingBrowsers.Add(core)) return;
            try { if (!core.IsSuspended) await core.TrySuspendAsync(); }
            finally
            {
                _suspendingBrowsers.Remove(core);
                // A new selection can resume/navigate while suspension is pending.
                // Never let its old completion put the current preview to sleep.
                if (ReferenceEquals(core, browser.CoreWebView2) && BrowserWorkNeeded(browser))
                    ResumeBrowser(browser);
            }
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or ObjectDisposedException or NotImplementedException)
        {
            Debug.WriteLine($"QuickPreview browser resource policy: {exception.GetType().Name}");
        }
    }

    private void ResumeBrowser(WebView2 browser)
    {
        browser.Visibility = Visibility.Visible;
        var core = browser.CoreWebView2;
        if (core is null) return;
        try { core.Resume(); }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or NotImplementedException)
        { Debug.WriteLine($"QuickPreview browser resume: {exception.GetType().Name}"); }
        ApplyBrowserProcessPriorities();
    }

    private void ConfigureBrowserResources(CoreWebView2 core)
    {
        var environment = core.Environment;
        if (!ReferenceEquals(_budgetEnvironment, environment))
        {
            if (_budgetEnvironment is not null) _budgetEnvironment.ProcessInfosChanged -= BrowserProcessesChanged;
            _budgetEnvironment = environment;
            environment.ProcessInfosChanged += BrowserProcessesChanged;
        }
        ApplyBrowserProcessPriorities();
        QueueBrowserResourceUpdate();
    }

    private void BrowserProcessesChanged(object? sender, object e) => ApplyBrowserProcessPriorities();

    private void ApplyBrowserProcessPriorities()
    {
        if (_budgetEnvironment is null) return;
        try
        {
            foreach (var info in _budgetEnvironment.GetProcessInfos())
            {
                if (info.Kind is not (CoreWebView2ProcessKind.Renderer or CoreWebView2ProcessKind.Utility) ||
                    info.ProcessId == Environment.ProcessId) continue;
                try
                {
                    // Only this WebView environment's own renderer/decoder processes.
                    // Keep the host UI, GPU process and Explorer priorities unchanged.
                    using var process = Process.GetProcessById(info.ProcessId);
                    if (process.PriorityClass is ProcessPriorityClass.Normal or ProcessPriorityClass.AboveNormal or
                        ProcessPriorityClass.High or ProcessPriorityClass.RealTime)
                        process.PriorityClass = ProcessPriorityClass.BelowNormal;
                }
                catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or ArgumentException or NotSupportedException)
                { Debug.WriteLine($"QuickPreview renderer priority: {exception.GetType().Name}"); }
            }
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or NotImplementedException)
        { Debug.WriteLine($"QuickPreview browser processes: {exception.GetType().Name}"); }
    }
}
