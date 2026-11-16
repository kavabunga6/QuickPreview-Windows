using System.IO;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace QuickPreview;

internal sealed record ModelPreviewInfo(int MeshCount, int TriangleCount, string Summary);

internal static class ModelPreviewService
{
    internal const string Origin = "https://quickpreview-model.invalid";
    internal const long MaxModelBytes = 128L * 1024 * 1024;
    internal const long MaxTriangles = 500_000;
    private const string ComplexityMessage = "Модель слишком сложная для быстрого предпросмотра. Откройте её в CAD-приложении.";
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".step", ".stp", ".iges", ".igs", ".brep", ".brp", ".fcstd", ".stl", ".obj",
        ".3mf", ".ply", ".off", ".amf", ".3ds", ".dae", ".fbx", ".gltf", ".glb",
        ".wrl", ".3dm", ".ifc", ".bim"
    };
    private static readonly HashSet<string> CompanionExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mtl", ".bin", ".png", ".jpg", ".jpeg", ".bmp", ".webp", ".gif", ".tga", ".dds", ".ktx2"
    };

    public static bool Supports(string path) => Extensions.Contains(Path.GetExtension(path));

    public static Task<ModelPreviewSession> CreateSessionAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var thread = Thread.CurrentThread;
            ThreadPriority? previous = null;
            try
            {
                try { previous = thread.Priority; thread.Priority = ThreadPriority.BelowNormal; }
                catch (Exception exception) when (exception is ThreadStateException or PlatformNotSupportedException or System.Security.SecurityException) { }
                return CreateSession(path, cancellationToken);
            }
            finally
            {
                if (previous is { } priority)
                {
                    try { thread.Priority = priority; }
                    catch (Exception exception) when (exception is ThreadStateException or PlatformNotSupportedException or System.Security.SecurityException) { }
                }
            }
        }, cancellationToken);

    private static ModelPreviewSession CreateSession(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Supports(path)) throw new NotSupportedException("Этот 3D-формат не поддерживается.");
        path = Path.GetFullPath(path);
        var file = new FileInfo(path);
        if (!file.Exists) throw new FileNotFoundException("Файл модели больше не существует.", path);
        if (file.Length > MaxModelBytes)
            throw new NotSupportedException("Модель слишком большая для быстрого предпросмотра (более 128 МБ).");
        if (HasReparsePoint(path))
            throw new NotSupportedException("Для предпросмотра модели откройте исходный файл вместо символической ссылки.");
        ValidateComplexity(path, token);

        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [file.Name] = path };
        // Interchange files can reference sidecar materials, binary buffers and textures.
        // Enumerate only these companion types, never scripts/documents or other models.
        if (Path.GetExtension(path).ToLowerInvariant() is ".obj" or ".gltf" or ".dae" or ".fbx" or ".3ds" or ".3dm")
        {
            var directory = file.DirectoryName!;
            long total = file.Length;
            var visited = 0;
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true, MaxRecursionDepth = 2,
                IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System
            };
            foreach (var companion in new DirectoryInfo(directory).EnumerateFiles("*", options))
            {
                token.ThrowIfCancellationRequested();
                if (++visited > 4_096 || files.Count >= 128) break;
                if (!CompanionExtensions.Contains(companion.Extension) || companion.Length > 32L * 1024 * 1024) continue;
                if (total + companion.Length > 256L * 1024 * 1024) continue;
                var relative = Path.GetRelativePath(directory, companion.FullName).Replace('\\', '/');
                files.TryAdd(relative, companion.FullName);
                total += companion.Length;
            }
        }
        return new ModelPreviewSession(files, token);
    }

    private static void ValidateComplexity(string path, CancellationToken token)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".stl" or ".obj" or ".3mf" or ".fcstd" or ".amf")) return;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            64 * 1024, FileOptions.SequentialScan);
        var header = new byte[84];
        var headerLength = stream.Read(header);
        token.ThrowIfCancellationRequested();
        if (extension == ".stl" && headerLength == 84)
        {
            var triangles = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(80));
            // Exporters may append padding/metadata after the declared facets.
            // Do not let a trailing byte bypass the binary complexity budget.
            if (stream.Length >= 84L + triangles * 50L)
            {
                if (triangles > MaxTriangles) throw new NotSupportedException(ComplexityMessage);
                return;
            }
        }
        stream.Position = 0;
        if (extension is ".3mf" or ".fcstd" || extension == ".amf" && headerLength >= 4 &&
            header[0] == 'P' && header[1] == 'K')
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            long expanded = 0;
            if (archive.Entries.Count > 4096) throw new NotSupportedException(ComplexityMessage);
            foreach (var entry in archive.Entries)
            {
                token.ThrowIfCancellationRequested();
                if (entry.Length > MaxModelBytes || expanded > 256L * 1024 * 1024 - entry.Length)
                    throw new NotSupportedException(ComplexityMessage);
                expanded += entry.Length;
            }
            return;
        }
        if (extension is not (".stl" or ".obj")) return;
        // Count ASCII facets/polygon faces without allocating strings per line or
        // loading a large text file into the preview process. A face can span buffers.
        var buffer = new byte[64 * 1024];
        var keyword = new char[8];
        var keywordLength = 0;
        var words = 0;
        var inWord = false;
        var comment = false;
        long triangleCount = 0, vectorCount = 0;
        void FinishLine()
        {
            if (extension == ".obj" && keywordLength == 1 && keyword[0] == 'f')
                triangleCount += Math.Max(0, words - 3L);
            else if (extension == ".stl" && keywordLength == 5 &&
                keyword[0] == 'f' && keyword[1] == 'a' && keyword[2] == 'c' && keyword[3] == 'e' && keyword[4] == 't')
                triangleCount++;
            if (extension == ".obj" && keyword[0] == 'v' && (keywordLength == 1 ||
                keywordLength == 2 && keyword[1] is 'n' or 't')) vectorCount++;
            if (triangleCount > MaxTriangles || vectorCount > MaxTriangles * 3)
                throw new NotSupportedException(ComplexityMessage);
            keywordLength = 0; words = 0; inWord = false; comment = false;
        }
        for (var count = stream.Read(buffer); count > 0; count = stream.Read(buffer))
        {
            token.ThrowIfCancellationRequested();
            for (var index = 0; index < count; index++)
            {
                var value = buffer[index];
                if (value is 10 or 13) { FinishLine(); continue; }
                if (comment) continue;
                if (value == '#') { comment = true; continue; }
                if (value is 9 or 32) { inWord = false; continue; }
                if (!inWord) { words++; inWord = true; }
                if (words == 1 && keywordLength < keyword.Length) keyword[keywordLength++] = (char)value;
            }
        }
        FinishLine();
    }

    internal static bool HasReparsePoint(string path)
    {
        for (FileSystemInfo? current = new FileInfo(path); current is not null;)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) return true;
            current = current is FileInfo file ? file.Directory : ((DirectoryInfo)current).Parent;
        }
        return false;
    }
}

// A session exposes one selected model and its bounded companion manifest. It never
// maps a disk directory into the browser and never permits outbound requests.
internal sealed class ModelPreviewSession : IDisposable
{
    private static readonly SemaphoreSlim ResourceReads = new(2, 2);
    private readonly Dictionary<string, string> _models;
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private readonly string _assetDirectory = Path.Combine(AppContext.BaseDirectory, "assets", "model-viewer");
    private readonly CancellationTokenSource _cancellation;
    private readonly CancellationToken _token;
    private readonly TaskCompletionSource<ModelPreviewInfo> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CoreWebView2? _core;
    private bool _disposed;
    private bool _allSourceKinds;

    public Task<ModelPreviewInfo> Ready => _ready.Task;
    internal string ViewerUri => $"{ModelPreviewService.Origin}/viewer.html?session={_sessionId}";
    internal IReadOnlyDictionary<string, string> ModelFiles => _models;

    internal ModelPreviewSession(Dictionary<string, string> models, CancellationToken token)
    {
        _models = models;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        _token = _cancellation.Token;
        _cancellation.CancelAfter(TimeSpan.FromSeconds(60));
        _token.Register(() =>
        {
            if (_disposed || token.IsCancellationRequested) _ready.TrySetCanceled(_token);
            else _ready.TrySetException(new TimeoutException("Построение 3D-модели заняло слишком много времени. Откройте её в CAD-приложении."));
        });
    }

    public void Attach(CoreWebView2 core, bool lightTheme)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ModelPreviewSession));
        if (_core is not null) throw new InvalidOperationException("Model preview session is already attached.");
        if (!File.Exists(Path.Combine(_assetDirectory, "viewer.html")))
            throw new FileNotFoundException("В установке отсутствует 3D-просмотрщик. Переустановите QuickPreview.");
        _core = core;
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
        core.Navigate(ViewerUri + "&theme=" + (lightTheme ? "light" : "dark"));
    }

    private async void ResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        using var deferral = args.GetDeferral();
        try
        {
            if (_disposed || !Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var uri) ||
                uri.Scheme != "https" || uri.Host != "quickpreview-model.invalid" || args.Request.Method != "GET")
            {
                Respond(args, 403, "Blocked", Array.Empty<byte>(), "text/plain");
                return;
            }

            var resource = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
            if (resource == "config.json" && uri.Query == "?session=" + _sessionId)
            {
                var urls = _models.Keys.Select(name => ModelPreviewService.Origin + "/models/" + _sessionId + "/" +
                    string.Join('/', name.Split('/').Select(Uri.EscapeDataString))).ToArray();
                var config = JsonSerializer.SerializeToUtf8Bytes(new { modelUrls = urls });
                Respond(args, 200, "OK", config, "application/json; charset=utf-8");
                return;
            }

            string? path = null;
            var isAsset = false;
            var modelPrefix = "models/" + _sessionId + "/";
            if (resource.StartsWith(modelPrefix, StringComparison.Ordinal))
                _models.TryGetValue(resource[modelPrefix.Length..], out path);
            else
            {
                // Some Emscripten modules resolve WASM relative to the document.
                resource = resource switch
                {
                    "web-ifc.wasm" => "vendor/ifc/web-ifc.wasm",
                    "rhino3dm.wasm" => "vendor/rhino/rhino3dm.wasm",
                    "draco_decoder.wasm" => "vendor/draco/draco_decoder.wasm",
                    _ => resource
                };
                if (!resource.Contains('\\') && !resource.Split('/').Any(part => part is ".." or "." or ""))
                {
                    var candidate = Path.GetFullPath(Path.Combine(_assetDirectory, resource.Replace('/', Path.DirectorySeparatorChar)));
                    var prefix = Path.GetFullPath(_assetDirectory) + Path.DirectorySeparatorChar;
                    if (candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                        (resource is "viewer.html" or "viewer.js" or "viewer.css" || resource.StartsWith("vendor/", StringComparison.Ordinal)))
                    {
                        path = candidate;
                        isAsset = true;
                    }
                }
            }

            if (path is null || !File.Exists(path) || ModelPreviewService.HasReparsePoint(path))
            {
                Respond(args, 404, "Not found", Array.Empty<byte>(), "text/plain");
                return;
            }
            var localPath = path;
            string? etag = null;
            if (isAsset)
            {
                var asset = new FileInfo(localPath);
                etag = $"\"{typeof(ModelPreviewSession).Assembly.GetName().Version}-{asset.Length:X}-{asset.LastWriteTimeUtc.Ticks:X}\"";
                if (args.Request.Headers.Contains("If-None-Match") && args.Request.Headers.GetHeader("If-None-Match") == etag)
                {
                    Respond(args, 304, "Not Modified", Array.Empty<byte>(), MimeType(path), etag);
                    return;
                }
            }
            await ResourceReads.WaitAsync(_token);
            byte[] bytes;
            try
            {
                bytes = await Task.Run(async () =>
                {
                    var info = new FileInfo(localPath);
                    if (info.Length > ModelPreviewService.MaxModelBytes) throw new IOException("Model resource exceeds preview limit.");
                    return await File.ReadAllBytesAsync(localPath, _token);
                }, _token);
            }
            finally { ResourceReads.Release(); }
            if (!_disposed) Respond(args, 200, "OK", bytes, MimeType(path), etag);
        }
        catch (OperationCanceledException) { if (!_disposed) Respond(args, 408, "Cancelled", Array.Empty<byte>(), "text/plain"); }
        catch (Exception exception)
        {
            if (!_disposed)
            {
                Respond(args, 500, "Read failed", Array.Empty<byte>(), "text/plain");
                _ready.TrySetException(new IOException("Не удалось прочитать файл 3D-модели.", exception));
            }
        }
    }

    private void Respond(CoreWebView2WebResourceRequestedEventArgs args, int status, string reason, byte[] bytes, string type,
        string? etag = null)
    {
        if (_core is null) return;
        args.Response = _core.Environment.CreateWebResourceResponse(new MemoryStream(bytes, writable: false), status, reason,
            "Content-Type: " + type + "\r\nCache-Control: " + (etag is null ? "no-store" : "private, no-cache") + "\r\n" +
            (etag is null ? "" : "ETag: " + etag + "\r\n") + "X-Content-Type-Options: nosniff\r\n" +
            "Content-Security-Policy: default-src 'none'; script-src 'self' 'unsafe-eval'; style-src 'self'; " +
            "img-src 'self' blob: data:; connect-src 'self' blob: data:; worker-src 'self' blob:; font-src 'self'; " +
            "frame-src 'none'; object-src 'none'; base-uri 'none'\r\n");
    }

    private void MessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (_disposed || !args.Source.StartsWith(ViewerUri, StringComparison.Ordinal)) return;
        try
        {
            using var json = JsonDocument.Parse(args.WebMessageAsJson);
            var message = json.RootElement;
            if (message.GetProperty("type").GetString() == "loaded")
            {
                var meshes = Math.Clamp(message.GetProperty("meshes").GetInt32(), 0, int.MaxValue);
                var triangles = Math.Clamp(message.GetProperty("triangles").GetInt32(), 0, int.MaxValue);
                _cancellation.CancelAfter(Timeout.InfiniteTimeSpan);
                _ready.TrySetResult(new ModelPreviewInfo(meshes, triangles, $"3D-модель · объектов: {meshes:N0} · треугольников: {triangles:N0}"));
            }
            else if (message.GetProperty("type").GetString() == "error")
                _ready.TrySetException(new NotSupportedException(message.TryGetProperty("reason", out var reason) && reason.GetString() == "complexity"
                    ? "Модель слишком сложная для быстрого предпросмотра. Откройте её в CAD-приложении."
                    : "Не удалось построить 3D-модель. Файл повреждён или использует неподдерживаемые элементы формата."));
        }
        catch (JsonException) { }
        catch (InvalidOperationException) { }
        catch (KeyNotFoundException) { }
    }

    private void NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args) =>
        args.Cancel = args.Uri != "about:blank" && !args.Uri.StartsWith(ViewerUri + "&theme=", StringComparison.Ordinal);
    private static void FrameNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args) => args.Cancel = true;
    private static void NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs args) => args.Handled = true;
    private static void PermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs args) => args.State = CoreWebView2PermissionState.Deny;
    private static void DownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs args) => args.Cancel = true;
    private static string MimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8", ".js" => "application/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8", ".wasm" => "application/wasm", ".json" => "application/json",
        ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp",
        ".gif" => "image/gif", _ => "application/octet-stream"
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cancellation.Cancel();
        if (_core is not null)
        {
            try
            {
                _core.Stop();
                _core.Navigate("about:blank");
                _core.WebResourceRequested -= ResourceRequested;
                _core.WebMessageReceived -= MessageReceived;
                _core.NavigationStarting -= NavigationStarting;
                _core.FrameNavigationStarting -= FrameNavigationStarting;
                _core.NewWindowRequested -= NewWindowRequested;
                _core.PermissionRequested -= PermissionRequested;
                _core.DownloadStarting -= DownloadStarting;
                if (_allSourceKinds)
                    _core.RemoveWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
                else _core.RemoveWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            }
            catch (InvalidOperationException) { }
            catch (System.Runtime.InteropServices.COMException) { }
            _core = null;
        }
        _cancellation.Dispose();
    }
}
