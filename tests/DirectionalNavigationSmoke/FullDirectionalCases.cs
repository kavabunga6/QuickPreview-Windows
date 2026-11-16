using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using QuickPreview;

internal sealed record ScreenCell(string Path,int NativeIndex,Rect Bounds,int Row,int Column,string Group);

internal static class FullDirectionalCases
{
    private static readonly Type Service=typeof(App).Assembly.GetType("QuickPreview.ExplorerSelectionService",true)!;
    private static readonly Type Direction=typeof(App).Assembly.GetType("QuickPreview.NavigationDirection",true)!;
    private static int _checks,_moves;

    public static async Task RunAsync(App app,NativeExplorerFixture explorer,FolderViewProbe view,string directory)
    {
        MainWindow? preview=null;
        try
        {
            var initial=Path.Combine(directory,"00-direction-fixture.txt");explorer.Select(Path.GetFileName(initial));await explorer.ForegroundAsync();
            Check((string?)Call("TryGetSelectedPath")==initial,"Production origin captures fixture selection");
            preview=new MainWindow();app.MainWindow=preview;typeof(App).GetField("_previewWindow",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(app,preview);
            preview.ShowPreview(initial,activateWindow:false);await WaitTextAsync(preview,initial);
            preview.Width=640;preview.Height=420;preview.Left=100;preview.Top=100;preview.UpdateLayout();await Task.Delay(50);
            var previewHandle=new WindowInteropHelper(preview).Handle;
            Check(Native.GetWindowRect(previewHandle,out var baseline),"Native preview bounds recorded");
            var downStrides=new Dictionary<string,int>();
            foreach(var(label,mode,size,width,height,dateSort,grouped)in new[]
            {
                ("icons64-wide",1u,64,1050,650,false,false),("icons64-narrow",1u,64,700,650,false,false),
                ("icons32-narrow",1u,32,700,650,false,false),("icons128-wide",1u,128,1050,700,false,false),
                ("icons64-date-desc",1u,64,1050,650,true,false),
                ("small-icons-wide",2u,16,1050,620,false,false),("list-short",3u,16,1050,500,false,false),
                ("list-tall",3u,16,1050,700,false,false),("details-date-desc",4u,16,850,620,true,false),
                ("tiles",6u,48,1050,700,false,false),("content-date-desc",8u,48,850,700,true,false),
                ("grouped-date",1u,64,1050,700,true,true)
            })
            {
                await explorer.ForegroundAsync();_=SetWindowPos(explorer.Handle,IntPtr.Zero,60,60,width,height,0x0010);
                view.SetMode(mode,size);view.SetSort(dateSort?"prop:-System.DateModified;":"prop:System.ItemNameDisplay;");view.SetGrouping(grouped?"System.DateModified":null);
                await Task.Delay(160);
                var native=view.Snapshot();
                explorer.Select(Path.GetFileName(native.Items.OrderBy(item=>item.NativeIndex).First().Path));await Task.Delay(40);
                var cells=ReadCells(explorer.Handle,native);
                Check(cells.Length>=4,label+": native UIA cells available");
                Console.WriteLine($"LAYOUT {label}: actualMode={native.Mode},icon={native.IconSize},rows={string.Join(',',cells.Select(c=>c.Row).Distinct())},columns={string.Join(',',cells.Select(c=>c.Column).Distinct())}");
                if(grouped)
                {
                    Check(cells.Select(cell=>cell.Group).Distinct().Count()>1,"Actual date grouping has multiple native containers");
                    var firstElement=AccessibleProbe.Items(explorer.Handle).First();
                    if(firstElement.TryGetCurrentPattern(ValuePattern.Pattern,out var value))Console.WriteLine("GROUP UIA ValuePattern.Value="+((ValuePattern)value).Current.Value);
                }
                if(mode is 4 or 8)
                {
                    var ordered=cells.OrderBy(cell=>cell.Bounds.Top).ToArray();var source=ordered[2];
                    await MoveAsync(app,preview,explorer,source.Path,ordered[1].Path,"Up",label,baseline);
                    await MoveAsync(app,preview,explorer,source.Path,ordered[3].Path,"Down",label,baseline);
                    await MoveAsync(app,preview,explorer,source.Path,source.Path,"Left",label,baseline);
                    await MoveAsync(app,preview,explorer,source.Path,source.Path,"Right",label,baseline);
                }
                else
                {
                    if(cells.All(cell=>cell.Column>=0)&&cells.Select(cell=>cell.Column).Distinct().Count()==1)
                    {
                        var ordered=cells.OrderBy(cell=>cell.Bounds.Top).ToArray();var source=ordered[2];
                        await MoveAsync(app,preview,explorer,source.Path,ordered[1].Path,"Up",label,baseline);
                        await MoveAsync(app,preview,explorer,source.Path,ordered[3].Path,"Down",label,baseline);
                        await MoveAsync(app,preview,explorer,source.Path,source.Path,"Left",label,baseline);
                        await MoveAsync(app,preview,explorer,source.Path,source.Path,"Right",label,baseline);
                    }
                    else
                    {
                    var source=cells.Where(cell=>cell.Row>=1&&cell.Column>=0)
                        .OrderByDescending(cell=>cell.Column==1).ThenBy(cell=>cell.Row)
                        .FirstOrDefault(cell=>Adjacent(cells,cell,1,0)is not null&&Adjacent(cells,cell,0,1)is not null)
                        ??throw new Exception(label+": no two-dimensional source with independent Down/Right targets");
                    var down=Adjacent(cells,source,1,0)!;var right=Adjacent(cells,source,0,1)!;
                    Check(down.Path!=right.Path,label+": native Down and Right are different files");
                    downStrides[label]=down.NativeIndex-source.NativeIndex;
                    foreach(var(direction,dr,dc)in new[]{("Up",-1,0),("Down",1,0),("Left",0,-1),("Right",0,1)})
                    {
                        var start=source;var target=Adjacent(cells,start,dr,dc);
                        if(target is null)
                        {
                            start=cells.First(cell=>Adjacent(cells,cell,dr,dc)is not null);target=Adjacent(cells,start,dr,dc)!;
                        }
                        await MoveAsync(app,preview,explorer,start.Path,target.Path,direction,label,baseline);
                    }
                    if(label=="icons64-narrow")
                    {
                        var columns=cells.Select(cell=>cell.Column).Max()+1;
                        var lastVisible=cells.Where(cell=>cell.Column==0).OrderByDescending(cell=>cell.NativeIndex).First();
                        var beyond=native.Items.SingleOrDefault(item=>item.NativeIndex==lastVisible.NativeIndex+columns);
                        if(beyond is not null)await MoveAsync(app,preview,explorer,lastVisible.Path,beyond.Path,"Down",label+" offscreen next row",baseline);
                    }
                    if(grouped)
                    {
                        var column=cells.Where(cell=>cell.Column==0).OrderBy(cell=>cell.Bounds.Top).ToArray();
                        var cross=Enumerable.Range(0,column.Length-1).First(index=>column[index].Group!=column[index+1].Group);
                        await MoveAsync(app,preview,explorer,column[cross].Path,column[cross+1].Path,"Down","cross-date-group",baseline);
                        await MoveAsync(app,preview,explorer,column[cross+1].Path,column[cross].Path,"Up","cross-date-group",baseline);
                    }
                    }
                }
                var first=native.Items.OrderBy(item=>item.NativeIndex).First().Path;var last=native.Items.OrderBy(item=>item.NativeIndex).Last().Path;
                await MoveAsync(app,preview,explorer,first,first,"Up",label+" first boundary",baseline);
                await MoveAsync(app,preview,explorer,last,last,"Down",label+" last boundary",baseline);
                Console.WriteLine($"PASS MODE {label}: actualMode={native.Mode},icon={native.IconSize},cells={cells.Length},groups={cells.Select(c=>c.Group).Distinct().Count()}");
            }
            Check(downStrides["icons64-wide"]!=downStrides["icons64-narrow"],"Resizing Explorer changes grid vertical stride from actual layout");
            await QueueSafetyAsync(app,preview,explorer,view,directory);
            Console.WriteLine($"PASS: Directional grid/list/details/content/group navigation, {_moves} moves, {_checks} checks; preview focus/Z-order/geometry preserved.");
        }
        finally{preview?.HidePreview();Call("ForgetOriginWindow");}
    }

    private static ScreenCell? Adjacent(ScreenCell[] cells,ScreenCell source,int row,int column)=>cells.FirstOrDefault(cell=>
        cell.Group==source.Group&&cell.Row==source.Row+row&&cell.Column==source.Column+column);

    private static ScreenCell[] ReadCells(IntPtr explorer,ViewSnapshot native)
    {
        var output=new List<ScreenCell>();
        foreach(var element in AccessibleProbe.Items(explorer))
        {
            var data=element.Current;var matches=native.Items.Where(item=>Path.GetFileName(item.Path)==data.Name).ToArray();
            if(matches.Length!=1||data.BoundingRectangle.IsEmpty||data.BoundingRectangle.Width<=0||data.BoundingRectangle.Height<=0||
                !double.IsFinite(data.BoundingRectangle.X)||!double.IsFinite(data.BoundingRectangle.Y))continue;
            var row=-1;var column=-1;AutomationElement? container=null;
            if(element.TryGetCurrentPattern(GridItemPattern.Pattern,out var pattern))
            {
                var cell=((GridItemPattern)pattern).Current;row=cell.Row;column=cell.Column;container=cell.ContainingGrid;
            }
            container??=TreeWalker.ControlViewWalker.GetParent(element);
            var group=container is null?"none":string.Join(',',container.GetRuntimeId());
            output.Add(new ScreenCell(matches[0].Path,matches[0].NativeIndex,data.BoundingRectangle,row,column,group));
        }
        return output.ToArray();
    }

    private static async Task MoveAsync(App app,MainWindow preview,NativeExplorerFixture explorer,string from,string expected,string direction,string label,Native.Rect baseline)
    {
        explorer.Select(Path.GetFileName(from));await explorer.ForegroundAsync();
        typeof(App).GetMethod("SynchronizeExplorerSelection",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(app,null);
        await WaitTextAsync(preview,from);
        var handle=new WindowInteropHelper(preview).Handle;await FocusPreviewAsync(preview,handle);
        var hook=typeof(App).Assembly.GetType("QuickPreview.GlobalKeyboardHook",true)!;
        var allowed=(bool)hook.GetMethod("CanHandlePreviewNavigation",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,null)!;
        Check(preview.IsActive&&allowed,label+" "+direction+": app keyboard-focus guard accepts fixture arrows");
        typeof(MainWindow).GetMethod("Navigate",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(preview,new[]{Enum.Parse(Direction,direction)});
        Check(Native.GetForegroundWindow()==handle,label+" "+direction+": initiating key keeps preview foreground");
        await WaitNavigationAsync(preview);
        var watch=Stopwatch.StartNew();
        while((string?)Call("TryGetSelectedPathInOriginWindow")!=expected)
        {
            if(watch.Elapsed>TimeSpan.FromSeconds(7))
            {
                throw new Exception($"{label} {direction}: from={Path.GetFileName(from)} expected={Path.GetFileName(expected)} actual={Path.GetFileName((string?)Call("TryGetSelectedPathInOriginWindow"))}");
            }
            await Task.Delay(20);
        }
        await WaitTextAsync(preview,expected);
        Check(Native.GetForegroundWindow()==handle,label+" "+direction+": committed navigation does not steal focus");
        Check(Native.IsAbove(handle,explorer.Handle)&&(Native.GetWindowLong(handle,-20)&8)==0,label+" "+direction+": preview above Explorer and not topmost");
        Check(Native.GetWindowRect(handle,out var actual)&&actual.Equals(baseline),label+" "+direction+": preview geometry unchanged");
        _moves++;
        Console.WriteLine($"  PASS {label} {direction}: {Path.GetFileName(from)} → {Path.GetFileName(expected)}");
    }

    private static async Task FocusPreviewAsync(Window window,IntPtr target)
    {
        window.Activate();_=Native.SetForegroundWindow(target);
        if(Native.GetForegroundWindow()!=target)
        {
            var from=Native.GetCurrentThreadId();var fore=Native.GetWindowThreadProcessId(Native.GetForegroundWindow(),IntPtr.Zero);
            var attached=fore!=0&&fore!=from&&Native.AttachThreadInput(from,fore,true);
            try{_=Native.SetActiveWindow(target);_=Native.SetForegroundWindow(target);}finally{if(attached)_=Native.AttachThreadInput(from,fore,false);}
            if(Native.GetForegroundWindow()!=target)Native.SwitchToThisWindow(target,true);
        }
        await Task.Delay(35);Check(Native.GetForegroundWindow()==target,"Owned preview foreground fixture setup");
    }

    private static async Task WaitTextAsync(MainWindow preview,string path)
    {
        var body=File.ReadAllText(path);var watch=Stopwatch.StartNew();
        while(((TextBlock)preview.FindName("FileNameText")).Text!=Path.GetFileName(path)||((TextBox)preview.FindName("PreviewText")).Text!=body)
        {
            if(watch.Elapsed>TimeSpan.FromSeconds(8))throw new TimeoutException("Header/content after navigation: "+path);
            await Task.Delay(15);
        }
        Check(((TextBlock)preview.FindName("FileNameText")).Text==Path.GetFileName(path),"Preview header tracks actual Explorer selection");
    }
    private static async Task WaitNavigationAsync(MainWindow preview)
    {
        var task=typeof(MainWindow).GetField("_navigationTail",BindingFlags.Instance|BindingFlags.NonPublic)?.GetValue(preview)as Task;
        if(task is null)return;
        if(await Task.WhenAny(task,Task.Delay(7000))!=task)throw new TimeoutException("Serial Explorer navigation queue did not settle");
        await task;
    }
    private static async Task QueueSafetyAsync(App app,MainWindow preview,NativeExplorerFixture explorer,FolderViewProbe view,string directory)
    {
        await explorer.ForegroundAsync();_=SetWindowPos(explorer.Handle,IntPtr.Zero,60,60,1050,650,0x0010);
        view.SetGrouping(null);view.SetMode(1,64);view.SetSort("prop:System.ItemNameDisplay;");await Task.Delay(100);
        var cells=ReadCells(explorer.Handle,view.Snapshot());
        var source=cells.First(cell=>Adjacent(cells,cell,0,1)is{}right&&Adjacent(cells,right,1,0)is{}down&&Adjacent(cells,down,0,-1)is not null);
        var right=Adjacent(cells,source,0,1)!;var down=Adjacent(cells,right,1,0)!;var final=Adjacent(cells,down,0,-1)!;
        explorer.Select(Path.GetFileName(source.Path));await explorer.ForegroundAsync();
        typeof(App).GetMethod("SynchronizeExplorerSelection",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(app,null);
        await WaitTextAsync(preview,source.Path);
        var handle=new WindowInteropHelper(preview).Handle;await FocusPreviewAsync(preview,handle);
        foreach(var direction in new[]{"Right","Down","Left"})Navigate(preview,direction);
        await WaitNavigationAsync(preview);await WaitTextAsync(preview,final.Path);
        Check((string?)Call("TryGetSelectedPathInOriginWindow")==final.Path,"Queued directions apply in order against the latest Explorer anchor");
        Check(Native.GetForegroundWindow()==handle,"Queued burst retains preview foreground");

        var tail=typeof(MainWindow).GetField("_navigationTail",BindingFlags.Instance|BindingFlags.NonPublic)!;
        var gate=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        tail.SetValue(preview,gate.Task);Navigate(preview,"Right");var queued=(Task)tail.GetValue(preview)!;
        preview.HidePreview();
        var reopened=Path.Combine(directory,"01-direction-fixture.txt");explorer.Select(Path.GetFileName(reopened));await explorer.ForegroundAsync();
        Check((string?)Call("TryGetSelectedPath")==reopened,"Reopened fixture reacquires production origin generation");
        preview.ShowPreview(reopened,activateWindow:false);await WaitTextAsync(preview,reopened);await FocusPreviewAsync(preview,handle);
        gate.SetResult(true);await AwaitQueueTaskAsync(queued);
        Check((string?)Call("TryGetSelectedPathInOriginWindow")==reopened,"Hidden/reopened preview drops queued old-generation direction");
        await WaitTextAsync(preview,reopened);

        gate=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        tail.SetValue(preview,gate.Task);Navigate(preview,"Right");queued=(Task)tail.GetValue(preview)!;
        var other=new Window{Title="DirectionalNavigationSmoke owned focus fixture",Width=220,Height=120,Left=900,Top=700};
        try
        {
            other.Show();var otherHandle=new WindowInteropHelper(other).Handle;await FocusPreviewAsync(other,otherHandle);
            gate.SetResult(true);await AwaitQueueTaskAsync(queued);
            Check((string?)Call("TryGetSelectedPathInOriginWindow")==reopened,"Queued direction is dropped after foreground changes");
            Check(Native.GetForegroundWindow()==otherHandle,"Dropped direction never steals another window's foreground");
        }
        finally{other.Close();}
        Console.WriteLine("PASS QUEUE: ordered burst, hide/reopen generation invalidation, foreground-change cancellation.");
    }
    private static void Navigate(MainWindow preview,string direction)=>typeof(MainWindow).GetMethod("Navigate",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(preview,new[]{Enum.Parse(Direction,direction)});
    private static async Task AwaitQueueTaskAsync(Task task)
    {
        if(await Task.WhenAny(task,Task.Delay(7000))!=task)throw new TimeoutException("Gated navigation queue did not settle");await task;
    }
    private static object? Call(string name)=>Service.GetMethod(name,BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic)!.Invoke(null,null);
    private static void Check(bool condition,string label){if(!condition)throw new Exception("FAIL: "+label);_checks++;}
    [DllImport("user32.dll")]private static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
}
