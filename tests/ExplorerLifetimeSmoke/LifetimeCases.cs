using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using QuickPreview;

internal static class LifetimeCases
{
    private static readonly Type Service=typeof(App).Assembly.GetType("QuickPreview.ExplorerSelectionService",true)!;
    private static readonly Type IdentityType=typeof(App).Assembly.GetType("QuickPreview.ExplorerOriginIdentity",true)!;
    private static int _checks;
    private static long _binding=10000;
    internal static async Task RunAsync(App app)
    {
        var output=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../artifacts"));Directory.CreateDirectory(output);
        var directory=Path.Combine(output,"regression-fixtures",Guid.NewGuid().ToString("N"));
        var first=Path.Combine(directory,"Origin");var child=Path.Combine(first,"Navigated");var neighbor=Path.Combine(directory,"Unrelated");
        foreach(var path in new[]{first,child,neighbor}){Directory.CreateDirectory(path);File.WriteAllText(Path.Combine(path,"Original.txt"),"Original lifetime text "+Path.GetFileName(path));}
        WriteCubeStl(Path.Combine(child,"Cube.stl"));
        using var origin=await NativeExplorerFixture.OpenAsync(first);
        using var probe=new ShellLifetimeProbe(origin.Handle,directory);
        var explorer=typeof(NativeExplorerFixture).GetField("_explorer",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(origin)!;
        var preview=new MainWindow();app.MainWindow=preview;
        typeof(App).GetField("_previewWindow",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(app,preview);
        Window? foreground=null;
        try
        {
            origin.Select("Original.txt");await origin.ForegroundAsync();
            var path=Path.Combine(first,"Original.txt");Check((string?)Call("TryGetSelectedPath")==path,"Real native selection attaches origin");
            var captured=Identity!;var tab=Property<IntPtr>(captured,"TabWindow");
            Check(tab!=IntPtr.Zero&&tab!=origin.Handle,"Real browser captures persistent ShellTab host rather than transient view");
            preview.ShowPreview(path,activateWindow:false);await WaitTextAsync(preview,path);
            Check(Lifetime(captured)=="Alive","Captured real origin passes native HWND identity checks");
            ((dynamic)explorer).Navigate2(child,0);await Task.Delay(650);
            Check(preview.IsVisible&&ReferenceEquals(captured,Identity),"Normal folder navigation preserves preview and origin binding");
            Check(Lifetime(captured)=="Alive","View recreation does not close the persistent browser host");
            using(var unrelated=await NativeExplorerFixture.OpenAsync(neighbor)){}
            await Task.Delay(650);Check(preview.IsVisible&&ReferenceEquals(captured,Identity),"Closing unrelated owned Explorer leaves preview open");

            await origin.ForegroundAsync();SelectCurrent(explorer,"Cube.stl");
            var cube=Path.Combine(child,"Cube.stl");Check((string?)Call("TryGetSelectedPath")==cube,"Real origin rebinds selected cube after folder navigation");
            CoreWebView2Environment.SetLoaderDllFolderPath(AppContext.BaseDirectory);
            Field("_webEnvironmentTask").SetValue(preview,CoreWebView2Environment.CreateAsync(userDataFolder:Path.Combine(output,"profiles",Guid.NewGuid().ToString("N"))));
            preview.ShowPreview(cube,activateWindow:false);await WaitModelAsync(preview);
            var token=((CancellationTokenSource)Field("_previewCancellation").GetValue(preview)!).Token;
            var session=Field("_modelSession").GetValue(preview)!;
            Check((IntPtr)Field("_originLifetimeHook").GetValue(preview)! != IntPtr.Zero,"Real origin installs native destruction hook without App startup");
            foreground=new Window{Title="QuickPreview owned lifetime foreground fixture",Width=230,Height=120,Left=1000,Top=700};foreground.Show();
            var foregroundHandle=new WindowInteropHelper(foreground).Handle;await FocusAsync(foreground,foregroundHandle);
            var close=Stopwatch.StartNew();probe.CloseOwnedFrame();await WaitAsync(()=>!preview.IsVisible,"Real origin close auto-hides preview");
            Check(token.IsCancellationRequested,"Origin close cancels active preview request");
            Check(Field("_modelSession").GetValue(preview)is null&&(bool)session.GetType().GetField("_disposed",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(session)!,"Origin close disposes loaded 3D session");
            Check(Identity is null&&!(bool)CallProperty("HasOriginWindow")!,"Origin close clears captured binding");
            Check(Native.GetForegroundWindow()==foregroundHandle,"Auto-hide preserves other foreground window");
            Console.WriteLine("METRIC real Explorer close→hidden: "+close.ElapsedMilliseconds+"ms");
            probe.Snapshot("real-origin-closed");probe.Save(Path.Combine(output,"real-close.json"));
            await ProtocolAsync(preview,Path.Combine(child,"Original.txt"),foregroundHandle);
            Console.WriteLine($"PASS Explorer origin lifetime: {_checks} checks; real window close/folder navigation and native two-host protocol.");
        }
        finally
        {
            preview.HidePreview();typeof(MainWindow).GetMethod("StopOriginLifetimeMonitoring",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(preview,null);
            ((WebView2)preview.FindName("ModelWebView")).Dispose();((WebView2)preview.FindName("PdfWebView")).Dispose();
            foreground?.Close();Call("ForgetOriginWindow");
        }
    }

    private static async Task ProtocolAsync(MainWindow preview,string text,IntPtr foreground)
    {
        using var tabs=new NativeTabFixture();var captured=MakeIdentity(tabs.Frame,tabs.First);
        Bind(captured);preview.ShowPreview(text,activateWindow:false);await WaitTextAsync(preview,text);
        Check(Lifetime(captured)=="Alive"&&(bool)CallProperty("IsOriginTabActive")!,"Native two-host binding starts on the active captured host");
        var callback=typeof(MainWindow).GetMethod("OriginLifetimeWindowDestroyed",BindingFlags.Instance|BindingFlags.NonPublic)!;
        void Notify(object identity,IntPtr window,uint? thread=null,uint? time=null,int objectId=0,int childId=0)=>callback.Invoke(preview,new object[]{identity,0x8001u,window,objectId,childId,thread??Property<uint>(identity,"TabThreadId"),time??unchecked((uint)Environment.TickCount)});
        Notify(captured,tabs.Second);Notify(captured,tabs.First,objectId:-4);Notify(captured,tabs.First,childId:1);
        Notify(captured,tabs.First,thread:Property<uint>(captured,"TabThreadId")+1);
        Notify(captured,tabs.First,time:Property<uint>(captured,"CapturedAt")-1);
        await Task.Delay(80);Check(preview.IsVisible&&ReferenceEquals(Identity,captured),"Neighbor/object/child/thread/older-time destruction notifications are ignored");
        tabs.Select(tabs.Second);await Task.Delay(650);
        Check(!(bool)CallProperty("IsOriginTabActive")!&&preview.IsVisible,"Switching tabs makes origin inactive without hiding it");
        Check(Call("TryGetSelectedPathInOriginWindow")is null,"Inactive origin cannot read another active tab's selection");
        tabs.CloseTab(tabs.Second);await Task.Delay(650);
        Check(preview.IsVisible&&Lifetime(captured)=="Alive","Actual neighboring host destruction leaves captured host and preview alive");
        var replacement=tabs.AddTab();tabs.Select(replacement);
        var token=((CancellationTokenSource)Field("_previewCancellation").GetValue(preview)!).Token;
        tabs.CloseTab(tabs.First);await WaitAsync(()=>!preview.IsVisible,"Native fallback detects origin host closure while frame survives");
        Check(NativeTabFixture.IsWindow(tabs.Frame)&&NativeTabFixture.IsWindow(replacement),"Origin tab close leaves frame and neighboring host alive");
        Check(token.IsCancellationRequested&&Identity is null,"Origin tab close cancels request and forgets binding");
        Check(Native.GetForegroundWindow()==foreground,"Origin tab auto-hide never restores unrelated Explorer focus");

        using var second=new NativeTabFixture();var old=MakeIdentity(second.Frame,second.First);Bind(old);preview.ShowPreview(text,false);await WaitTextAsync(preview,text);
        Notify(old,second.First);
        second.Select(second.Second);var latest=MakeIdentity(second.Frame,second.Second);Bind(latest);preview.ShowPreview(text,false);
        await WaitTextAsync(preview,text);await Task.Delay(650);
        Check(preview.IsVisible&&ReferenceEquals(Identity,latest),"Queued old-origin close cannot hide a newly rebound preview");
        Notify(old,second.First);await Task.Delay(80);Check(preview.IsVisible,"Late detached-hook callback remains harmless");
        Notify(latest,second.Second);await WaitAsync(()=>!preview.IsVisible,"Matching destroy proof closes even if numeric HWND is still alive");
        Check(NativeTabFixture.IsWindow(second.Second),"Destroy-proof check does not rely on immediate IsWindow=false");
        Check(Native.GetForegroundWindow()==foreground,"Synthetic native notification preserves current foreground");

        var invalid=MakeIdentity(second.Frame,second.Second,processDelta:1);Check(Lifetime(invalid)=="Closed","Native process stamp rejects reused/mismatched identity");
        invalid=MakeIdentity(second.Frame,second.Second,threadDelta:1);Check(Lifetime(invalid)=="Closed","Native thread stamp rejects reused/mismatched identity");
        invalid=MakeIdentity(second.Frame,second.Second,tabClass:"UnrelatedClass");Check(Lifetime(invalid)=="Closed","Native class stamp rejects reused/mismatched identity");
        using var detached=new NativeTabFixture();invalid=MakeIdentity(second.Frame,detached.First);Check(Lifetime(invalid)=="Closed","Unrelated child cannot be attached to captured frame");
        var legacy=MakeIdentity(second.Frame,second.Frame);Check(Lifetime(legacy)=="Alive","Legacy frame-only identity remains supported");

        Call("ForgetOriginWindow");preview.ShowPreview(text,false);await WaitTextAsync(preview,text);
        var literalToken=((CancellationTokenSource)Field("_previewCancellation").GetValue(preview)!).Token;
        second.CloseTab(second.Second);await Task.Delay(650);
        Check(preview.IsVisible&&!literalToken.IsCancellationRequested,"Literal file preview without origin ignores unrelated window/tab destruction");
        Check(Native.GetForegroundWindow()==foreground,"Literal preview retains foreground during unrelated destruction");
    }

    private static object MakeIdentity(IntPtr frame,IntPtr tab,uint processDelta=0,uint threadDelta=0,string? tabClass=null)
    {
        var frameStamp=NativeTabFixture.Stamp(frame);var tabStamp=NativeTabFixture.Stamp(tab);
        return Activator.CreateInstance(IdentityType,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,new object[]{++_binding,frame,tab,frameStamp.Process+processDelta,frameStamp.Thread,tabStamp.Thread+threadDelta,unchecked((uint)Environment.TickCount),NativeTabFixture.ClassName(frame),tabClass??NativeTabFixture.ClassName(tab)},null)!;
    }
    private static void Bind(object identity)
    {
        Service.GetField("_originIdentity",BindingFlags.Static|BindingFlags.NonPublic)!.SetValue(null,identity);
        Service.GetField("_originExplorerWindow",BindingFlags.Static|BindingFlags.NonPublic)!.SetValue(null,Property<IntPtr>(identity,"FrameWindow"));
        var generation=Service.GetField("_originGeneration",BindingFlags.Static|BindingFlags.NonPublic)!;generation.SetValue(null,(int)generation.GetValue(null)!+1);
        ((Action?)Service.GetField("OriginIdentityChanged",BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null))?.Invoke();
    }
    private static object? Identity=>CallProperty("OriginIdentity");
    private static string Lifetime(object value)=>Call("CheckOriginLifetime",value)!.ToString()!;
    private static object? CallProperty(string name)=>Service.GetProperty(name,BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic)!.GetValue(null);
    private static object? Call(string name,params object[]args)=>Service.GetMethod(name,BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic)!.Invoke(null,args);
    private static T Property<T>(object value,string name)=>(T)value.GetType().GetProperty(name)!.GetValue(value)!;
    private static FieldInfo Field(string name)=>typeof(MainWindow).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!;
    private static async Task WaitTextAsync(MainWindow window,string path)=>await WaitAsync(()=>window.IsVisible&&((TextBox)window.FindName("PreviewText")).Text==File.ReadAllText(path),"Original text visible");
    private static async Task WaitModelAsync(MainWindow window)
    {
        await WaitAsync(()=>Field("_modelSession").GetValue(window)is not null,"3D session attached");var session=Field("_modelSession").GetValue(window)!;
        await ((Task)session.GetType().GetProperty("Ready")!.GetValue(session)!).WaitAsync(TimeSpan.FromSeconds(15));
        await WaitAsync(()=>((FrameworkElement)window.FindName("ModelView")).IsVisible,"3D model visible");
    }
    private static async Task WaitAsync(Func<bool> predicate,string label)
    {
        var watch=Stopwatch.StartNew();while(!predicate()){if(watch.ElapsedMilliseconds>5000)throw new TimeoutException(label);await Task.Delay(20);}Check(true,label);
    }
    private static async Task FocusAsync(Window window,IntPtr target)
    {
        window.Activate();_=Native.SetForegroundWindow(target);
        if(Native.GetForegroundWindow()!=target){var current=Native.GetCurrentThreadId();var before=Native.GetWindowThreadProcessId(Native.GetForegroundWindow(),IntPtr.Zero);var attached=before!=current&&before!=0&&Native.AttachThreadInput(current,before,true);try{_=Native.SetActiveWindow(target);_=Native.SetForegroundWindow(target);}finally{if(attached)_=Native.AttachThreadInput(current,before,false);}if(Native.GetForegroundWindow()!=target)Native.SwitchToThisWindow(target,true);}
        await Task.Delay(40);Check(Native.GetForegroundWindow()==target,"Owned foreground fixture activated");
    }
    private static void SelectCurrent(object explorer,string name)
    {
        object? document=null,folder=null,item=null;
        try{document=((dynamic)explorer).Document;folder=((dynamic)document).Folder;item=((dynamic)folder).ParseName(name);((dynamic)document).SelectItem(item,0x1|0x4|0x8|0x10);}
        finally{foreach(var value in new[]{item,folder,document})if(value is not null&&Marshal.IsComObject(value))Marshal.ReleaseComObject(value);}
    }
    private static void WriteCubeStl(string path)
    {
        // Original unit cube: twelve outward-facing triangles, no generated
        // assets or output directories from another test project are required.
        var vertices=new[]{(0,0,0),(1,0,0),(1,1,0),(0,1,0),(0,0,1),(1,0,1),(1,1,1),(0,1,1)};
        var faces=new[]{(0,2,1),(0,3,2),(4,5,6),(4,6,7),(0,1,5),(0,5,4),(1,2,6),(1,6,5),(2,3,7),(2,7,6),(3,0,4),(3,4,7)};
        using var writer=new StreamWriter(path,false,new System.Text.UTF8Encoding(false));writer.WriteLine("solid original_lifetime_cube");
        foreach(var(a,b,c)in faces)
        {
            var p=vertices[a];var q=vertices[b];var r=vertices[c];
            var ux=q.Item1-p.Item1;var uy=q.Item2-p.Item2;var uz=q.Item3-p.Item3;
            var vx=r.Item1-p.Item1;var vy=r.Item2-p.Item2;var vz=r.Item3-p.Item3;
            writer.WriteLine($"facet normal {uy*vz-uz*vy} {uz*vx-ux*vz} {ux*vy-uy*vx}\nouter loop");
            foreach(var index in new[]{a,b,c}){var v=vertices[index];writer.WriteLine($"vertex {v.Item1} {v.Item2} {v.Item3}");}
            writer.WriteLine("endloop\nendfacet");
        }
        writer.WriteLine("endsolid original_lifetime_cube");
    }
    private static void Check(bool value,string label){if(!value)throw new Exception("FAIL: "+label);_checks++;Console.WriteLine("PASS: "+label);}
}
