using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Input;

namespace QuickPreview;

internal sealed class GlobalKeyboardHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int VkSpace = 0x20;

    private readonly LowLevelKeyboardProc _callback;
    private IntPtr _hook;
    private bool _consumeSpaceUntilKeyUp;

    public event Action? SpacePressed;

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
        if (code >= 0)
        {
            var message = wParam.ToInt32();
            var data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            if (data.VirtualKeyCode == VkSpace)
            {
                if ((message == WmKeyUp || message == WmSysKeyUp) && _consumeSpaceUntilKeyUp)
                {
                    _consumeSpaceUntilKeyUp = false;
                    return new IntPtr(1);
                }

                if (message == WmKeyDown || message == WmSysKeyDown)
                {
                    if (!_consumeSpaceUntilKeyUp && CanHandleSpace())
                    {
                        _consumeSpaceUntilKeyUp = true;
                        System.Windows.Application.Current.Dispatcher.BeginInvoke(
                            new Action(() => SpacePressed?.Invoke()));
                    }

                    if (_consumeSpaceUntilKeyUp)
                        return new IntPtr(1);
                }
            }
        }

        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private static bool CanHandleSpace()
    {
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0)
            return false;

        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
            return false;

        if (System.Windows.Application.Current.MainWindow is { IsVisible: true } preview &&
            new System.Windows.Interop.WindowInteropHelper(preview).Handle == foreground)
            return true;

        var className = GetWindowClass(foreground);
        if (!string.Equals(className, "CabinetWClass", StringComparison.Ordinal) &&
            !string.Equals(className, "ExploreWClass", StringComparison.Ordinal))
            return false;

        return !ExplorerHasTextInputFocus(foreground);
    }

    private static bool ExplorerHasTextInputFocus(IntPtr explorerWindow)
    {
        var foregroundThread = GetWindowThreadProcessId(explorerWindow, IntPtr.Zero);
        var currentThread = GetCurrentThreadId();
        try
        {
            if (foregroundThread != currentThread)
                AttachThreadInput(currentThread, foregroundThread, true);

            var focused = GetFocus();
            var focusedClass = GetWindowClass(focused);
            return focusedClass.Contains("Edit", StringComparison.OrdinalIgnoreCase) ||
                   focusedClass.Contains("RichEdit", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (foregroundThread != currentThread)
                AttachThreadInput(currentThread, foregroundThread, false);
        }
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

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, bool doAttach);

    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();
}
