using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using QuickPreview;

internal static class Program
{
    private static readonly string TestRoot=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../.."));
    private static readonly List<object> Results=new();
    private static string _label="before";
    private static int _exit;
    private static CoreWebView2Environment? _environment;
    private static WebView2? _browser;
    private static FrameState? _lastFrames;
    private static MainWindow? _preview;
    private static IntPtr _explorer;
    private static string? _phaseNote;
    private static readonly List<string> Errors=new();
    private static readonly Stopwatch RunWatch=new();

    [STAThread]
    private static int Main(string[] args)
    {
        if(args.FirstOrDefault()=="--compare"){ComparisonReport.Write(Path.Combine(TestRoot,"artifacts"));return 0;}
        if(args.Length>0)_label=args[0];
        var app=new App();app.InitializeComponent();
        var startup=typeof(App).GetMethod("App_OnStartup",BindingFlags.Instance|BindingFlags.NonPublic)!;
        app.Startup-=(StartupEventHandler)Delegate.CreateDelegate(typeof(StartupEventHandler),app,startup);
        app.DispatcherUnhandledException+=(_,e)=>{Errors.Add(e.Exception.ToString());_exit=1;e.Handled=true;};
        app.Dispatcher.BeginInvoke(new Action(async()=>
        {
            try{await RunAsync(app);}
            catch(Exception error){Console.Error.WriteLine(error);Errors.Add(error.ToString());_exit=1;}
            finally
            {
                _preview?.HidePreview();_browser?.Dispose();
                Directory.CreateDirectory(Path.Combine(TestRoot,"artifacts"));
                var report=new{Label=_label,TimestampUtc=DateTime.UtcNow,AppAssembly=typeof(App).Assembly.GetName().Version?.ToString(),AppSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(App).Assembly.Location))),ViewerSha256=Hash("assets/model-viewer/viewer.js"),LogicalProcessors=Environment.ProcessorCount,DurationSeconds=RunWatch.Elapsed.TotalSeconds,Units=new{Cpu="CPU core milliseconds per second; 1000 means one full logical core",Memory="MiB private bytes",Lag="milliseconds, dispatcher Background priority probes every40ms",Explorer="milliseconds for WM_NULL roundtrip to only owned Explorer HWND"},Results,Errors};
                File.WriteAllText(Path.Combine(TestRoot,"artifacts",_label+".json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
                if(_label=="after")ComparisonReport.Write(Path.Combine(TestRoot,"artifacts"));
                app.Shutdown();Dispatcher.ExitAllFrames();
            }
        }));
        Dispatcher.Run();return _exit;
    }

    private static async Task RunAsync(App app)
    {
        var fixtures=Path.Combine(TestRoot,"artifacts","fixtures");Directory.CreateDirectory(fixtures);
        PrepareFixtures(fixtures);
        using var explorer=await NativeExplorerFixture.OpenAsync(fixtures);_explorer=explorer.Handle;
        _preview=new MainWindow();app.MainWindow=_preview;
        typeof(App).GetField("_previewWindow",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(app,_preview);
        RunWatch.Start();
        await MeasureAsync("no-preview-idle",()=>Task.Delay(3000));
        CoreWebView2Environment.SetLoaderDllFolderPath(AppContext.BaseDirectory);
        var environmentTask=CoreWebView2Environment.CreateAsync(userDataFolder:Path.Combine(TestRoot,"artifacts","profiles",_label+"-"+Guid.NewGuid().ToString("N")));
        Field("_webEnvironmentTask").SetValue(_preview,environmentTask);
        _environment=await environmentTask;
        _browser=(WebView2)_preview.FindName("ModelWebView");
        await MeasureAsync("step-load",()=>LoadAsync(Path.Combine(fixtures,"cube.step"),10000));
        await InstrumentAsync();
        await MeasureAsync("step-ready-idle",()=>Task.Delay(3000));
        await MeasureAsync("step-origin-foreground-production-poll",async()=>
        {
            explorer.Select("cube.step");await explorer.ForegroundAsync();
            var service=typeof(App).Assembly.GetType("QuickPreview.ExplorerSelectionService",true)!;
            service.GetMethod("TryGetSelectedPath",BindingFlags.Static|BindingFlags.Public)!.Invoke(null,null);
            var tick=typeof(App).GetMethod("SynchronizeExplorerSelection",BindingFlags.Instance|BindingFlags.NonPublic)!;
            var timer=new DispatcherTimer(DispatcherPriority.Background){Interval=TimeSpan.FromMilliseconds(120)};
            timer.Tick+=(_,_)=>tick.Invoke(app,null);timer.Start();
            try{await Task.Delay(4000);}finally{timer.Stop();service.GetMethod("ForgetOriginWindow",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,null);}
        });
        ((TabControl)_preview.FindName("PreviewTabs")).SelectedIndex=1;await Task.Delay(150);
        await MeasureAsync("step-text-tab-idle",()=>Task.Delay(3000));
        ((TabControl)_preview.FindName("PreviewTabs")).SelectedIndex=0;_preview.WindowState=WindowState.Minimized;await Task.Delay(150);
        await MeasureAsync("step-minimized-idle",()=>Task.Delay(3000));
        _preview.WindowState=WindowState.Normal;
        await MeasureAsync("stl-200k-load",()=>LoadAsync(Path.Combine(fixtures,"mesh-200k.stl"),10000));
        await InstrumentAsync();
        await MeasureAsync("stl-200k-ready-idle",()=>Task.Delay(3000));
        await MeasureAsync("stl-200k-camera-interaction",InteractionAsync);
        _preview.HidePreview();
        await MeasureAsync("hidden-after-200k-idle",()=>Task.Delay(3000));
        await MeasureAsync("obj-200k-load",()=>LoadAsync(Path.Combine(fixtures,"mesh-200k.obj"),10000));
        await InstrumentAsync();
        await MeasureAsync("obj-200k-ready-idle",()=>Task.Delay(2000));
        await MeasureAsync("stl-1m-rapid-cancel-to-cube",async()=>
        {
            _preview.ShowPreview(Path.Combine(fixtures,"mesh-1m.stl"),activateWindow:false);await Task.Delay(500);_phaseNote=DescribeModelState();
            await LoadAsync(Path.Combine(fixtures,"cube.stl"),8000);
        });
        await InstrumentAsync();
        await MeasureAsync("idle-after-stl-cancel",()=>Task.Delay(3000));
        await MeasureAsync("obj-1m-cancel-by-hide",async()=>
        {
            _preview.ShowPreview(Path.Combine(fixtures,"mesh-1m.obj"),activateWindow:false);await Task.Delay(500);_phaseNote=DescribeModelState();_preview.HidePreview();await Task.Delay(1000);
        });
        await MeasureAsync("hidden-after-obj-cancel-idle",()=>Task.Delay(3000));
        Console.WriteLine("BENCHMARK COMPLETE: "+_label+" "+RunWatch.Elapsed.TotalSeconds.ToString("0.0",CultureInfo.InvariantCulture)+"s");
    }

    private static async Task LoadAsync(string path,int timeout)
    {
        _preview!.ShowPreview(path,activateWindow:false);var watch=Stopwatch.StartNew();
        while(watch.ElapsedMilliseconds<timeout)
        {
            if(Field("_modelSession").GetValue(_preview)is{}session)
            {
                var ready=(Task)session.GetType().GetProperty("Ready")!.GetValue(session)!;
                if(ready.IsCompleted){await ready;await Task.Delay(100);return;}
            }
            if(((FrameworkElement)_preview.FindName("ErrorView")).IsVisible)throw new InvalidOperationException("Preview rejected "+Path.GetFileName(path)+": "+((TextBlock)_preview.FindName("ErrorText")).Text);
            await Task.Delay(25);
        }
        _preview.HidePreview();throw new TimeoutException("Bounded model load exceeded "+timeout+"ms: "+Path.GetFileName(path));
    }
    private static async Task InstrumentAsync()
    {
        if(_browser?.CoreWebView2 is null)return;
        await _browser.CoreWebView2.ExecuteScriptAsync("(()=>{const n=window.quickPreviewModel?.viewer.GetViewer();if(!n||n.__performanceWrapped)return;n.__performanceWrapped=true;window.__benchmarkRenderCalls=0;const render=n.Render.bind(n);n.Render=function(...a){window.__benchmarkRenderCalls++;return render(...a);};})()");
    }
    private static async Task InteractionAsync()
    {
        if(_browser?.CoreWebView2 is null)return;
        var until=Stopwatch.StartNew();
        while(until.ElapsedMilliseconds<3000)
        {
            await _browser.CoreWebView2.ExecuteScriptAsync("(()=>{const n=window.quickPreviewModel?.viewer.GetViewer();if(n){n.navigation.Orbit(2,0);n.navigation.Update();}})()");await Task.Delay(40);
        }
    }
    private static async Task<FrameState?> FramesAsync()
    {
        try
        {
            if(_browser?.CoreWebView2 is null)return null;
            // Reading JS must not wake a deliberately suspended renderer. Cached
            // counters are labeled suspended; no frame delta is inferred from them.
            if(_browser.CoreWebView2.IsSuspended)return _lastFrames is null?null:_lastFrames with{Suspended=true,Hidden=true};
            var json=await _browser.CoreWebView2.ExecuteScriptAsync("(()=>{const n=window.quickPreviewModel?.viewer.GetViewer();return n?{Frame:n.renderer?.info.render.frame??0,Calls:window.__benchmarkRenderCalls??0,CanvasWidth:n.renderer?.domElement.width??0,CanvasHeight:n.renderer?.domElement.height??0,Triangles:window.quickPreviewModel.triangles,Hidden:document.hidden}:null;})()").WaitAsync(TimeSpan.FromMilliseconds(500));
            _lastFrames=JsonSerializer.Deserialize<FrameState>(json);return _lastFrames;
        }
        catch{return null;}
    }

    private static async Task MeasureAsync(string name,Func<Task> action)
    {
        _phaseNote=null;
        if(RunWatch.ElapsedMilliseconds>75000)throw new TimeoutException("Bounded benchmark budget reached before another phase");
        Console.WriteLine("PHASE "+name);
        var processes=new HashSet<int>{Environment.ProcessId};var browserIds=new HashSet<int>();
        void RefreshIds(){if(_environment is null)return;foreach(var item in _environment.GetProcessInfos()){processes.Add(item.ProcessId);browserIds.Add(item.ProcessId);}}
        RefreshIds();var before=await FramesAsync();
        var beforeCpu=new Dictionary<int,double>();var finalCpu=new Dictionary<int,double>();var memory=new Dictionary<int,long>();
        foreach(var id in browserIds)try{using var process=Process.GetProcessById(id);beforeCpu[id]=process.TotalProcessorTime.TotalMilliseconds;}catch(ArgumentException){}
        // Boundary CPU snapshots keep expensive Process memory observations out of
        // the measured app interval. Sampling private bytes every40ms can itself
        // dominate CPU on Windows with many system processes.
        using var appProcess=Process.GetCurrentProcess();beforeCpu[Environment.ProcessId]=appProcess.TotalProcessorTime.TotalMilliseconds;
        var lags=new List<double>();var health=new List<double>();
        using var cancel=new CancellationTokenSource();var phase=Stopwatch.StartNew();
        var sampler=Task.Run(async()=>
        {
            while(!cancel.IsCancellationRequested)
            {
                var lag=Stopwatch.StartNew();await Application.Current.Dispatcher.InvokeAsync(()=>lags.Add(lag.Elapsed.TotalMilliseconds),DispatcherPriority.Background);
                var reply=Stopwatch.StartNew();_=SendMessageTimeout(_explorer,0,IntPtr.Zero,IntPtr.Zero,2,250,out _);health.Add(reply.Elapsed.TotalMilliseconds);
                await Task.Delay(40);
            }
        });
        string status="ready";
        try
        {
            var work=action();
            while(!work.IsCompleted)await Task.WhenAny(work,Task.Delay(100));
            await work;
        }
        catch(Exception error){status=error.GetType().Name+": "+error.Message;Errors.Add(name+": "+status);_exit=1;Console.WriteLine(status);_preview?.HidePreview();}
        finally{cancel.Cancel();await sampler;}
        var seconds=phase.Elapsed.TotalSeconds;
        appProcess.Refresh();finalCpu[Environment.ProcessId]=appProcess.TotalProcessorTime.TotalMilliseconds;
        RefreshIds();
        foreach(var id in processes)try{using var process=Process.GetProcessById(id);if(id!=Environment.ProcessId)finalCpu[id]=process.TotalProcessorTime.TotalMilliseconds;memory[id]=process.PrivateMemorySize64;}catch(ArgumentException){}catch(InvalidOperationException){}
        var after=await FramesAsync();
        double Cpu(IEnumerable<int> ids)=>ids.Sum(id=>Math.Max(0,finalCpu.GetValueOrDefault(id)-beforeCpu.GetValueOrDefault(id)))/seconds;
        double Mb(IEnumerable<int> ids)=>ids.Sum(id=>memory.GetValueOrDefault(id))/1048576d;
        var result=new{Name=name,Status=status,ActionNote=_phaseNote,Seconds=seconds,AppCpuCoreMsPerSecond=Cpu(new[]{Environment.ProcessId}),WebViewCpuCoreMsPerSecond=Cpu(browserIds),AppPrivateMiBAtEnd=Mb(new[]{Environment.ProcessId}),WebViewPrivateMiBAtEnd=Mb(browserIds),DispatcherLagP95Ms=Percentile(lags,.95),DispatcherLagMaxMs=lags.DefaultIfEmpty().Max(),ExplorerWmNullP95Ms=Percentile(health,.95),ExplorerWmNullMaxMs=health.DefaultIfEmpty().Max(),RenderFrameDelta=before is{Suspended:false}&&after is{Suspended:false}?after.Frame-before.Frame:(long?)null,RenderCallDelta=before is{Suspended:false}&&after is{Suspended:false}?after.Calls-before.Calls:(long?)null,FrameBefore=before,FrameAfter=after,ProcessIds=browserIds.OrderBy(id=>id).ToArray()};
        Results.Add(result);File.WriteAllText(Path.Combine(TestRoot,"artifacts",_label+"-partial.json"),JsonSerializer.Serialize(Results,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine(JsonSerializer.Serialize(result));
    }
    private static double Percentile(List<double> values,double percentile)=>values.Count==0?0:values.OrderBy(value=>value).ElementAt((int)Math.Min(values.Count-1,Math.Ceiling(values.Count*percentile)-1));
    private static string DescribeModelState()=>((FrameworkElement)_preview!.FindName("ErrorView")).IsVisible?"Rejected before cancellation: "+((TextBlock)_preview.FindName("ErrorText")).Text:Field("_modelSession").GetValue(_preview)is{}session?"Import state before cancellation: "+((Task)session.GetType().GetProperty("Ready")!.GetValue(session)!).Status:"No model session before cancellation";
    private static FieldInfo Field(string name)=>typeof(MainWindow).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!;
    private static string? Hash(string relative){var path=Path.Combine(AppContext.BaseDirectory,relative);return File.Exists(path)?Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))):null;}
    private static void PrepareFixtures(string directory)
    {
        var original=Path.GetFullPath(Path.Combine(TestRoot,"../ModelSmoke/bin/Release/net6.0-windows10.0.17763.0/fixtures"));
        foreach(var extension in new[]{"step","stl"})File.Copy(Path.Combine(original,"cube."+extension),Path.Combine(directory,"cube."+extension),true);
        foreach(var(count,label)in new[]{(200000,"200k"),(1000000,"1m")})
        {
            var stl=Path.Combine(directory,"mesh-"+label+".stl");
            if(!File.Exists(stl))using(var writer=new BinaryWriter(File.Create(stl))){writer.Write(new byte[80]);writer.Write((uint)count);for(var i=0;i<count;i++){var x=i%500;var y=i/500;foreach(var value in new float[]{0,0,1,x,y,0,x+1,y,0,x,y+1,0})writer.Write(value);writer.Write((ushort)0);}}
            var obj=Path.Combine(directory,"mesh-"+label+".obj");
            if(!File.Exists(obj))using(var writer=new StreamWriter(obj,false,new UTF8Encoding(false),1024*1024)){writer.WriteLine("# Original deterministic planar performance fixture");for(var i=0;i<count;i++){var x=i%500;var y=i/500;writer.WriteLine($"v {x} {y} 0\nv {x+1} {y} 0\nv {x} {y+1} 0");}for(var i=0;i<count;i++){var start=i*3+1;writer.WriteLine($"f {start} {start+1} {start+2}");}}
        }
    }
    private sealed record FrameState(long Frame,long Calls,int CanvasWidth,int CanvasHeight,int Triangles,bool Hidden,bool Suspended=false);
    [DllImport("user32.dll")]private static extern IntPtr SendMessageTimeout(IntPtr window,uint message,IntPtr wParam,IntPtr lParam,uint flags,uint timeout,out IntPtr result);
}
