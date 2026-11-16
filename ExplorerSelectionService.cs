using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.CSharp.RuntimeBinder;

namespace QuickPreview;

internal enum NavigationDirection { Left = 0x25, Up = 0x26, Right = 0x27, Down = 0x28 }

internal static class ExplorerSelectionService
{
    private const int SelectItemFlags = 0x1 | 0x4 | 0x8 | 0x10 | 0x40000000; // select, deselect others, ensure visible, focused item, NO TAKE FOCUS
    private static IntPtr _originExplorerWindow;
    private static int _originGeneration;
    private static long _originBindingId;
    private static ExplorerOriginIdentity? _originIdentity;
    internal static ExplorerOriginIdentity? OriginIdentity => Volatile.Read(ref _originIdentity);
    internal static event Action? OriginIdentityChanged;
    internal static int OriginGeneration => Volatile.Read(ref _originGeneration);
    internal static void CancelPendingNavigation() => Interlocked.Increment(ref _originGeneration);

    internal static bool IsOriginWindow(IntPtr window) =>
        window != IntPtr.Zero && window == _originExplorerWindow && IsWindow(window) &&
        (OriginIdentity is not { } identity || CheckOriginLifetime(identity) == OriginLifetimeState.Alive);

    internal static bool HasOriginWindow => IsOriginWindow(_originExplorerWindow);
    internal static bool IsOriginForeground => IsOriginWindow(GetForegroundWindow()) && IsOriginTabActive;
    internal static bool IsOriginTabActive => OriginIdentity is not { } identity ||
        CheckOriginLifetime(identity) == OriginLifetimeState.Alive && GetActiveExplorerTab(identity.FrameWindow) == identity.TabWindow;
    internal static void ForgetOriginWindow()
    {
        Interlocked.Increment(ref _originGeneration);
        Interlocked.Increment(ref _originBindingId);
        Volatile.Write(ref _originIdentity, null);
        _originExplorerWindow = IntPtr.Zero;
        OriginIdentityChanged?.Invoke();
    }
    internal static void RestoreOriginFocus()
    {
        if (HasOriginWindow && IsOriginTabActive)
            _ = SetForegroundWindow(_originExplorerWindow);
    }

    public static string? TryGetSelectedPath()
    {
        var foreground = GetForegroundWindow();
        return TryGetSelectedPathInTab(foreground, GetActiveExplorerTab(foreground));
    }

    internal static string? TryGetSelectedPathInTab(IntPtr frame, IntPtr tab)
    {
        if (frame == IntPtr.Zero || tab == IntPtr.Zero || GetForegroundWindow() != frame || GetActiveExplorerTab(frame) != tab)
            return null;
        return WithExplorerWindow(frame, explorer =>
        {
            var path = ReadSelectedPath(explorer);
            if (string.IsNullOrWhiteSpace(path) || GetForegroundWindow() != frame || GetActiveExplorerTab(frame) != tab)
                return null;
            var frameThread = GetWindowThreadProcessId(frame, out var process);
            var tabThread = GetWindowThreadProcessId(tab, out var tabProcess);
            var frameClass = WindowClass(frame);
            var tabClass = WindowClass(tab);
            if (frameThread == 0 || tabThread == 0 || process != tabProcess || frameClass.Length == 0 || tabClass.Length == 0)
                return null;
            var identity = new ExplorerOriginIdentity(Interlocked.Increment(ref _originBindingId), frame, tab,
                process, frameThread, tabThread, unchecked((uint)Environment.TickCount), frameClass, tabClass);
            if (CheckOriginLifetime(identity) != OriginLifetimeState.Alive) return null;
            Interlocked.Increment(ref _originGeneration);
            _originExplorerWindow = frame;
            Volatile.Write(ref _originIdentity, identity);
            OriginIdentityChanged?.Invoke();
            return path;
        }, tab);
    }

    internal static string? TryGetSelectedPathInOriginWindow() =>
        HasOriginWindow && IsOriginTabActive ? WithExplorerWindow(_originExplorerWindow, ReadSelectedPath) : null;

    internal static string? TryNavigateInOriginWindow(NavigationDirection direction) =>
        NavigateCore(direction, IntPtr.Zero, OriginGeneration);

    internal static string? TryNavigateFromPreview(NavigationDirection direction, IntPtr previewWindow, int originGeneration) =>
        NavigateCore(direction, previewWindow, originGeneration);

    private static string? NavigateCore(NavigationDirection direction, IntPtr expectedForeground, int generation)
    {
        if (!HasOriginWindow || !IsOriginTabActive || OriginGeneration != generation || ModifiersPressed() || direction is not (NavigationDirection.Left or NavigationDirection.Up or
            NavigationDirection.Right or NavigationDirection.Down)) return null;
        var origin = _originExplorerWindow;
        var foreground = GetForegroundWindow();
        if (expectedForeground != IntPtr.Zero && foreground != expectedForeground) return null;
        bool StillCurrent() => IsOriginWindow(origin) && IsOriginTabActive && OriginGeneration == generation &&
            GetForegroundWindow() == foreground && !ModifiersPressed();
        return WithExplorerWindow(origin, explorer =>
        {
            object? browser = null, shellView = null, enumeratorObject = null;
            var focusedPidl = IntPtr.Zero;
            var currentPidl = IntPtr.Zero;
            var items = new List<ViewItem>();
            try
            {
                var serviceId = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837"); // SID_STopLevelBrowser
                var browserId = typeof(IShellBrowserNative).GUID;
                if (((IServiceProviderNative)explorer).QueryService(ref serviceId, ref browserId, out browser) < 0 || browser is null ||
                    ((IShellBrowserNative)browser).QueryActiveShellView(out shellView) < 0 || shellView is null) return null;
                var folderView = (IFolderViewNative)shellView;
                if (folderView.GetCurrentViewMode(out var mode) < 0 ||
                    folderView.GetFocusedItem(out var focusedIndex) < 0 || focusedIndex < 0 ||
                    folderView.Item(focusedIndex, out focusedPidl) < 0 || focusedPidl == IntPtr.Zero) return null;

                // Details and Content are single-column views. Horizontal arrows do
                // not select the previous/next file in those views.
                if ((mode is 4 or 8) && direction is NavigationDirection.Left or NavigationDirection.Right)
                    return ReadSelectedPath(explorer);
                if (mode is 4 or 8)
                    return NavigateRows(explorer, shellView, focusedPidl, direction, StillCurrent);
                if (folderView.GetItemPosition(focusedPidl, out var focusedPosition) < 0)
                    return NavigateAccessibleGrid(explorer, shellView, folderView, focusedIndex, focusedPidl, mode, direction, StillCurrent);
                _ = folderView.GetSpacing(out var spacing);
                _ = GetClientRect(origin, out var initialBounds);
                var enumeratorId = typeof(IEnumIdListNative).GUID;
                // Enumerate this active view, including its current filtering,
                // sorting and grouping, never an independent directory listing.
                if (folderView.Items(unchecked((int)0x80000002), ref enumeratorId, out enumeratorObject) < 0 ||
                    enumeratorObject is null) return null;
                var enumerator = (IEnumIdListNative)enumeratorObject;
                var elapsed = Stopwatch.StartNew();
                var complete = false;
                for (var index = 0; index < 100000 && elapsed.Elapsed < TimeSpan.FromMilliseconds(500); index++)
                {
                    var result = enumerator.Next(1, out currentPidl, out var fetched);
                    if (result == 1 || fetched == 0) { complete = true; break; }
                    if (result < 0 || currentPidl == IntPtr.Zero) return null;
                    if (folderView.GetItemPosition(currentPidl, out var position) >= 0)
                    {
                        items.Add(new ViewItem(currentPidl, position));
                        currentPidl = IntPtr.Zero;
                    }
                    else
                    {
                        Marshal.FreeCoTaskMem(currentPidl);
                        currentPidl = IntPtr.Zero;
                    }
                }
                // A partial geometry snapshot can produce a wrong neighbor.
                if (!complete) return ReadSelectedPath(explorer);
                var target = FindDirectionalNeighbor(items, focusedPosition, spacing, mode, direction);
                if (target is null) return ReadSelectedPath(explorer);

                // Explorer can be resized or receive native input during COM calls.
                // Do not commit a selection based on a snapshot that became stale.
                if (!StillCurrent() || !GetClientRect(origin, out var currentBounds) || !initialBounds.Equals(currentBounds) ||
                    folderView.GetCurrentViewMode(out var currentMode) < 0 || currentMode != mode ||
                    !HasSameFocusedItem(folderView, focusedPidl)) return ReadSelectedPath(explorer);
                if (((IShellViewNative)shellView).SelectItem(target.Pidl, SelectItemFlags) < 0) return null;
                return ReadSelectedPath(explorer);
            }
            finally
            {
                if (currentPidl != IntPtr.Zero) Marshal.FreeCoTaskMem(currentPidl);
                foreach (var item in items) Marshal.FreeCoTaskMem(item.Pidl);
                if (focusedPidl != IntPtr.Zero) Marshal.FreeCoTaskMem(focusedPidl);
                ReleaseCom(enumeratorObject); ReleaseCom(shellView); ReleaseCom(browser);
            }
        });
    }

    private static string? NavigateAccessibleGrid(object explorer, object shellView, IFolderViewNative view,
        int focusedIndex, IntPtr focusedPidl, uint mode, NavigationDirection direction, Func<bool> stillCurrent)
    {
        if (((IShellViewNative)shellView).GetWindow(out var viewWindow) < 0 || viewWindow == IntPtr.Zero ||
            !GetClientRect(viewWindow, out var bounds)) return ReadSelectedPath(explorer);
        var view2 = (IFolderView2Native)shellView;
        if (view2.GetViewModeAndIconSize(out _, out var iconSize) < 0) return ReadSelectedPath(explorer);
        var grid = ExplorerGridLayout.Read(viewWindow, focusedIndex, direction, GetDisplayName(view2, focusedIndex));
        if (grid is null || !string.Equals(grid.SourceName, GetDisplayName(view2, focusedIndex), StringComparison.OrdinalIgnoreCase))
            return ReadSelectedPath(explorer);
        var targetPidl = IntPtr.Zero;
        try
        {
            if (grid.TargetIndex is { } targetIndex)
            {
                if (grid.IndicesAreGlobal)
                {
                    if (!string.Equals(grid.TargetName, GetDisplayName(view2, targetIndex), StringComparison.OrdinalIgnoreCase) ||
                        view.Item(targetIndex, out targetPidl) < 0) return ReadSelectedPath(explorer);
                }
                else targetPidl = FindUniqueDisplayName(view, view2, grid.TargetName);
            }
            else
            {
                // UIA virtualizes items outside the viewport. Its lane dimensions
                // still describe the live layout; select the corresponding child
                // in Shell view order and let ENSUREVISIBLE scroll it into view.
                if (grid.OffscreenStrideSafe)
                    targetPidl = GetOffscreenNeighbor(view, focusedPidl, grid, mode, direction);
            }
            if (targetPidl == IntPtr.Zero) return ReadSelectedPath(explorer);
            if (!stillCurrent() ||
                !GetClientRect(viewWindow, out var currentBounds) || !bounds.Equals(currentBounds) ||
                view2.GetViewModeAndIconSize(out var currentMode, out var currentSize) < 0 || currentMode != mode || currentSize != iconSize ||
                !HasSameFocusedItem(view, focusedPidl)) return ReadSelectedPath(explorer);
            if (((IShellViewNative)shellView).SelectItem(targetPidl, SelectItemFlags) < 0) return null;
            return ReadSelectedPath(explorer);
        }
        finally { if (targetPidl != IntPtr.Zero) Marshal.FreeCoTaskMem(targetPidl); }
    }

    private static IntPtr FindUniqueDisplayName(IFolderViewNative view, IFolderView2Native view2, string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || view.ItemCount(2, out var count) < 0) return IntPtr.Zero;
        var match = -1;
        var elapsed = Stopwatch.StartNew();
        for (var index = 0; index < count; index++)
        {
            if (index >= 100000 || elapsed.Elapsed > TimeSpan.FromMilliseconds(500)) return IntPtr.Zero;
            if (!string.Equals(name, GetDisplayName(view2, index), StringComparison.OrdinalIgnoreCase)) continue;
            if (match >= 0) return IntPtr.Zero;
            match = index;
        }
        return match >= 0 && view.Item(match, out var pidl) >= 0 ? pidl : IntPtr.Zero;
    }

    private static string? GetDisplayName(IFolderView2Native view, int index)
    {
        var iid = typeof(IShellItemNative).GUID;
        object? item = null;
        var text = IntPtr.Zero;
        try
        {
            if (view.GetItem(index, ref iid, out item) < 0 || item is null ||
                ((IShellItemNative)item).GetDisplayName(0, out text) < 0) return null;
            return Marshal.PtrToStringUni(text);
        }
        finally { if (text != IntPtr.Zero) Marshal.FreeCoTaskMem(text); ReleaseCom(item); }
    }

    private static IntPtr GetOffscreenNeighbor(IFolderViewNative view, IntPtr focusedPidl, ExplorerGridSnapshot grid,
        uint mode, NavigationDirection direction)
    {
        object? enumeration = null;
        var items = new List<IntPtr>();
        var current = IntPtr.Zero;
        try
        {
            var iid = typeof(IEnumIdListNative).GUID;
            if (view.Items(unchecked((int)0x80000002), ref iid, out enumeration) < 0 || enumeration is null) return IntPtr.Zero;
            var enumerator = (IEnumIdListNative)enumeration;
            var complete = false;
            var focused = -1;
            var elapsed = Stopwatch.StartNew();
            for (var count = 0; count < 100000 && elapsed.Elapsed < TimeSpan.FromMilliseconds(500); count++)
            {
                var status = enumerator.Next(1, out current, out var fetched);
                if (status == 1 || fetched == 0) { complete = true; break; }
                if (status < 0 || current == IntPtr.Zero) return IntPtr.Zero;
                if (ILIsEqual(current, focusedPidl)) focused = items.Count;
                items.Add(current); current = IntPtr.Zero;
            }
            if (!complete || focused < 0) return IntPtr.Zero;
            var stride = mode == 3 ? grid.Rows : grid.Columns;
            if (stride <= 0 || focused % stride != (mode == 3 ? grid.FocusedRow : grid.FocusedColumn)) return IntPtr.Zero;
            var alongLane = mode == 3
                ? direction is NavigationDirection.Up or NavigationDirection.Down
                : direction is NavigationDirection.Left or NavigationDirection.Right;
            var sign = direction is NavigationDirection.Left or NavigationDirection.Up ? -1 : 1;
            var next = focused + sign * (alongLane ? 1 : stride);
            if (alongLane && ((sign < 0 && focused % stride == 0) || (sign > 0 && focused % stride == stride - 1))) return IntPtr.Zero;
            if (!alongLane && next >= items.Count && focused / stride < (items.Count - 1) / stride) next = items.Count - 1;
            if (next < 0 || next >= items.Count) return IntPtr.Zero;
            var result = items[next]; items[next] = IntPtr.Zero;
            return result;
        }
        finally
        {
            if (current != IntPtr.Zero) Marshal.FreeCoTaskMem(current);
            foreach (var item in items) if (item != IntPtr.Zero) Marshal.FreeCoTaskMem(item);
            ReleaseCom(enumeration);
        }
    }

    private static string? NavigateRows(object explorer, object shellView, IntPtr focusedPidl,
        NavigationDirection direction, Func<bool> stillCurrent)
    {
        var view = (IFolderViewNative)shellView;
        var enumeratorId = typeof(IEnumIdListNative).GUID;
        object? enumeration = null;
        var current = IntPtr.Zero;
        var previous = IntPtr.Zero;
        try
        {
            if (view.Items(unchecked((int)0x80000002), ref enumeratorId, out enumeration) < 0 || enumeration is null) return null;
            var enumerator = (IEnumIdListNative)enumeration;
            var found = false;
            var elapsed = Stopwatch.StartNew();
            for (var index = 0; index < 100000 && elapsed.Elapsed < TimeSpan.FromMilliseconds(500); index++)
            {
                if (enumerator.Next(1, out current, out var fetched) != 0 || fetched != 1 || current == IntPtr.Zero)
                    return ReadSelectedPath(explorer);
                var target = IntPtr.Zero;
                if (found) target = current;
                else if (ILIsEqual(current, focusedPidl))
                {
                    found = true;
                    if (direction == NavigationDirection.Up)
                    {
                        if (previous == IntPtr.Zero) return ReadSelectedPath(explorer);
                        target = previous;
                    }
                }
                if (target != IntPtr.Zero)
                {
                    if (!stillCurrent() || !HasSameFocusedItem(view, focusedPidl)) return ReadSelectedPath(explorer);
                    if (((IShellViewNative)shellView).SelectItem(target, SelectItemFlags) < 0) return null;
                    return ReadSelectedPath(explorer);
                }
                if (previous != IntPtr.Zero) Marshal.FreeCoTaskMem(previous);
                previous = current; current = IntPtr.Zero;
            }
            return ReadSelectedPath(explorer);
        }
        finally
        {
            if (current != IntPtr.Zero) Marshal.FreeCoTaskMem(current);
            if (previous != IntPtr.Zero) Marshal.FreeCoTaskMem(previous);
            ReleaseCom(enumeration);
        }
    }

    private static bool HasSameFocusedItem(IFolderViewNative view, IntPtr expected)
    {
        var current = IntPtr.Zero;
        try
        {
            return view.GetFocusedItem(out var index) >= 0 && index >= 0 &&
                view.Item(index, out current) >= 0 && current != IntPtr.Zero && ILIsEqual(current, expected);
        }
        finally { if (current != IntPtr.Zero) Marshal.FreeCoTaskMem(current); }
    }

    private static bool ModifiersPressed() =>
        new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0);

    private static ViewItem? FindDirectionalNeighbor(List<ViewItem> items, NativePoint origin, NativePoint spacing,
        uint mode, NavigationDirection direction)
    {
        var horizontal = direction is NavigationDirection.Left or NavigationDirection.Right;
        var sign = direction is NavigationDirection.Left or NavigationDirection.Up ? -1L : 1L;
        // Lists fill columns top-to-bottom; icon/tile views fill rows left-to-right.
        var acrossLanes = mode == 3 ? horizontal : !horizontal;
        var sameLaneTolerance = Math.Max(1L, Math.Abs((long)(horizontal ? spacing.Y : spacing.X)) / 3);
        var nextLaneTolerance = Math.Max(1L, Math.Abs((long)(horizontal ? spacing.X : spacing.Y)) / 3);
        var candidates = items.Select(item =>
        {
            var dx = (long)item.Position.X - origin.X;
            var dy = (long)item.Position.Y - origin.Y;
            return (Item: item, Forward: (horizontal ? dx : dy) * sign, Across: Math.Abs(horizontal ? dy : dx));
        }).Where(candidate => candidate.Forward > 1 && (acrossLanes || candidate.Across <= sameLaneTolerance)).ToArray();
        if (candidates.Length == 0) return null;
        if (!acrossLanes)
            return candidates.OrderBy(candidate => candidate.Forward).ThenBy(candidate => candidate.Across).First().Item;
        var nearestLane = candidates.Min(candidate => candidate.Forward);
        return candidates.Where(candidate => candidate.Forward <= nearestLane + nextLaneTolerance)
            .OrderBy(candidate => candidate.Across).ThenBy(candidate => candidate.Forward).First().Item;
    }

    private sealed record ViewItem(IntPtr Pidl, NativePoint Position);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

    private static string? ReadSelectedPath(object explorer)
    {
        object? document = null;
        object? selectedItems = null;
        object? focused = null;
        object? item = null;
        try
        {
            document = ((dynamic)explorer).Document;
            selectedItems = ((dynamic)document).SelectedItems();
            var count = (int)((dynamic)selectedItems).Count;
            if (count < 1)
                return null;

            focused = ((dynamic)document).FocusedItem;
            var focusedPath = focused is null ? null : (string?)((dynamic)focused).Path;
            string? firstPath = null;
            // Prefer the focused selected item when Shift/Ctrl has selected several files.
            for (var index = 0; index < Math.Min(count, 256); index++)
            {
                item = ((dynamic)selectedItems).Item(index);
                var path = item is null ? null : (string?)((dynamic)item).Path;
                firstPath ??= path;
                if (!string.IsNullOrWhiteSpace(focusedPath) && PathsEqual(path, focusedPath))
                    return path;
                ReleaseCom(item);
                item = null;
            }

            return firstPath;
        }
        finally
        {
            ReleaseCom(item);
            ReleaseCom(focused);
            ReleaseCom(selectedItems);
            ReleaseCom(document);
        }
    }

    private static string? WithExplorerWindow(IntPtr window, Func<object, string?> action, IntPtr expectedTab = default)
    {
        object? shell = null;
        object? windows = null;
        try
        {
            var tab = expectedTab != IntPtr.Zero ? expectedTab :
                OriginIdentity is { } identity && identity.FrameWindow == window ? identity.TabWindow : GetActiveExplorerTab(window);
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null)
                return null;

            shell = Activator.CreateInstance(shellType);
            if (shell is null)
                return null;

            dynamic dynamicShell = shell;
            windows = dynamicShell.Windows();
            dynamic dynamicWindows = windows;
            var count = (int)dynamicWindows.Count;

            for (var index = 0; index < count; index++)
            {
                object? explorer = null;
                try
                {
                    explorer = dynamicWindows.Item(index);
                    if (explorer is null)
                        continue;

                    dynamic dynamicExplorer = explorer;
                    if (Convert.ToInt64(dynamicExplorer.HWND) != window.ToInt64())
                        continue;
                    if (GetBrowserWindow(explorer) != tab) continue;

                    return action(explorer);
                }
                catch (Exception exception) when (exception is COMException or InvalidCastException or RuntimeBinderException)
                {
                    // Explorer may be navigating while the hotkey is pressed.
                }
                finally
                {
                    ReleaseCom(explorer);
                }
            }
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or RuntimeBinderException)
        {
            return null;
        }
        finally
        {
            ReleaseCom(windows);
            ReleaseCom(shell);
        }

        return null;
    }

    internal static IntPtr GetActiveExplorerTab(IntPtr frame) => frame == IntPtr.Zero ? IntPtr.Zero :
        FindWindowEx(frame, IntPtr.Zero, "ShellTabWindowClass", null) is var tab && tab != IntPtr.Zero ? tab : frame;

    internal static OriginLifetimeState CheckOriginLifetime(ExplorerOriginIdentity identity)
    {
        var frame = CheckWindow(identity.FrameWindow, identity.ProcessId, identity.FrameThreadId, identity.FrameClass);
        if (frame != OriginLifetimeState.Alive) return frame;
        var tab = CheckWindow(identity.TabWindow, identity.ProcessId, identity.TabThreadId, identity.TabClass);
        if (tab != OriginLifetimeState.Alive) return tab;
        return identity.TabWindow == identity.FrameWindow || IsChild(identity.FrameWindow, identity.TabWindow)
            ? OriginLifetimeState.Alive : OriginLifetimeState.Closed;
    }

    private static OriginLifetimeState CheckWindow(IntPtr window, uint process, uint thread, string className)
    {
        if (!IsWindow(window)) return OriginLifetimeState.Closed;
        var actualThread = GetWindowThreadProcessId(window, out var actualProcess);
        if (actualThread == 0) return IsWindow(window) ? OriginLifetimeState.Unknown : OriginLifetimeState.Closed;
        if (actualThread != thread || actualProcess != process) return OriginLifetimeState.Closed;
        var actualClass = WindowClass(window);
        if (actualClass.Length == 0) return OriginLifetimeState.Unknown;
        return actualClass == className ? OriginLifetimeState.Alive : OriginLifetimeState.Closed;
    }

    private static string WindowClass(IntPtr window)
    {
        var text = new StringBuilder(256);
        return GetClassName(window, text, text.Capacity) > 0 ? text.ToString() : string.Empty;
    }

    private static IntPtr GetBrowserWindow(object explorer)
    {
        object? browser = null;
        try
        {
            var browserId = typeof(IShellBrowserNative).GUID;
            var serviceId = browserId; // SID_SShellBrowser == IID_IShellBrowser: the tab's own host.
            if (((IServiceProviderNative)explorer).QueryService(ref serviceId, ref browserId, out browser) < 0 || browser is null)
            {
                ReleaseCom(browser);
                browser = null;
                serviceId = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837"); // SID_STopLevelBrowser
                if (((IServiceProviderNative)explorer).QueryService(ref serviceId, ref browserId, out browser) < 0 || browser is null)
                    return IntPtr.Zero;
            }
            return ((IShellBrowserNative)browser).GetWindow(out var window) >= 0 ? window : IntPtr.Zero;
        }
        finally { ReleaseCom(browser); }
    }

    public static bool TrySelectPathInOriginWindow(string path, bool activateWindow = true)
    {
        if (!HasOriginWindow || !IsOriginTabActive) return false;
        var origin = _originExplorerWindow;
        var identity = OriginIdentity;
        var parentPath = Directory.Exists(path) ? Directory.GetParent(path)?.FullName : Path.GetDirectoryName(path);
        var itemName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(parentPath) || string.IsNullOrWhiteSpace(itemName)) return false;
        var selected = false;
        _ = WithExplorerWindow(origin, explorer =>
        {
            object? document = null, folder = null, folderSelf = null, item = null;
            try
            {
                document = ((dynamic)explorer).Document;
                folder = ((dynamic)document).Folder;
                folderSelf = ((dynamic)folder).Self;
                if (!PathsEqual((string?)((dynamic)folderSelf).Path, parentPath)) return null;
                item = ((dynamic)folder).ParseName(itemName);
                if (item is null || !ReferenceEquals(identity, OriginIdentity) || !HasOriginWindow || !IsOriginTabActive) return null;
                ((dynamic)document).SelectItem(item, SelectItemFlags);
                selected = true;
                return path;
            }
            finally { ReleaseCom(item); ReleaseCom(folderSelf); ReleaseCom(folder); ReleaseCom(document); }
        });
        if (selected && activateWindow && ReferenceEquals(identity, OriginIdentity) && IsOriginTabActive)
            _ = SetForegroundWindow(origin);
        return selected;
    }

    private static bool PathsEqual(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
            return false;

        try
        {
            var left = Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var right = Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void ReleaseCom(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
            Marshal.ReleaseComObject(value);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProviderNative
    {
        [PreserveSig] int QueryService(ref Guid service, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object? result);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowserNative
    {
        [PreserveSig] int GetWindow(out IntPtr window);
        void ContextSensitiveHelp();
        void InsertMenusSB();
        void SetMenuSB();
        void RemoveMenusSB();
        void SetStatusTextSB();
        void EnableModelessSB();
        void TranslateAcceleratorSB();
        void BrowseObject();
        void GetViewStateStream();
        void GetControlWindow();
        void SendControlMsg();
        [PreserveSig] int QueryActiveShellView([MarshalAs(UnmanagedType.Interface)] out object? view);
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellViewNative
    {
        [PreserveSig] int GetWindow(out IntPtr window);
        void ContextSensitiveHelp();
        void TranslateAccelerator();
        void EnableModeless();
        void UIActivate();
        void Refresh();
        void CreateViewWindow();
        void DestroyViewWindow();
        void GetCurrentInfo();
        void AddPropertySheetPages();
        void SaveViewState();
        [PreserveSig] int SelectItem(IntPtr pidl, int flags);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemNative
    {
        void BindToHandler(); void GetParent();
        [PreserveSig] int GetDisplayName(uint name, out IntPtr result);
    }

    [ComImport, Guid("1AF3A467-214F-4298-908E-06B03E0B39F9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFolderView2Native
    {
        void GetCurrentViewMode(); void SetCurrentViewMode(); void GetFolder(); void Item(); void ItemCount();
        void Items(); void GetSelectionMarkedItem(); void GetFocusedItem(); void GetItemPosition(); void GetSpacing();
        void GetDefaultSpacing(); void GetAutoArrange(); void SelectItem(); void SelectAndPositionItems();
        void SetGroupBy(); void GetGroupBy(); void SetViewProperty(); void GetViewProperty(); void SetTileViewProperties();
        void SetExtendedTileViewProperties(); void SetText(); void SetCurrentFolderFlags(); void GetCurrentFolderFlags();
        void GetSortColumnCount(); void SetSortColumns(); void GetSortColumns();
        [PreserveSig] int GetItem(int index, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object? item);
        void GetVisibleItem(); void GetSelectedItem(); void GetSelection(); void GetSelectionState(); void InvokeVerbOnSelection();
        void SetViewModeAndIconSize();
        [PreserveSig] int GetViewModeAndIconSize(out uint mode, out int iconSize);
    }

    [ComImport, Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFolderViewNative
    {
        [PreserveSig] int GetCurrentViewMode(out uint mode);
        void SetCurrentViewMode();
        void GetFolder();
        [PreserveSig] int Item(int index, out IntPtr pidl);
        [PreserveSig] int ItemCount(int flags, out int count);
        [PreserveSig] int Items(int flags, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object? enumerator);
        void GetSelectionMarkedItem();
        [PreserveSig] int GetFocusedItem(out int index);
        [PreserveSig] int GetItemPosition(IntPtr pidl, out NativePoint point);
        [PreserveSig] int GetSpacing(out NativePoint point);
    }

    [ComImport, Guid("000214F2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumIdListNative
    {
        [PreserveSig] int Next(uint count, out IntPtr pidl, out uint fetched);
    }

    [DllImport("shell32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ILIsEqual(IntPtr first, IntPtr second);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr window, out NativeRect bounds);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string? title);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder text, int capacity);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(IntPtr parent, IntPtr child);
}
