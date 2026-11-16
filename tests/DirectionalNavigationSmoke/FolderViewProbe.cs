using System.Reflection;
using System.Runtime.InteropServices;

internal sealed record ItemPosition(int NativeIndex, string Path, int X, int Y, int Result);
internal sealed record ViewSnapshot(uint Mode, int IconSize, PointNative Spacing, int SpacingResult, ItemPosition[] Items);

internal sealed class FolderViewProbe : IDisposable
{
    private object? _browser, _shellView;
    private readonly object _document;
    private readonly IFolderView2Native _view;

    public FolderViewProbe(NativeExplorerFixture fixture)
    {
        var explorer = typeof(NativeExplorerFixture).GetField("_explorer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture)!;
        _document = typeof(NativeExplorerFixture).GetField("_document", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture)!;
        var service = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
        var browserId = typeof(IShellBrowserNative).GUID;
        Marshal.ThrowExceptionForHR(((IServiceProviderNative)explorer).QueryService(ref service, ref browserId, out _browser));
        Marshal.ThrowExceptionForHR(((IShellBrowserNative)_browser!).QueryActiveShellView(out _shellView));
        _view = (IFolderView2Native)_shellView!;
    }

    public void SetMode(uint mode, int iconSize) => Marshal.ThrowExceptionForHR(_view.SetViewModeAndIconSize(mode, iconSize));
    public void SetSort(string sort) => ((dynamic)_document).SortColumns = sort;
    public int FocusedIndex() { Marshal.ThrowExceptionForHR(_view.GetFocusedItem(out var index)); return index; }
    public (int Down,int Up) TranslateArrow(int key,IntPtr? target=null)
    {
        var shell=(IShellViewAccelerator)_shellView!;Marshal.ThrowExceptionForHR(shell.GetWindow(out var window));
        var message=new NativeMessage{Window=target??window,Message=0x100,WParam=new IntPtr(key),LParam=new IntPtr(1|(MapVirtualKey((uint)key,0)<<16)|0x01000000)};
        var down=shell.TranslateAccelerator(ref message);
        message.Message=0x101;message.LParam=new IntPtr(unchecked((long)message.LParam.ToInt64()|0xC0000000));
        return(down,shell.TranslateAccelerator(ref message));
    }
    public void SetGrouping(string? canonicalName)
    {
        var key = new PropertyKey();
        if (canonicalName is not null) Marshal.ThrowExceptionForHR(PSGetPropertyKeyFromName(canonicalName, out key));
        Marshal.ThrowExceptionForHR(_view.SetCurrentFolderFlags(0x00100000, canonicalName is null ? 0x00100000u : 0));
        Marshal.ThrowExceptionForHR(_view.SetGroupBy(ref key, true));
    }

    public ViewSnapshot Snapshot()
    {
        Marshal.ThrowExceptionForHR(_view.GetViewModeAndIconSize(out var mode, out var size));
        var spacingResult = _view.GetSpacing(out var spacing);
        Marshal.ThrowExceptionForHR(_view.ItemCount(2, out var count));
        var positions = new List<ItemPosition>();
        for (var index = 0; index < count; index++)
        {
            var pidl = IntPtr.Zero;
            object? shellItem = null;
            var pathPointer = IntPtr.Zero;
            try
            {
                Marshal.ThrowExceptionForHR(_view.Item(index, out pidl));
                var result = _view.GetItemPosition(pidl, out var point);
                var itemId = typeof(IShellItemNative).GUID;
                Marshal.ThrowExceptionForHR(_view.GetItem(index, ref itemId, out shellItem));
                Marshal.ThrowExceptionForHR(((IShellItemNative)shellItem!).GetDisplayName(0x80058000, out pathPointer));
                positions.Add(new ItemPosition(index, Marshal.PtrToStringUni(pathPointer)!, point.X, point.Y, result));
            }
            finally
            {
                if (pathPointer != IntPtr.Zero) Marshal.FreeCoTaskMem(pathPointer);
                if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
                Release(shellItem);
            }
        }
        return new ViewSnapshot(mode, size, spacing, spacingResult, positions.ToArray());
    }

    public void Dispose() { Release(_shellView); Release(_browser); _shellView = _browser = null; }
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
    [DllImport("propsys.dll", CharSet = CharSet.Unicode)] private static extern int PSGetPropertyKeyFromName(string name, out PropertyKey key);
    [DllImport("user32.dll")]private static extern uint MapVirtualKey(uint code,uint type);
    [StructLayout(LayoutKind.Sequential)]private struct NativeMessage{public IntPtr Window;public uint Message;public IntPtr WParam,LParam;public uint Time;public PointNative Point;public uint Private;}
    [ComImport,Guid("000214E3-0000-0000-C000-000000000046"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellViewAccelerator{[PreserveSig]int GetWindow(out IntPtr window);void ContextSensitiveHelp();[PreserveSig]int TranslateAccelerator(ref NativeMessage message);}

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProviderNative { [PreserveSig] int QueryService(ref Guid service, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object? result); }
    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowserNative
    {
        void GetWindow(); void ContextSensitiveHelp(); void InsertMenusSB(); void SetMenuSB(); void RemoveMenusSB();
        void SetStatusTextSB(); void EnableModelessSB(); void TranslateAcceleratorSB(); void BrowseObject(); void GetViewStateStream();
        void GetControlWindow(); void SendControlMsg(); [PreserveSig] int QueryActiveShellView([MarshalAs(UnmanagedType.Interface)] out object? view);
    }
    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemNative
    {
        void BindToHandler(); void GetParent(); [PreserveSig] int GetDisplayName(uint name, out IntPtr result); void GetAttributes(); void Compare();
    }
    [ComImport, Guid("1AF3A467-214F-4298-908E-06B03E0B39F9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFolderView2Native
    {
        [PreserveSig] int GetCurrentViewMode(out uint mode);
        [PreserveSig] int SetCurrentViewMode(uint mode);
        void GetFolder();
        [PreserveSig] int Item(int index, out IntPtr pidl);
        [PreserveSig] int ItemCount(int flags, out int count);
        void Items(); void GetSelectionMarkedItem(); [PreserveSig] int GetFocusedItem(out int index);
        [PreserveSig] int GetItemPosition(IntPtr pidl, out PointNative point);
        [PreserveSig] int GetSpacing(out PointNative spacing);
        void GetDefaultSpacing(); void GetAutoArrange(); void SelectItem(); void SelectAndPositionItems();
        [PreserveSig] int SetGroupBy(ref PropertyKey key, [MarshalAs(UnmanagedType.Bool)] bool ascending);
        void GetGroupBy(); void SetViewProperty(); void GetViewProperty(); void SetTileViewProperties(); void SetExtendedTileViewProperties();
        void SetText(); [PreserveSig] int SetCurrentFolderFlags(uint mask,uint flags); void GetCurrentFolderFlags(); void GetSortColumnCount(); void SetSortColumns(); void GetSortColumns();
        [PreserveSig] int GetItem(int index, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object? item);
        void GetVisibleItem(); void GetSelectedItem(); void GetSelection(); void GetSelectionState(); void InvokeVerbOnSelection();
        [PreserveSig] int SetViewModeAndIconSize(uint mode, int iconSize);
        [PreserveSig] int GetViewModeAndIconSize(out uint mode, out int iconSize);
    }
}

[StructLayout(LayoutKind.Sequential)] internal struct PointNative { public int X, Y; }
[StructLayout(LayoutKind.Sequential)] internal struct PropertyKey { public Guid Format; public uint Id; }
