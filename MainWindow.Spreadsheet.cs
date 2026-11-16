using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Media;

namespace QuickPreview;

public partial class MainWindow
{
    private SpreadsheetPreviewSession? _spreadsheetSession;
    private Task? _spreadsheetInitializationTask;
    private bool _spreadsheetLoading;

    private async Task InitializeSpreadsheetWebViewAsync()
    {
        await SpreadsheetWebView.EnsureCoreWebView2Async(await GetWebEnvironmentAsync());
        ConfigureBrowserResources(SpreadsheetWebView.CoreWebView2);
    }

    private async Task LoadSpreadsheetAsync(string path, CancellationToken token)
    {
        _spreadsheetLoading = true;
        SpreadsheetPreviewSession? session = null;
        try
        {
            var workbook = await SpreadsheetPreviewService.LoadAsync(path, token);
            token.ThrowIfCancellationRequested();
            _spreadsheetInitializationTask ??= InitializeSpreadsheetWebViewAsync();
            await _spreadsheetInitializationTask.WaitAsync(token);
            token.ThrowIfCancellationRequested();
            ResumeBrowser(SpreadsheetWebView);
            session = new SpreadsheetPreviewSession(workbook, token);
            _spreadsheetSession = session;
            var color = (System.Windows.Application.Current.Resources["PreviewBackgroundBrush"] as SolidColorBrush)?.Color;
            session.Attach(SpreadsheetWebView.CoreWebView2, color is { } value && value.R + value.G + value.B > 384);
            await session.Ready.WaitAsync(TimeSpan.FromSeconds(20), token);
            token.ThrowIfCancellationRequested();
            _spreadsheetLoading = false;
            FileMetaText.Text += $"  •  {workbook.Summary}";
            ShowOnly(SpreadsheetView);
        }
        catch
        {
            session?.Dispose();
            if (ReferenceEquals(_spreadsheetSession, session)) _spreadsheetSession = null;
            throw;
        }
        finally
        {
            if (_previewCancellation?.Token == token) _spreadsheetLoading = false;
            QueueBrowserResourceUpdate();
        }
    }

    private void ReleaseSpreadsheetDocument()
    {
        var session = _spreadsheetSession;
        _spreadsheetSession = null;
        if (session is not null)
        {
            session.Dispose();
            return;
        }
        var core = SpreadsheetWebView.CoreWebView2;
        if (core is null) return;
        try
        {
            core.Stop();
            if (core.Source != "about:blank") core.Navigate("about:blank");
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or ObjectDisposedException)
        { Debug.WriteLine($"QuickPreview spreadsheet release: {exception.GetType().Name}"); }
    }
}
