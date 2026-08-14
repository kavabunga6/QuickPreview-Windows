using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace QuickPreview;

internal static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "QuickPreview";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                        ?? throw new InvalidOperationException("Не удалось открыть настройки автозапуска Windows.");

        if (enabled)
            key.SetValue(ValueName, $"\"{GetExecutablePath()}\"", RegistryValueKind.String);
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    public static void NormalizeEnabledExecutablePath()
    {
        if (IsEnabled())
            SetEnabled(enabled: true);
    }

    private static string GetExecutablePath()
    {
        var processPath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
        if (!string.IsNullOrWhiteSpace(processPath) &&
            !string.Equals(Path.GetFileName(processPath), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
            return processPath;

        var appHostPath = Path.Combine(AppContext.BaseDirectory, "QuickPreview.exe");
        if (File.Exists(appHostPath))
            return appHostPath;

        throw new InvalidOperationException("Не удалось определить путь к QuickPreview.exe.");
    }
}
