using System.ComponentModel;
using System.Runtime.InteropServices;

// Two real native child HWNDs model the lifetime contract on Windows10, where
// Explorer exposes one ShellTab host but does not support the Windows11 tab UI.
internal sealed class NativeTabFixture:IDisposable
{
    private static readonly WindowProcedure Procedure=DefWindowProc;
    private static readonly HashSet<string> Registered=new();
    private readonly List<IntPtr> _children=new();
    public IntPtr Frame{get;}
    public IntPtr First{get;}
    public IntPtr Second{get;}

    public NativeTabFixture()
    {
        Register("QuickPreviewLifetimeFrame");Register("ShellTabWindowClass");
        Frame=CreateWindowEx(0,"QuickPreviewLifetimeFrame","QuickPreview owned tab-lifetime protocol fixture",0x00CF0000,900,700,240,100,IntPtr.Zero,IntPtr.Zero,GetModuleHandle(null),IntPtr.Zero);
        if(Frame==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());
        First=AddTab();Second=AddTab();Select(First);_=ShowWindow(Frame,4);
    }
    public IntPtr AddTab()
    {
        var window=CreateWindowEx(0,"ShellTabWindowClass","Owned native tab host",0x50000000,0,0,200,60,Frame,IntPtr.Zero,GetModuleHandle(null),IntPtr.Zero);
        if(window==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());_children.Add(window);return window;
    }
    public void Select(IntPtr tab){foreach(var child in _children)if(IsWindow(child))_=ShowWindow(child,child==tab?5:0);_=SetWindowPos(tab,IntPtr.Zero,0,0,0,0,0x0013);}
    public void CloseTab(IntPtr tab){if(!_children.Contains(tab))throw new InvalidOperationException("Only an owned tab host may be closed");if(IsWindow(tab)&&!DestroyWindow(tab))throw new Win32Exception(Marshal.GetLastWin32Error());}
    public void Dispose(){if(IsWindow(Frame))_=DestroyWindow(Frame);}
    public static string ClassName(IntPtr window){var value=new System.Text.StringBuilder(256);_=GetClassName(window,value,value.Capacity);return value.ToString();}
    public static (uint Thread,uint Process) Stamp(IntPtr window){var thread=GetWindowThreadProcessId(window,out var process);return(thread,process);}
    private static void Register(string name)
    {
        if(Registered.Contains(name))return;
        var value=new WindowClass{Size=(uint)Marshal.SizeOf<WindowClass>(),Procedure=Procedure,Instance=GetModuleHandle(null),Name=name};
        if(RegisterClassEx(ref value)==0)throw new Win32Exception(Marshal.GetLastWin32Error(),"Could not register owned lifetime-fixture window class");Registered.Add(name);
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct WindowClass{public uint Size,Style;public WindowProcedure Procedure;public int ClassExtra,WindowExtra;public IntPtr Instance,Icon,Cursor,Background;public string? Menu;public string Name;public IntPtr SmallIcon;}
    private delegate IntPtr WindowProcedure(IntPtr window,uint message,IntPtr wParam,IntPtr lParam);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern ushort RegisterClassEx(ref WindowClass value);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr CreateWindowEx(uint extended,string className,string title,uint style,int x,int y,int width,int height,IntPtr parent,IntPtr menu,IntPtr instance,IntPtr parameter);
    [DllImport("user32.dll")]private static extern IntPtr DefWindowProc(IntPtr window,uint message,IntPtr wParam,IntPtr lParam);
    [DllImport("user32.dll",SetLastError=true)]private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")]public static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")]private static extern bool ShowWindow(IntPtr window,int command);
    [DllImport("user32.dll")]private static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetClassName(IntPtr window,System.Text.StringBuilder text,int size);
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]private static extern IntPtr GetModuleHandle(string? module);
}
