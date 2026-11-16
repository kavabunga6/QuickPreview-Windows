using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace QuickPreview;

internal sealed class GlobalKeyboardHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int VkSpace = 0x20;
    private const int VkEscape = 0x1B;
    private const int VkLeft = 0x25;
    private const int VkUp = 0x26;
    private const int VkRight = 0x27;
    private const int VkDown = 0x28;
    private const uint GaRoot = 2;

    private readonly LowLevelKeyboardProc _callback;
    private IntPtr _hook;
    private readonly HashSet<uint> _consumedKeys = new();

    public event Action? SpacePressed;
    internal ExplorerSpaceGesture? DispatchedSpaceGesture { get; private set; }
    public event Action<NavigationDirection>? NavigateRequested;
    public event Action? CloseRequested;

    public GlobalKeyboardHook() => _callback = HookCallback;

    public void Install()
    {
        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule;
        _hook = SetWindowsHookEx(WhKeyboardLl, _callback,
            GetModuleHandle(module?.ModuleName), 0);

        if (_hook == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            return HandleKey(code, wParam, lParam);
        }
        catch (Exception exception)
        {
            // Managed errors must never escape a native keyboard callback or break
            // keyboard input in Explorer while the application is shutting down.
            Debug.WriteLine($"QuickPreview keyboard hook: {exception.Message}");
            return CallNextHookEx(_hook, code, wParam, lParam);
        }
    }

    private IntPtr HandleKey(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var message = wParam.ToInt32();
            var data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            var isDown = message == WmKeyDown || message == WmSysKeyDown;
            var isUp = message == WmKeyUp || message == WmSysKeyUp;
            var key = data.VirtualKeyCode;
            if (isUp && _consumedKeys.Remove(key))
                return new IntPtr(1);

            if (isDown && key == VkSpace)
            {
                if (!_consumedKeys.Contains(key) && CanHandleSpace())
                {
                    _consumedKeys.Add(key);
                    var previewWasVisible = System.Windows.Application.Current.MainWindow is { IsVisible: true };
                    var frame = previewWasVisible ? IntPtr.Zero : GetForegroundWindow();
                    var gesture = new ExplorerSpaceGesture(previewWasVisible, frame,
                        ExplorerSelectionService.GetActiveExplorerTab(frame));
                    System.Windows.Application.Current.Dispatcher.BeginInvoke(
                        new Action(() =>
                        {
                            DispatchedSpaceGesture = gesture;
                            try { SpacePressed?.Invoke(); }
                            finally { DispatchedSpaceGesture = null; }
                        }));
                }

                if (_consumedKeys.Contains(key))
                    return new IntPtr(1);
            }

            if (isDown && key == VkEscape)
            {
                if (!_consumedKeys.Contains(key) && CanHandleClose())
                {
                    _consumedKeys.Add(key);
                    System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (CanHandleClose())
                            CloseRequested?.Invoke();
                    }));
                }

                if (_consumedKeys.Contains(key))
                    return new IntPtr(1);
            }

            if (isDown && key is VkLeft or VkUp or VkRight or VkDown)
            {
                if (CanHandleNavigation())
                {
                    _consumedKeys.Add(key);
                    var direction = (NavigationDirection)key;
                    System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        // A queued repeat must not change a hidden preview or steal focus
                        // after the user has already switched to another application.
                        if (CanHandleNavigation())
                            NavigateRequested?.Invoke(direction);
                    }));
                    return new IntPtr(1);
                }

                if (_consumedKeys.Contains(key))
                    return new IntPtr(1);
            }
        }

        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private static bool CanHandleSpace()
    {
        if (HasModifiers())
            return false;

        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
            return false;

        if (IsPreviewForeground(foreground))
            return !NativeInputHasFocus(foreground) && !PreviewControlNeedsArrowKeys();

        var className = GetWindowClass(foreground);
        if (!string.Equals(className, "CabinetWClass", StringComparison.Ordinal) &&
            !string.Equals(className, "ExploreWClass", StringComparison.Ordinal))
            return false;

        return !NativeInputHasFocus(foreground);
    }

    private static bool CanHandleNavigation()
    {
        if (HasModifiers() || System.Windows.Application.Current.MainWindow is not { IsVisible: true })
            return false;

        var foreground = GetForegroundWindow();
        if (IsPreviewForeground(foreground))
            return CanHandlePreviewNavigation();

        // Explorer must receive its own arrows. Its view decides the next highlighted
        // item, including current sort, grouping, icon layout, folders and filters.
        return false;
    }

    private static bool CanHandleClose()
    {
        if (HasModifiers() || System.Windows.Application.Current.MainWindow is not { IsVisible: true })
            return false;

        var foreground = GetForegroundWindow();
        return (IsPreviewForeground(foreground) || ExplorerSelectionService.IsOriginWindow(foreground)) &&
               !NativeInputHasFocus(foreground);
    }

    internal static bool CanHandlePreviewNavigation() =>
        !HasModifiers() && !PreviewControlNeedsArrowKeys() && !NativeInputHasFocus(GetForegroundWindow());

    private static bool IsPreviewForeground(IntPtr foreground)
    {
        if (foreground == IntPtr.Zero || System.Windows.Application.Current.MainWindow is not { IsVisible: true } preview)
            return false;

        var previewHandle = new System.Windows.Interop.WindowInteropHelper(preview).Handle;
        return previewHandle != IntPtr.Zero && GetAncestor(foreground, GaRoot) == previewHandle;
    }

    private static bool PreviewControlNeedsArrowKeys()
    {
        for (var current = Keyboard.FocusedElement as DependencyObject; current is not null;)
        {
            if (current is Slider or System.Windows.Controls.ComboBox or PasswordBox or System.Windows.Controls.TabControl or TabItem ||
                current is System.Windows.Controls.Primitives.TextBoxBase { IsReadOnly: false })
                return true;

            current = current is Visual || current is System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return false;
    }

    private static bool HasModifiers() =>
        IsKeyDown(0x10) || IsKeyDown(0x11) || IsKeyDown(0x12) || IsKeyDown(0x5B) || IsKeyDown(0x5C);

    private static bool IsKeyDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    private static bool NativeInputHasFocus(IntPtr window)
    {
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        if (!GetGUIThreadInfo(GetWindowThreadProcessId(window, IntPtr.Zero), ref info))
            return true;

        // Menus, Explorer rename/search/address boxes, and native document fields
        // retain normal keyboard handling. Reading focus does not attach input queues.
        if ((info.Flags & (0x4 | 0x8 | 0x10)) != 0)
            return true;

        var focusedClass = GetWindowClass(info.Focus);
        return focusedClass.Contains("Edit", StringComparison.OrdinalIgnoreCase) ||
               focusedClass.Contains("ComboBox", StringComparison.OrdinalIgnoreCase) ||
               (info.Caret != IntPtr.Zero && focusedClass.StartsWith("Chrome_", StringComparison.Ordinal));
    }

    private static string GetWindowClass(IntPtr window)
    {
        if (window == IntPtr.Zero)
            return string.Empty;

        var builder = new StringBuilder(256);
        _ = GetClassName(window, builder, builder.Capacity);
        return builder.ToString();
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VirtualKeyCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size;
        public uint Flags;
        public IntPtr Active;
        public IntPtr Focus;
        public IntPtr Capture;
        public IntPtr MenuOwner;
        public IntPtr MoveSize;
        public IntPtr Caret;
        public int CaretLeft;
        public int CaretTop;
        public int CaretRight;
        public int CaretBottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, IntPtr processId);

    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
}
