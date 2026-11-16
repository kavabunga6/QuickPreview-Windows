using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using QuickPreview;

internal static class Program
{
    private static int _exitCode, _checks;
    [STAThread] private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try { await RunAsync(); }
            catch (Exception error) { Console.Error.WriteLine(error); _exitCode = 1; }
            finally { app.Shutdown(); Dispatcher.ExitAllFrames(); }
        }));
        Dispatcher.Run(); return _exitCode;
    }
    private static async Task RunAsync()
    {
        var fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures"); Directory.CreateDirectory(fixtures);
        var allowed = Path.Combine(fixtures, "boundary.stl"); CreateBinaryStl(allowed, 500000);
        using (var session = await ModelPreviewService.CreateSessionAsync(allowed)) Check(session.ModelFiles.Count == 1, "500k triangle binary STL passes preflight boundary without reading its payload");
        var tooLarge = Path.Combine(fixtures, "over-budget.stl"); CreateBinaryStl(tooLarge, 500001);
        await RejectAsync(tooLarge, "Binary STL rejected by header before browser import");
        using (var padding = new FileStream(tooLarge, FileMode.Append)) padding.WriteByte(0);
        await RejectAsync(tooLarge, "Trailing STL padding cannot bypass the binary triangle budget");
        var obj = Path.Combine(fixtures, "over-budget.obj");
        using (var writer = new StreamWriter(obj, false, new UTF8Encoding(false)))
        {
            writer.WriteLine("v 0 0 0\nv 1 0 0\nv 0 1 0");
            for (var i = 0; i < 500001; i++) writer.WriteLine("f 1 2 3 # original repeated triangle");
        }
        await RejectAsync(obj, "OBJ polygon preflight rejects excess faces with bounded streaming memory");
        var archive = Path.Combine(fixtures, "oversized-entry.3mf");
        using (var zip = new ZipArchive(File.Create(archive), ZipArchiveMode.Create)) using (var entry = zip.CreateEntry("3D/3dmodel.model").Open()) entry.WriteByte(1);
        var bytes = File.ReadAllBytes(archive);
        for (var i = 0; i + 30 < bytes.Length; i++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i, 4)) == 0x02014b50)
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i + 24, 4), 129 * 1024 * 1024);
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i, 4)) == 0x04034b50)
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i + 22, 4), 129 * 1024 * 1024);
        }
        File.WriteAllBytes(archive, bytes); await RejectAsync(archive, "Compressed container rejected from expanded-size metadata before decompression");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try { using var unused = await ModelPreviewService.CreateSessionAsync(obj, cancelled.Token); throw new Exception("Missing cancellation"); }
            catch (OperationCanceledException) { Check(true, "Preflight honours cancellation"); }
        }
        var small = Path.Combine(fixtures, "triangle.obj"); File.WriteAllText(small, "v 0 0 0\nv 10 0 0\nv 0 10 0\nf 1 2 3\n");
        CoreWebView2Environment.SetLoaderDllFolderPath(AppContext.BaseDirectory);
        var browser = new WebView2(); var window = new Window { Content = browser, Width = 1600, Height = 1000, ShowActivated = false };
        window.Show();
        try
        {
            await browser.EnsureCoreWebView2Async(await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(AppContext.BaseDirectory, "WebView2-test")));
            await browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Emulation.setDeviceMetricsOverride", "{\"width\":1600,\"height\":1000,\"deviceScaleFactor\":4,\"mobile\":false}");
            using var session = await ModelPreviewService.CreateSessionAsync(small); session.Attach(browser.CoreWebView2, true);
            await session.Ready.WaitAsync(TimeSpan.FromSeconds(20)); await Task.Delay(150);
            using var drawing = JsonDocument.Parse(await browser.CoreWebView2.ExecuteScriptAsync("(()=>{const n=window.quickPreviewModel.viewer.GetViewer(); return {ratio:n.renderer.getPixelRatio(),pixels:n.renderer.domElement.width*n.renderer.domElement.height,frames:n.renderer.info.render.frame};})()"));
            Check(drawing.RootElement.GetProperty("ratio").GetDouble() <= 1.5, "4x-DPI device uses at most1.5x drawing ratio");
            Check(drawing.RootElement.GetProperty("pixels").GetInt64() <= 2073600, "Drawing buffer stays within Full-HD pixel budget at large size");
            var initialFrame = drawing.RootElement.GetProperty("frames").GetInt64(); await Task.Delay(250);
            var idleFrame = long.Parse(await browser.CoreWebView2.ExecuteScriptAsync("window.quickPreviewModel.viewer.GetViewer().renderer.info.render.frame"));
            Check(idleFrame == initialFrame, "Loaded idle model renders zero extra frames");
            await browser.CoreWebView2.ExecuteScriptAsync("(()=>{const n=window.quickPreviewModel.viewer.GetViewer(),original=n.GetBoundingSphere.bind(n);window.fitBoundsCalls=0;window.beforeBurst=n.renderer.info.render.frame;n.GetBoundingSphere=(f)=>{window.fitBoundsCalls++;return original(f);};for(let i=0;i<20;i++){document.getElementById('fit').click();n.ResizeRenderer(1600,1000);n.Render();}})()");
            await Task.Delay(150);
            using var burst = JsonDocument.Parse(await browser.CoreWebView2.ExecuteScriptAsync("({frames:window.quickPreviewModel.viewer.GetViewer().renderer.info.render.frame-window.beforeBurst,bounds:window.fitBoundsCalls})"));
            Check(burst.RootElement.GetProperty("frames").GetInt32() <= 2, "20 synchronous fit/resize/render requests coalesce into at most2 frames");
            Check(burst.RootElement.GetProperty("bounds").GetInt32() == 0, "Fit and resize reuse cached model bounds");
            // Exercise the actual importer success boundary with an oversized model
            // record. If the check happened after conversion, this model's missing
            // geometry methods would cause a generic conversion failure instead.
            var rejected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            browser.CoreWebView2.WebMessageReceived += (_, args) => { using var m = JsonDocument.Parse(args.WebMessageAsJson); if (m.RootElement.TryGetProperty("reason", out var reason) && reason.GetString() == "complexity") rejected.TrySetResult(); };
            await browser.CoreWebView2.ExecuteScriptAsync("(()=>{const v=window.quickPreviewModel.viewer,importer=v.modelLoader.GetImporter();importer.ImportLoadedFiles=(_settings,callbacks)=>callbacks.onImportSuccess({model:{TriangleCount:()=>500001}});v.LoadModelFromUrlList([location.origin+'/models/'+new URLSearchParams(location.search).get('session')+'/triangle.obj']);})()");
            await rejected.Task.WaitAsync(TimeSpan.FromSeconds(5)); Check(true, "Complexity rejected before expanding imported model into Three geometry");
        }
        finally { browser.Dispose(); window.Close(); }
        Console.WriteLine($"PASS: {_checks} model resource-budget and dirty-render checks.");
    }
    private static void CreateBinaryStl(string path, uint count)
    {
        using var stream = File.Create(path); var header = new byte[84]; BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(80), count); stream.Write(header); stream.SetLength(84L + count * 50L);
    }
    private static async Task RejectAsync(string path, string name)
    {
        try { using var unused = await ModelPreviewService.CreateSessionAsync(path); throw new Exception("Resource budget did not reject " + name); }
        catch (NotSupportedException error) { Check(error.Message.Contains("сложная"), name); }
    }
    private static void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); _checks++; Console.WriteLine("PASS: " + name); }
}
