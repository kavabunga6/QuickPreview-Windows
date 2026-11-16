using System.Runtime.InteropServices;
using System.Windows.Automation;
using Accessibility;

internal static class AccessibleProbe
{
    public static void ProbeSelectWithoutFocus(IntPtr explorer)
    {
        var info=new GuiThreadInfo{Size=Marshal.SizeOf<GuiThreadInfo>()};_=GetGUIThreadInfo(Native.GetWindowThreadProcessId(explorer,IntPtr.Zero),ref info);
        IAccessible? root=null;object? parentObject=null;
        try
        {
            var iid=new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");Marshal.ThrowExceptionForHR(AccessibleObjectFromWindow(info.Focus,0xFFFFFFFC,ref iid,out root));
            var focused=root!.accFocus as IAccessible??root;
            parentObject=focused.accParent;
            var parent=(IAccessible)parentObject;
            Console.WriteLine($"MSAA parentName={parent.get_accName(0)},children={parent.accChildCount}");
            var children=new object[parent.accChildCount];Marshal.ThrowExceptionForHR(AccessibleChildren(parent,0,children.Length,children,out var read));
            for(var index=0;index<read;index++)
            {
                var child=children[index];var accessible=child as IAccessible??parent;var id=child is int number?number:0;
                var name=accessible.get_accName(id);
                if(name!="04-direction-fixture.txt")continue;
                var before=Native.GetForegroundWindow();
                Console.WriteLine($"MSAA target={name},childId={id},value={accessible.get_accValue(id)},before=0x{before.ToInt64():X}");
                accessible.accSelect(2,id);
                Console.WriteLine($"MSAA accSelect TAKESELECTION only after=0x{Native.GetForegroundWindow().ToInt64():X}");
                return;
            }
            Console.WriteLine("MSAA target04 notenumerated.");
        }
        catch(Exception error){Console.WriteLine($"MSAAselect error {error.GetType().Name} HR=0x{error.HResult:X8} {error.Message}");}
        finally{if(parentObject is not null&&Marshal.IsComObject(parentObject))Marshal.ReleaseComObject(parentObject);if(root is not null&&Marshal.IsComObject(root))Marshal.ReleaseComObject(root);}
    }
    public static void Dump(IntPtr explorer, ViewSnapshot snapshot)
    {
        var root = AutomationElement.FromHandle(explorer);
        var children = root.FindAll(TreeScope.Descendants, new OrCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem)));
        Console.WriteLine("UIA items=" + children.Count);
        var matches = 0;
        foreach (AutomationElement item in children)
        {
            var data=item.Current;
            if(int.TryParse(data.AutomationId,out var index)&&snapshot.Items.Any(entry=>entry.NativeIndex==index&&System.IO.Path.GetFileName(entry.Path)==data.Name))matches++;
        }
        Console.WriteLine($"UIA AutomationId→IFolderView2.GetItem(index) name matches: {matches}/{children.Count}");
        foreach (AutomationElement item in children.Cast<AutomationElement>().Take(4))
        {
            var data = item.Current;
            Console.WriteLine($"  UIA {data.Name} id={data.AutomationId} bounds={data.BoundingRectangle} offscreen={data.IsOffscreen} patterns={string.Join(',', item.GetSupportedPatterns().Select(p => p.ProgrammaticName))}");
            if(item.TryGetCurrentPattern(GridItemPattern.Pattern,out var gridItemObject))
            {
                var gridItem=(GridItemPattern)gridItemObject;
                var cell=gridItem.Current;
                Console.WriteLine($"    GridItem row={cell.Row},col={cell.Column},rowSpan={cell.RowSpan},colSpan={cell.ColumnSpan}");
                if(cell.ContainingGrid.TryGetCurrentPattern(GridPattern.Pattern,out var gridObject))
                {
                    var grid=(GridPattern)gridObject;
                    Console.WriteLine($"    ContainingGrid rows={grid.Current.RowCount},cols={grid.Current.ColumnCount}");
                    if(matches==children.Count)
                    {
                        try
                        {
                            var last=grid.GetItem(grid.Current.RowCount-1,0);
                            Console.WriteLine($"    GetItem(lastRow,0) name={last.Current.Name} id={last.Current.AutomationId} bounds={last.Current.BoundingRectangle} offscreen={last.Current.IsOffscreen}");
                        }
                        catch(Exception error){Console.WriteLine($"    GetItem(lastRow,0) error {error.GetType().Name} HR=0x{error.HResult:X8}");}
                    }
                }
            }
        }
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        var thread = Native.GetWindowThreadProcessId(explorer, IntPtr.Zero);
        _ = GetGUIThreadInfo(thread, ref info);
        Console.WriteLine($"GUI focus=0x{info.Focus.ToInt64():X}");
        foreach (var (label, hwnd) in new[] { ("focused HWND",info.Focus), ("Explorer HWND",explorer) })
        {
            IAccessible? accessible = null;
            try
            {
                var iid = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");
                var result = AccessibleObjectFromWindow(hwnd, 0xFFFFFFFC, ref iid, out accessible);
                Console.WriteLine($"MSAA {label}: HRESULT=0x{result:X8}");
                if (result < 0 || accessible is null) continue;
                object? focus = accessible.accFocus;
                var start = focus is int child ? child : 0;
                var focused = focus as IAccessible ?? accessible;
                Console.WriteLine($"  FocusVariant={focus?.GetType().FullName ?? "null"} child={start}, name={focused.get_accName(start)}");
                foreach (var (name, direction) in new[] { ("Up",1),("Down",2),("Left",3),("Right",4),("Next",5),("Previous",6) })
                {
                    object? target = null;
                    try
                    {
                        target = focused.accNavigate(direction,start);
                        var targetName = target is IAccessible acc ? acc.get_accName(0) : target is int id ? focused.get_accName(id) : "<null>";
                        Console.WriteLine($"  accNavigate {name}: {target?.GetType().FullName ?? "null"} -> {targetName}");
                    }
                    catch (Exception error) { Console.WriteLine($"  accNavigate {name}: {error.GetType().Name} HR=0x{error.HResult:X8} {error.Message}"); }
                    finally { if (target is not null && Marshal.IsComObject(target)) Marshal.ReleaseComObject(target); }
                }
            }
            catch (Exception error) { Console.WriteLine($"MSAA {label} error: {error.GetType().Name} HR=0x{error.HResult:X8} {error.Message}"); }
            finally { if (accessible is not null && Marshal.IsComObject(accessible)) Marshal.ReleaseComObject(accessible); }
        }
    }

    public static AutomationElement[] Items(IntPtr explorer)=>AutomationElement.FromHandle(explorer).FindAll(TreeScope.Descendants,new OrCondition(
        new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.ListItem),new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.DataItem))).Cast<AutomationElement>().ToArray();

    [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo
    {
        public int Size; public uint Flags;
        public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public int Left, Top, Right, Bottom;
    }
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("oleacc.dll")] private static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint objectId, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IAccessible? accessible);
    [DllImport("oleacc.dll")]private static extern int AccessibleChildren(IAccessible container,int start,int count,[Out,MarshalAs(UnmanagedType.LPArray,ArraySubType=UnmanagedType.Struct,SizeParamIndex=2)]object[] children,out int obtained);
}
