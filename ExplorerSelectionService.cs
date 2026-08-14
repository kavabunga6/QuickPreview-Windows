using System.IO;
using System.Runtime.InteropServices;

namespace QuickPreview;

internal static class ExplorerSelectionService
{
    private const int SelectItemFlags = 0x1 | 0x4 | 0x8 | 0x10; // select, deselect others, ensure visible, focus
    private static IntPtr _originExplorerWindow;

    public static string? TryGetSelectedPath()
    {
        object? shell = null;
        object? windows = null;
        try
        {
            var foreground = GetForegroundWindow().ToInt64();
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
                object? document = null;
                object? selectedItems = null;
                object? item = null;
                try
                {
                    explorer = dynamicWindows.Item(index);
                    if (explorer is null)
                        continue;

                    dynamic dynamicExplorer = explorer;
                    if (Convert.ToInt64(dynamicExplorer.HWND) != foreground)
                        continue;

                    document = dynamicExplorer.Document;
                    selectedItems = ((dynamic)document).SelectedItems();
                    if ((int)((dynamic)selectedItems).Count < 1)
                        return null;

                    item = ((dynamic)selectedItems).Item(0);
                    var path = (string?)((dynamic)item).Path;
                    if (!string.IsNullOrWhiteSpace(path))
                        _originExplorerWindow = new IntPtr(foreground);
                    return path;
                }
                catch (COMException)
                {
                    // Explorer may be navigating while the hotkey is pressed.
                }
                finally
                {
                    ReleaseCom(item);
                    ReleaseCom(selectedItems);
                    ReleaseCom(document);
                    ReleaseCom(explorer);
                }
            }
        }
        catch (COMException)
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

    public static bool TrySelectPathInOriginWindow(string path)
    {
        if (_originExplorerWindow == IntPtr.Zero || !IsWindow(_originExplorerWindow))
            return false;

        var parentPath = Directory.Exists(path)
            ? Directory.GetParent(path)?.FullName
            : Path.GetDirectoryName(path);
        var itemName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(parentPath) || string.IsNullOrWhiteSpace(itemName))
            return false;

        object? shell = null;
        object? windows = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null)
                return false;

            shell = Activator.CreateInstance(shellType);
            if (shell is null)
                return false;

            windows = ((dynamic)shell).Windows();
            dynamic dynamicWindows = windows;
            var count = (int)dynamicWindows.Count;
            for (var index = 0; index < count; index++)
            {
                object? explorer = null;
                object? document = null;
                object? folder = null;
                object? folderSelf = null;
                object? item = null;
                try
                {
                    explorer = dynamicWindows.Item(index);
                    if (explorer is null)
                        continue;

                    dynamic dynamicExplorer = explorer;
                    if (Convert.ToInt64(dynamicExplorer.HWND) != _originExplorerWindow.ToInt64())
                        continue;

                    document = dynamicExplorer.Document;
                    folder = ((dynamic)document).Folder;
                    folderSelf = ((dynamic)folder).Self;
                    var openFolderPath = (string?)((dynamic)folderSelf).Path;
                    if (!PathsEqual(openFolderPath, parentPath))
                        return false;

                    item = ((dynamic)folder).ParseName(itemName);
                    if (item is null)
                        return false;

                    ((dynamic)document).SelectItem(item, SelectItemFlags);
                    _ = SetForegroundWindow(_originExplorerWindow);
                    return true;
                }
                catch (COMException)
                {
                    return false;
                }
                finally
                {
                    ReleaseCom(item);
                    ReleaseCom(folderSelf);
                    ReleaseCom(folder);
                    ReleaseCom(document);
                    ReleaseCom(explorer);
                }
            }
        }
        catch (COMException)
        {
            return false;
        }
        finally
        {
            ReleaseCom(windows);
            ReleaseCom(shell);
        }

        return false;
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
            Marshal.FinalReleaseComObject(value);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr window);
}
