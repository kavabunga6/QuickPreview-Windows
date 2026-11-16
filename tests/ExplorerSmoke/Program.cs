using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using QuickPreview;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "selection-fixtures");
        Directory.CreateDirectory(fixture);
        var files = new[] { "a-oldest.txt", "b-newest.txt", "c-middle.txt" };
        var dates = new[] { DateTime.Now.AddDays(-3), DateTime.Now.AddDays(-1), DateTime.Now.AddDays(-2) };
        for (var i = 0; i < files.Length; i++)
        {
            var path = Path.Combine(fixture, files[i]);
            File.WriteAllText(path, files[i]);
            File.SetLastWriteTime(path, dates[i]);
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"/n,\"{fixture}\"") { UseShellExecute = true });
        object? shell = null;
        object? windows = null;
        object? testWindow = null;
        object? document = null;
        object? folder = null;
        object? selected = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
            windows = ((dynamic)shell!).Windows();
            var timeout = Stopwatch.StartNew();
            while (testWindow is null && timeout.Elapsed < TimeSpan.FromSeconds(10))
            {
                for (var index = 0; index < (int)((dynamic)windows).Count; index++)
                {
                    object? candidate = null;
                    object? doc = null;
                    object? dir = null;
                    object? self = null;
                    try
                    {
                        candidate = ((dynamic)windows).Item(index);
                        doc = ((dynamic)candidate!).Document;
                        dir = ((dynamic)doc).Folder;
                        self = ((dynamic)dir).Self;
                        if (string.Equals((string)((dynamic)self).Path, fixture, StringComparison.OrdinalIgnoreCase))
                        {
                            testWindow = candidate;
                            candidate = null;
                            break;
                        }
                    }
                    catch (COMException) { }
                    finally { Release(self); Release(dir); Release(doc); Release(candidate); }
                }
                if (testWindow is null) Thread.Sleep(100);
            }

            if (testWindow is null) throw new Exception("Fixture Explorer window not found.");
            document = ((dynamic)testWindow).Document;
            ((dynamic)document).CurrentViewMode = 4;
            ((dynamic)document).SortColumns = "prop:-System.DateModified;";
            Thread.Sleep(300);
            folder = ((dynamic)document).Folder;
            selected = ((dynamic)folder).ParseName("b-newest.txt");
            ((dynamic)document).SelectItem(selected, 0x1 | 0x4 | 0x8 | 0x10);
            var handle = new IntPtr(Convert.ToInt64(((dynamic)testWindow).HWND));
            typeof(ExplorerSelectionService).GetField("_originExplorerWindow", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, handle);
            AssertPath("initial focused selection", ExplorerSelectionService.TryGetSelectedPathInOriginWindow(), "b-newest.txt");
            AssertPath("next follows date descending, not alphabetical", ExplorerSelectionService.TryNavigateInOriginWindow(NavigationDirection.Down), "c-middle.txt");
            AssertPath("previous follows actual view", ExplorerSelectionService.TryNavigateInOriginWindow(NavigationDirection.Up), "b-newest.txt");
            AssertPath("first boundary stays first", ExplorerSelectionService.TryNavigateInOriginWindow(NavigationDirection.Up), "b-newest.txt");
            AssertPath("next again", ExplorerSelectionService.TryNavigateInOriginWindow(NavigationDirection.Down), "c-middle.txt");
            AssertPath("last follows actual view", ExplorerSelectionService.TryNavigateInOriginWindow(NavigationDirection.Down), "a-oldest.txt");
            AssertPath("last boundary stays last", ExplorerSelectionService.TryNavigateInOriginWindow(NavigationDirection.Down), "a-oldest.txt");
            Console.WriteLine("PASS: Explorer actual selection and view-order navigation, 7 assertions.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            ExplorerSelectionService.ForgetOriginWindow();
            if (testWindow is not null) { try { ((dynamic)testWindow).Quit(); } catch (COMException) { } }
            Release(selected); Release(folder); Release(document); Release(testWindow); Release(windows); Release(shell);
        }
    }

    private static void AssertPath(string label, string? actual, string expected)
    {
        if (Path.GetFileName(actual) != expected)
            throw new Exception($"{label}: expected {expected}, got {actual ?? "<null>"}");
        Console.WriteLine($"PASS: {label}: {expected}");
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }
}
