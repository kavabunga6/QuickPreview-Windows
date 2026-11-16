using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Automation;
using System.Windows.Interop;
using QuickPreview;

internal static class Program
{
    private static int _exitCode;
    [STAThread]
    private static int Main(string[] args)
    {
        var app = new App(); app.InitializeComponent();
        var startup = typeof(App).GetMethod("App_OnStartup", BindingFlags.Instance | BindingFlags.NonPublic)!;
        app.Startup -= (StartupEventHandler)Delegate.CreateDelegate(typeof(StartupEventHandler), app, startup);
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try { await RunAsync(app, args.Contains("--probe-native"), args.Contains("--probe-grouped"),args.Contains("--probe-accelerator")); }
            catch (Exception error) { Console.Error.WriteLine(error); _exitCode = 1; }
            finally { app.Shutdown(); Dispatcher.ExitAllFrames(); }
        }));
        Dispatcher.Run(); return _exitCode;
    }

    private static async Task RunAsync(App app, bool probeOnly, bool groupedProbe,bool acceleratorProbe)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "direction-fixtures", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        for (var index = 0; index < 40; index++)
        {
            var path = Path.Combine(directory, $"{index:00}-direction-fixture{(index<20?".txt":".log")}");
            File.WriteAllText(path, "Original directional fixture " + index);
            File.SetLastWriteTime(path, DateTime.Today.AddDays(-(index%8)).AddMinutes(index*3));
        }
        using var explorer = await NativeExplorerFixture.OpenAsync(directory);
        using var view = new FolderViewProbe(explorer);
        view.SetGrouping(null);
        if(acceleratorProbe)
        {
            _=SetWindowPos(explorer.Handle,IntPtr.Zero,60,60,900,650,0x0010);view.SetMode(1,64);await Task.Delay(180);
            var other=new Window{Title="DirectionalNavigationSmoke accelerator focus fixture",Width=400,Height=250};other.Show();
            var otherHandle=new WindowInteropHelper(other).Handle;
            foreach(var(name,key)in new[]{("Down",0x28),("Right",0x27),("Up",0x26),("Left",0x25)})
            {
                explorer.Select("03-direction-fixture.txt");await explorer.ForegroundAsync();
                var info=new GuiInfo{Size=Marshal.SizeOf<GuiInfo>()};_=GetGUIThreadInfo(Native.GetWindowThreadProcessId(explorer.Handle,IntPtr.Zero),ref info);
                var input=info.Focus;
                await FocusOwnAsync(other,otherHandle);
                var before=view.FocusedIndex();var fg=Native.GetForegroundWindow();
                var result=view.TranslateArrow(key);await Task.Delay(50);var after=view.FocusedIndex();var snapshot=view.Snapshot();
                Console.WriteLine($"TRANSLATE {name}: down=0x{result.Down:X8} up=0x{result.Up:X8} index{before}->{after} file={Path.GetFileName(snapshot.Items.Single(i=>i.NativeIndex==after).Path)} foreground=0x{fg.ToInt64():X}->0x{Native.GetForegroundWindow().ToInt64():X} ownedWpf=0x{otherHandle.ToInt64():X}");
                var childResult=view.TranslateArrow(key,input);await Task.Delay(40);
                Console.WriteLine($"TRANSLATE child0x{input.ToInt64():X} {name}: down=0x{childResult.Down:X8},up=0x{childResult.Up:X8},index={view.FocusedIndex()},foreground=0x{Native.GetForegroundWindow().ToInt64():X}");
                var keyParameter=new IntPtr(1|(MapVirtualKey((uint)key,0)<<16)|0x01000000);
                var sentDown=SendMessageTimeout(input,0x100,new IntPtr(key),keyParameter,2,1000,out var downValue);
                var sentUp=SendMessageTimeout(input,0x101,new IntPtr(key),new IntPtr(keyParameter.ToInt64()|0xC0000000),2,1000,out var upValue);
                await Task.Delay(60);var sentIndex=view.FocusedIndex();
                Console.WriteLine($"SEND child {name}: API={sentDown}/{sentUp},results={downValue}/{upValue},index={before}->{sentIndex},file={Path.GetFileName(view.Snapshot().Items.Single(i=>i.NativeIndex==sentIndex).Path)},foreground=0x{fg.ToInt64():X}->0x{Native.GetForegroundWindow().ToInt64():X}");
            }
            explorer.Select("03-direction-fixture.txt");await explorer.ForegroundAsync();await FocusOwnAsync(other,otherHandle);
            AccessibleProbe.ProbeSelectWithoutFocus(explorer.Handle);await Task.Delay(80);
            Console.WriteLine($"MSAA settledforeground=0x{Native.GetForegroundWindow().ToInt64():X},ownedWpf=0x{otherHandle.ToInt64():X},focusedindex={view.FocusedIndex()}");
            other.Close();return;
        }
        if(groupedProbe)
        {
            _=SetWindowPos(explorer.Handle,IntPtr.Zero,60,60,1050,650,0x0010);
            view.SetMode(1,64);view.SetSort("prop:-System.DateModified;");view.SetGrouping("System.DateModified");await Task.Delay(400);
            foreach(var name in new[]{"00-direction-fixture.txt","39-direction-fixture.log"})
            {
                explorer.Select(name);await Task.Delay(80);
                var snapshot=view.Snapshot();
                Console.WriteLine("GROUP PROBE selected="+name+" nativeFocusedIndex="+view.FocusedIndex());
                foreach(var item in AccessibleProbe.Items(explorer.Handle))
                {
                    var data=item.Current;
                    if(!item.TryGetCurrentPattern(GridItemPattern.Pattern,out var p))continue;
                    var cell=((GridItemPattern)p).Current;
                    var grid=cell.ContainingGrid?.GetCurrentPattern(GridPattern.Pattern) as GridPattern;
                    var match=int.TryParse(data.AutomationId,out var index)&&snapshot.Items.Any(i=>i.NativeIndex==index&&Path.GetFileName(i.Path)==data.Name);
                    var parent=TreeWalker.ControlViewWalker.GetParent(item);
                    Console.WriteLine($" {data.Name} id={data.AutomationId} maps={match} row={cell.Row} col={cell.Column} grid={(grid is null?"NULL":grid.Current.RowCount+"x"+grid.Current.ColumnCount)} parent={parent?.Current.Name}:{parent?.Current.ControlType.ProgrammaticName} container={(cell.ContainingGrid is null?"NULL":string.Join(',',cell.ContainingGrid.GetRuntimeId()))} bounds={data.BoundingRectangle} offscreen={data.IsOffscreen}");
                }
            }
            return;
        }
        if(!probeOnly)
        {
            await FullDirectionalCases.RunAsync(app,explorer,view,directory);
            return;
        }
        foreach (var (label,mode,size,width,height) in new[]
        {
            ("icons64-narrow",1u,64,700,620),("icons128-wide",1u,128,1050,700),
            ("small-icons",2u,16,850,620),("list",3u,16,1050,500),("details",4u,16,850,620),
            ("tiles",6u,48,1050,700),("content",8u,48,850,700)
        })
        {
            _ = SetWindowPos(explorer.Handle,IntPtr.Zero,60,60,width,height,0x0010);
            view.SetMode(mode,size); await Task.Delay(180);
            explorer.Select("03-direction-fixture.txt"); await Task.Delay(40);
            var snapshot=view.Snapshot();
            Console.WriteLine($"PROBE {label}: requestedmode={mode}, actual={snapshot.Mode}, icon={snapshot.IconSize}; spacingHR=0x{snapshot.SpacingResult:X8} spacing=({snapshot.Spacing.X},{snapshot.Spacing.Y}); items={snapshot.Items.Length}; uniqueX={snapshot.Items.Select(i=>i.X).Distinct().Count()},uniqueY={snapshot.Items.Select(i=>i.Y).Distinct().Count()}");
            foreach(var item in snapshot.Items.Take(8))Console.WriteLine($"  {Path.GetFileName(item.Path)} ({item.X},{item.Y}) HR=0x{item.Result:X8}");
            AccessibleProbe.Dump(explorer.Handle,snapshot);
            if(label=="icons64-narrow")
            {
                view.SetSort("prop:-System.DateModified;");await Task.Delay(150);
                explorer.Select("03-direction-fixture.txt");await Task.Delay(30);
                Console.WriteLine("PROBE DATE-DESC AutomationId mapping:");
                AccessibleProbe.Dump(explorer.Handle,view.Snapshot());
                view.SetSort("prop:System.ItemNameDisplay;");await Task.Delay(120);
            }
            if(label is "icons64-narrow" or "list")
            {
                var target=AccessibleProbe.Items(explorer.Handle).First(item=>item.Current.AutomationId=="4");
                var other=new Window{Title="DirectionalNavigationSmoke own foreground",Width=350,Height=200};other.Show();
                var otherHandle=new WindowInteropHelper(other).Handle;
                if(Native.GetForegroundWindow()!=otherHandle)Native.SwitchToThisWindow(otherHandle,true);
                await Task.Delay(50);
                var before=Native.GetForegroundWindow();
                ((SelectionItemPattern)target.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
                await Task.Delay(80);
                Console.WriteLine($"UIA SELECT FOCUS {label}: before=0x{before.ToInt64():X},after=0x{Native.GetForegroundWindow().ToInt64():X},ownedWpf=0x{otherHandle.ToInt64():X},selected={target.Current.Name},focusedNativeIndex={view.FocusedIndex()}");
                other.Close();await explorer.ForegroundAsync();
            }
        }
        Console.WriteLine("PASS: Native folder-position probe.");
    }
    private static async Task FocusOwnAsync(Window window,IntPtr target)
    {
        window.Activate();_=Native.SetForegroundWindow(target);
        if(Native.GetForegroundWindow()!=target)
        {
            var from=Native.GetCurrentThreadId();var fore=Native.GetWindowThreadProcessId(Native.GetForegroundWindow(),IntPtr.Zero);
            var attached=fore!=0&&fore!=from&&Native.AttachThreadInput(from,fore,true);
            try{_=Native.SetActiveWindow(target);_=Native.SetForegroundWindow(target);}
            finally{if(attached)_=Native.AttachThreadInput(from,fore,false);}
            if(Native.GetForegroundWindow()!=target)Native.SwitchToThisWindow(target,true);
        }
        await Task.Delay(50);
        if(Native.GetForegroundWindow()!=target)throw new Exception("OwnWPFforegroundsetupfailed.");
    }
    [DllImport("user32.dll")]private static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
    [StructLayout(LayoutKind.Sequential)]private struct GuiInfo{public int Size;public uint Flags;public IntPtr Active,Focus,Capture,MenuOwner,MoveSize,Caret;public int Left,Top,Right,Bottom;}
    [DllImport("user32.dll")]private static extern bool GetGUIThreadInfo(uint thread,ref GuiInfo info);
    [DllImport("user32.dll")]private static extern uint MapVirtualKey(uint key,uint type);
    [DllImport("user32.dll",SetLastError=true)]private static extern IntPtr SendMessageTimeout(IntPtr hwnd,uint message,IntPtr wParam,IntPtr lParam,uint flags,uint timeout,out UIntPtr result);
}
