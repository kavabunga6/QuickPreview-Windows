using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using QuickPreview;

internal static class Program
{
    private static int _exit;
    [STAThread]
    private static int Main(string[] args)
    {
        var app=new App();app.InitializeComponent();
        var startup=typeof(App).GetMethod("App_OnStartup",BindingFlags.Instance|BindingFlags.NonPublic)!;
        app.Startup-=(StartupEventHandler)Delegate.CreateDelegate(typeof(StartupEventHandler),app,startup);
        app.DispatcherUnhandledException+=(_,e)=>{Console.Error.WriteLine(e.Exception);_exit=1;e.Handled=true;};
        app.Dispatcher.BeginInvoke(new Action(async()=>
        {
            try
            {
                if(args.Any(value=>value is "--probe" or "--hold" or "--try-tab"))await ProbeAsync(args.Contains("--hold"),args.Contains("--try-tab"));
                else await LifetimeCases.RunAsync(app);
            }
            catch(Exception error){Console.Error.WriteLine(error);_exit=1;}
            finally{app.Shutdown();Dispatcher.ExitAllFrames();}
        }));
        Dispatcher.Run();return _exit;
    }

    private static async Task ProbeAsync(bool hold,bool tryTab)
    {
        var output=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../artifacts"));Directory.CreateDirectory(output);
        var root=Path.Combine(output,"fixtures",Guid.NewGuid().ToString("N"));
        var first=Path.Combine(root,"Origin-A");var child=Path.Combine(first,"Navigated-A");var second=Path.Combine(root,"Neighbor-B");
        Directory.CreateDirectory(child);Directory.CreateDirectory(second);
        foreach(var path in new[]{first,child,second})File.WriteAllText(Path.Combine(path,"Lifetime fixture.txt"),"Original lifetime fixture: "+Path.GetFileName(path));
        using var fixture=await NativeExplorerFixture.OpenAsync(first);
        using var probe=new ShellLifetimeProbe(fixture.Handle,root);
        probe.Snapshot("initial-origin");
        var explorer=typeof(NativeExplorerFixture).GetField("_explorer",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(fixture)!;
        ((dynamic)explorer).Navigate2(child,0);await Task.Delay(400);probe.Snapshot("folder-navigation-same-tab");
        var existing=probe.AllFrames();
        ((dynamic)explorer).Navigate2(second,tryTab?0x800:0);await Task.Delay(700);probe.Snapshot(tryTab?"Navigate2-navOpenInNewTab":"neighbor-folder-same-tab");
        probe.CloseExtraFrames(existing);
        if(hold)
        {
            Console.WriteLine($"FIXTURE_HWND=0x{fixture.Handle.ToInt64():X}");
            Console.WriteLine("ORIGIN_PATH="+first);Console.WriteLine("NEIGHBOR_PATH="+second);
            Console.WriteLine("FIXTURE_TITLE="+probe.Title());
            var stop=Path.Combine(output,"stop-"+Guid.NewGuid().ToString("N")+".flag");Console.WriteLine("STOP_FILE="+stop);
            Console.WriteLine("READY: hold up to120s for own-window tab actions; snapshots recorded on native tab/COM entry change.");
            var watch=Stopwatch.StartNew();var previous="";
            while(watch.ElapsedMilliseconds<120000&&!File.Exists(stop)&&ShellLifetimeProbe.IsWindow(fixture.Handle))
            {
                var current=probe.Fingerprint();
                if(current!=previous){probe.Snapshot("desktop-tab-change-"+watch.ElapsedMilliseconds);previous=current;}
                await Task.Delay(200);
            }
        }
        probe.Snapshot("before-cleanup");
        probe.CloseOwnedFrame();
        await Task.Delay(200);
        probe.Snapshot("after-owned-frame-close");
        probe.Save(Path.Combine(output,"native-probe.json"));
        Console.WriteLine("DONE: native lifetime probe; "+Path.Combine(output,"native-probe.json"));
    }
}

internal sealed class ShellLifetimeProbe:IDisposable
{
    private readonly IntPtr _frame;
    private readonly string _fixtureRoot;
    private readonly object _shell,_windows;
    private readonly Dictionary<IntPtr,string> _known=new();
    private readonly List<object> _snapshots=new(),_events=new();
    private readonly WinEventProc _callback;
    private readonly IntPtr _hook;
    private readonly Stopwatch _clock=Stopwatch.StartNew();
    public ShellLifetimeProbe(IntPtr frame,string fixtureRoot)
    {
        _frame=frame;_fixtureRoot=fixtureRoot;
        _shell=Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;_windows=((dynamic)_shell).Windows();
        _callback=OnWinEvent;var process=GetWindowThreadProcessId(frame,out var pid);
        _hook=SetWinEventHook(0x8001,0x8003,IntPtr.Zero,_callback,pid,0,0);
    }
    public void Snapshot(string label)
    {
        var entries=Entries(_frame);var descendants=Descendants(_frame);
        foreach(var window in descendants)_known[new IntPtr(window.Hwnd)]=window.Class;
        if(IsWindow(_frame))_known[_frame]=Class(_frame);
        var row=new{Label=label,Milliseconds=_clock.ElapsedMilliseconds,Frame=_frame.ToInt64(),FrameAlive=IsWindow(_frame),FrameTitle=Title(),Entries=entries,Descendants=descendants};
        _snapshots.Add(row);Console.WriteLine(JsonSerializer.Serialize(row));
    }
    public string Title(){var value=new StringBuilder(512);_=GetWindowText(_frame,value,value.Capacity);return value.ToString();}
    public string Fingerprint()=>JsonSerializer.Serialize(new{Alive=IsWindow(_frame),Entries=Entries(_frame),Tabs=Descendants(_frame).Where(w=>w.Class=="ShellTabWindowClass")});
    public HashSet<long> AllFrames()
    {
        var handles=new HashSet<long>();
        foreach(var entry in RawEntries())try{handles.Add(Convert.ToInt64(((dynamic)entry).HWND));}finally{Release(entry);}
        return handles;
    }
    public void CloseExtraFrames(HashSet<long> preexisting)
    {
        foreach(var entry in RawEntries())
        {
            try{var handle=Convert.ToInt64(((dynamic)entry).HWND);if(!preexisting.Contains(handle)&&Location(entry).StartsWith(_fixtureRoot,StringComparison.OrdinalIgnoreCase))((dynamic)entry).Quit();}
            finally{Release(entry);}
        }
    }
    public void CloseOwnedFrame()
    {
        if(!IsWindow(_frame))return;
        var entries=Entries(_frame);
        if(entries.Length>0&&entries.Any(entry=>!entry.Location.StartsWith(_fixtureRoot,StringComparison.OrdinalIgnoreCase)))
        {Console.WriteLine("Leaving fixture window open: an unrelated path was introduced during desktop actions.");return;}
        _=SendMessageTimeout(_frame,0x10,IntPtr.Zero,IntPtr.Zero,2,2000,out _);
    }
    private IEnumerable<object> RawEntries()
    {
        var count=(int)((dynamic)_windows).Count;
        for(var index=0;index<count;index++){object? item=null;try{item=((dynamic)_windows).Item(index);}catch(COMException){}if(item is not null)yield return item;}
    }
    private Entry[] Entries(IntPtr frame)
    {
        var result=new List<Entry>();
        foreach(var item in RawEntries())
        {
            object? browser=null,view=null;
            try
            {
                var handle=new IntPtr(Convert.ToInt64(((dynamic)item).HWND));if(handle!=frame)continue;
                var sid=new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");var iid=typeof(IShellBrowserNative).GUID;
                var browserWindow=IntPtr.Zero;var viewWindow=IntPtr.Zero;var browserHr=((IServiceProviderNative)item).QueryService(ref sid,ref iid,out browser);
                var viewHr=int.MinValue;
                if(browserHr>=0&&browser is not null){_=((IShellBrowserNative)browser).GetWindow(out browserWindow);viewHr=((IShellBrowserNative)browser).QueryActiveShellView(out view);if(viewHr>=0&&view is not null)_=((IShellViewNative)view).GetWindow(out viewWindow);}
                result.Add(new Entry(handle.ToInt64(),Identity(item),Location(item),browser is null?0:Identity(browser),browserWindow.ToInt64(),Class(browserWindow),IsWindowVisible(browserWindow),viewWindow.ToInt64(),Class(viewWindow),IsWindowVisible(viewWindow),Parents(viewWindow),browserHr,viewHr));
            }
            catch(Exception error)when(error is COMException or InvalidCastException){Console.WriteLine("ENTRY ERROR "+error.Message);}
            finally{Release(view);Release(browser);Release(item);}
        }
        return result.ToArray();
    }
    private static string Location(object explorer)
    {
        object? document=null,folder=null,self=null;
        try{document=((dynamic)explorer).Document;folder=((dynamic)document).Folder;self=((dynamic)folder).Self;return (string)((dynamic)self).Path;}
        catch(COMException){return "<disconnected>";}
        finally{Release(self);Release(folder);Release(document);}
    }
    private static long Identity(object value){var pointer=Marshal.GetIUnknownForObject(value);try{return pointer.ToInt64();}finally{Marshal.Release(pointer);}}
    private static WindowInfo[] Parents(IntPtr window)
    {
        var result=new List<WindowInfo>();var seen=new HashSet<IntPtr>();
        for(var current=window;current!=IntPtr.Zero&&seen.Add(current);current=GetParent(current))result.Add(new WindowInfo(current.ToInt64(),Class(current),IsWindowVisible(current)));
        return result.ToArray();
    }
    private static WindowInfo[] Descendants(IntPtr frame)
    {
        var result=new List<WindowInfo>();
        _=EnumChildWindows(frame,(window,_)=>{var name=Class(window);if(name is "ShellTabWindowClass" or "SHELLDLL_DefView" or "DirectUIHWND")result.Add(new WindowInfo(window.ToInt64(),name,IsWindowVisible(window)));return true;},IntPtr.Zero);
        return result.ToArray();
    }
    private void OnWinEvent(IntPtr hook,uint type,IntPtr window,int objectId,int childId,uint thread,uint time)
    {
        if(objectId!=0||childId!=0||!_known.TryGetValue(window,out var name))return;
        var entry=new{Milliseconds=_clock.ElapsedMilliseconds,Event=type,Hwnd=window.ToInt64(),Class=name,AliveAtCallback=IsWindow(window)};
        _events.Add(entry);Console.WriteLine("NATIVE EVENT "+JsonSerializer.Serialize(entry));
    }
    public void Save(string file)=>File.WriteAllText(file,JsonSerializer.Serialize(new{OsVersion=Environment.OSVersion.VersionString,Snapshots=_snapshots,Events=_events},new JsonSerializerOptions{WriteIndented=true}));
    public void Dispose(){if(_hook!=IntPtr.Zero)_=UnhookWinEvent(_hook);Release(_windows);Release(_shell);}
    private static void Release(object? value){if(value is not null&&Marshal.IsComObject(value))Marshal.ReleaseComObject(value);}
    private static string Class(IntPtr window){var value=new StringBuilder(256);_=GetClassName(window,value,value.Capacity);return value.ToString();}
    private sealed record WindowInfo(long Hwnd,string Class,bool Visible);
    private sealed record Entry(long Frame,long CanonicalIUnknown,string Location,long BrowserIUnknown,long BrowserWindow,string BrowserClass,bool BrowserVisible,long ViewWindow,string ViewClass,bool ViewVisible,WindowInfo[] ViewParents,int BrowserResult,int ViewResult);
    [ComImport,Guid("6D5140C1-7436-11CE-8034-00AA006009FA"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]private interface IServiceProviderNative{[PreserveSig]int QueryService(ref Guid service,ref Guid iid,[MarshalAs(UnmanagedType.Interface)]out object? result);}
    [ComImport,Guid("000214E2-0000-0000-C000-000000000046"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]private interface IShellBrowserNative
    {
        [PreserveSig]int GetWindow(out IntPtr window);void ContextSensitiveHelp();void InsertMenusSB();void SetMenuSB();void RemoveMenusSB();void SetStatusTextSB();void EnableModelessSB();void TranslateAcceleratorSB();void BrowseObject();void GetViewStateStream();void GetControlWindow();void SendControlMsg();[PreserveSig]int QueryActiveShellView([MarshalAs(UnmanagedType.Interface)]out object? view);
    }
    [ComImport,Guid("000214E3-0000-0000-C000-000000000046"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]private interface IShellViewNative{[PreserveSig]int GetWindow(out IntPtr window);void ContextSensitiveHelp();}
    private delegate bool EnumWindowProc(IntPtr window,IntPtr parameter);
    private delegate void WinEventProc(IntPtr hook,uint type,IntPtr window,int objectId,int childId,uint thread,uint time);
    [DllImport("user32.dll")]private static extern bool EnumChildWindows(IntPtr parent,EnumWindowProc callback,IntPtr parameter);
    [DllImport("user32.dll")]private static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll")]public static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")]private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetClassName(IntPtr window,StringBuilder text,int length);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetWindowText(IntPtr window,StringBuilder text,int length);
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
    [DllImport("user32.dll")]private static extern IntPtr SetWinEventHook(uint first,uint last,IntPtr module,WinEventProc callback,uint process,uint thread,uint flags);
    [DllImport("user32.dll")]private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")]private static extern IntPtr SendMessageTimeout(IntPtr window,uint message,IntPtr wParam,IntPtr lParam,uint flags,uint timeout,out IntPtr result);
}
