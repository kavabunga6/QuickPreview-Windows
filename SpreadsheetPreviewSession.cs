using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace QuickPreview;

/// <summary>Serves one bounded workbook locally, without exposing a directory or executing document content.</summary>
internal sealed class SpreadsheetPreviewSession : IDisposable
{
    internal const string Origin = "https://quickpreview-sheet.invalid";
    private static readonly ConditionalWeakTable<CoreWebView2, SpreadsheetPreviewSession> Owners = new();
    private static readonly SemaphoreSlim AssetReads = new(2, 2);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private readonly string _assetDirectory = Path.Combine(AppContext.BaseDirectory, "assets", "spreadsheet-viewer");
    private readonly CancellationTokenSource _cancellation;
    private readonly CancellationToken _token;
    private readonly CancellationTokenRegistration _registration;
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task<byte[]> _workbookJson;
    private CoreWebView2? _core;
    private string? _documentUri;
    private ulong? _documentNavigationId;
    private bool _allSourceKinds;
    private volatile bool _disposed;

    public Task Ready => _ready.Task;
    internal string ViewerUri => $"{Origin}/viewer.html?session={_sessionId}";

    internal SpreadsheetPreviewSession(SpreadsheetWorkbook workbook, CancellationToken token = default)
    {
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        _token = _cancellation.Token;
        _cancellation.CancelAfter(TimeSpan.FromSeconds(25));
        _registration = _token.Register(() =>
        {
            if (_disposed || token.IsCancellationRequested) _ready.TrySetCanceled(_token);
            else _ready.TrySetException(new TimeoutException("Таблица загружалась слишком долго. Откройте файл в офисном приложении."));
        });
        _workbookJson = Task.Run(() =>
        {
            _token.ThrowIfCancellationRequested();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(workbook, JsonOptions);
            _token.ThrowIfCancellationRequested();
            if (bytes.Length > 16 * 1024 * 1024)
                throw new NotSupportedException("Таблица слишком сложная для быстрого предпросмотра.");
            return bytes;
        }, _token);
        _ = _workbookJson.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public void Attach(CoreWebView2 core, bool lightTheme)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SpreadsheetPreviewSession));
        if (_core is not null) throw new InvalidOperationException("Spreadsheet preview is already attached.");
        foreach (var asset in new[] { "viewer.html", "viewer.css", "viewer.js" })
        {
            var path = Path.Combine(_assetDirectory, asset);
            if (!File.Exists(path) || ModelPreviewService.HasReparsePoint(path))
                throw new FileNotFoundException("В установке отсутствует просмотрщик таблиц. Переустановите QuickPreview.");
        }
        // Reusing a WebView must not leave two sessions handling the same requests.
        if (Owners.TryGetValue(core, out var previous)) previous.Dispose();
        Owners.Add(core, this);
        _core = core;
        _documentUri = ViewerUri + "&theme=" + (lightTheme ? "light" : "dark");
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsBuiltInErrorPageEnabled = false;
        core.WebResourceRequested += ResourceRequested;
        core.WebMessageReceived += MessageReceived;
        core.NavigationStarting += NavigationStarting;
        core.NavigationCompleted += NavigationCompleted;
        core.FrameNavigationStarting += FrameNavigationStarting;
        core.NewWindowRequested += NewWindowRequested;
        core.PermissionRequested += PermissionRequested;
        core.DownloadStarting += DownloadStarting;
        try
        {
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            _allSourceKinds = true;
        }
        catch (NotImplementedException) { core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All); }
        core.Navigate(_documentUri);
    }

    private bool OwnsCore() => !_disposed && _core is { } core &&
        Owners.TryGetValue(core, out var owner) && ReferenceEquals(owner, this);

    private async void ResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        if (!OwnsCore()) return;
        using var deferral = args.GetDeferral();
        try
        {
            if (!Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var uri) ||
                uri.GetLeftPart(UriPartial.Authority) != Origin || uri.UserInfo.Length != 0 || args.Request.Method != "GET")
            {
                Respond(args, 403, "Blocked", Array.Empty<byte>(), "text/plain");
                return;
            }
            var resource = uri.AbsolutePath;
            if (resource == "/workbook.json" && uri.Query == "?session=" + _sessionId)
            {
                var bytes = await _workbookJson.WaitAsync(_token);
                if (OwnsCore()) Respond(args, 200, "OK", bytes, "application/json; charset=utf-8");
                return;
            }
            if (resource is not ("/viewer.html" or "/viewer.css" or "/viewer.js"))
            {
                Respond(args, 404, "Not found", Array.Empty<byte>(), "text/plain");
                return;
            }
            var path = Path.Combine(_assetDirectory, resource[1..]);
            if (ModelPreviewService.HasReparsePoint(path) || new FileInfo(path).Length > 256 * 1024)
                throw new IOException("Invalid spreadsheet viewer asset.");
            await AssetReads.WaitAsync(_token);
            byte[] assetBytes;
            try { assetBytes = await File.ReadAllBytesAsync(path, _token); }
            finally { AssetReads.Release(); }
            if (OwnsCore()) Respond(args, 200, "OK", assetBytes, Path.GetExtension(path) switch
            {
                ".html" => "text/html; charset=utf-8", ".css" => "text/css; charset=utf-8",
                _ => "application/javascript; charset=utf-8"
            });
        }
        catch (OperationCanceledException)
        {
            if (OwnsCore()) Respond(args, 408, "Cancelled", Array.Empty<byte>(), "text/plain");
        }
        catch (Exception exception)
        {
            if (OwnsCore())
            {
                Respond(args, 500, "Read failed", Array.Empty<byte>(), "text/plain");
                _ready.TrySetException(new IOException("Не удалось подготовить табличный просмотр.", exception));
            }
        }
    }

    private void Respond(CoreWebView2WebResourceRequestedEventArgs args, int status, string reason, byte[] bytes, string type)
    {
        if (!OwnsCore()) return;
        args.Response = _core!.Environment.CreateWebResourceResponse(new MemoryStream(bytes, writable: false), status, reason,
            "Content-Type: " + type + "\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n" +
            "Content-Security-Policy: default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; " +
            "connect-src 'self'; img-src 'none'; font-src 'none'; frame-src 'none'; object-src 'none'; " +
            "worker-src 'none'; base-uri 'none'; form-action 'none'\r\n");
    }

    private void MessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (!OwnsCore() || args.Source != _documentUri) return;
        try
        {
            using var document = JsonDocument.Parse(args.WebMessageAsJson);
            if (!document.RootElement.TryGetProperty("type", out var type)) return;
            if (type.GetString() == "loaded")
            {
                _cancellation.CancelAfter(Timeout.InfiniteTimeSpan);
                _ready.TrySetResult(true);
            }
            else if (type.GetString() == "error")
                _ready.TrySetException(new InvalidDataException("Не удалось отобразить таблицу."));
        }
        catch (JsonException) { }
        catch (InvalidOperationException) { }
    }

    private void NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (!OwnsCore()) return;
        args.Cancel = args.Uri != _documentUri;
        if (!args.Cancel) _documentNavigationId = args.NavigationId;
    }
    private void NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        // Stop/about:blank from the preceding selection can complete after the
        // new session attaches. Only this document's navigation can fail Ready.
        if (OwnsCore() && _documentNavigationId is { } id && args.NavigationId == id && !args.IsSuccess)
            _ready.TrySetException(new IOException("Не удалось открыть табличный просмотр."));
    }
    private static void FrameNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args) => args.Cancel = true;
    private static void NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs args) => args.Handled = true;
    private static void PermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs args) => args.State = CoreWebView2PermissionState.Deny;
    private static void DownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs args) => args.Cancel = true;

    public void Dispose()
    {
        if (_disposed) return;
        var core = _core;
        var wasOwner = core is not null && Owners.TryGetValue(core, out var owner) && ReferenceEquals(owner, this);
        _disposed = true;
        _cancellation.Cancel();
        if (wasOwner) Owners.Remove(core!);
        if (core is not null)
        {
            try
            {
                core.WebResourceRequested -= ResourceRequested;
                core.WebMessageReceived -= MessageReceived;
                core.NavigationStarting -= NavigationStarting;
                core.NavigationCompleted -= NavigationCompleted;
                core.FrameNavigationStarting -= FrameNavigationStarting;
                core.NewWindowRequested -= NewWindowRequested;
                core.PermissionRequested -= PermissionRequested;
                core.DownloadStarting -= DownloadStarting;
                if (wasOwner)
                {
                    core.Stop();
                    core.Navigate("about:blank");
                    if (_allSourceKinds)
                        core.RemoveWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
                    else core.RemoveWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                }
            }
            catch (InvalidOperationException) { }
            catch (COMException) { }
        }
        _core = null;
        _registration.Dispose();
        _cancellation.Dispose();
    }
}
