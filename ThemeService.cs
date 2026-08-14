using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Color = System.Windows.Media.Color;

namespace QuickPreview;

internal sealed class ThemeService : IDisposable
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmWindowCornerPreference = 33;

    private bool _isLightTheme;

    public void Start()
    {
        SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
        ApplyTheme();
    }

    public void RegisterWindow(Window window)
    {
        window.SourceInitialized += (_, _) => ApplyNativeWindowTheme(window);
    }

    private void SystemEvents_UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (System.Windows.Application.Current is not { } application)
            return;

        application.Dispatcher.BeginInvoke(new Action(ApplyTheme));
    }

    private void ApplyTheme()
    {
        _isLightTheme = ReadIsLightTheme();
        var resources = System.Windows.Application.Current.Resources;
        var palette = _isLightTheme ? LightPalette : DarkPalette;

        foreach (var (key, value) in palette)
            resources[key] = CreateBrush(value);

        var accent = ReadAccentColor();
        resources["AccentBrush"] = CreateBrush(accent);
        resources["AccentSoftBrush"] = CreateBrush(Color.FromArgb(_isLightTheme ? (byte)34 : (byte)52,
            accent.R, accent.G, accent.B));
        resources["AccentTextBrush"] = CreateBrush(GetContrastingTextColor(accent));

        foreach (Window window in System.Windows.Application.Current.Windows)
            ApplyNativeWindowTheme(window);
    }

    private static bool ReadIsLightTheme()
    {
        var testOverride = Environment.GetEnvironmentVariable("QUICKPREVIEW_TEST_THEME");
        if (string.Equals(testOverride, "light", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(testOverride, "dark", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
        }
        catch
        {
            return false;
        }
    }

    private static Color ReadAccentColor()
    {
        var color = SystemParameters.WindowGlassColor;
        if (color.A == 0 || Math.Abs(color.R - color.G) < 8 && Math.Abs(color.G - color.B) < 8)
            return Color.FromRgb(0, 103, 192);
        return Color.FromRgb(color.R, color.G, color.B);
    }

    private static Color GetContrastingTextColor(Color color)
    {
        var luminance = (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255;
        return luminance > 0.62 ? Color.FromRgb(20, 20, 22) : Colors.White;
    }

    private void ApplyNativeWindowTheme(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return;

        var useDarkMode = _isLightTheme ? 0 : 1;
        if (DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref useDarkMode, sizeof(int)) != 0)
            _ = DwmSetWindowAttribute(handle, DwmUseImmersiveDarkModeBefore20H1, ref useDarkMode, sizeof(int));

        var roundedCorners = 2;
        _ = DwmSetWindowAttribute(handle, DwmWindowCornerPreference, ref roundedCorners, sizeof(int));
    }

    private static SolidColorBrush CreateBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static readonly Dictionary<string, Color> DarkPalette = new()
    {
        ["WindowBackgroundBrush"] = Color.FromArgb(248, 24, 25, 29),
        ["HeaderBackgroundBrush"] = Color.FromArgb(246, 29, 30, 35),
        ["PreviewBackgroundBrush"] = Color.FromRgb(20, 21, 25),
        ["ImageBackgroundBrush"] = Color.FromRgb(12, 13, 16),
        ["CardBackgroundBrush"] = Color.FromRgb(35, 37, 43),
        ["PlayerBarBrush"] = Color.FromArgb(238, 42, 44, 51),
        ["CardBorderBrush"] = Color.FromRgb(58, 61, 70),
        ["TextPrimaryBrush"] = Color.FromRgb(247, 247, 248),
        ["TextSecondaryBrush"] = Color.FromRgb(169, 174, 184),
        ["ControlFillBrush"] = Color.FromRgb(43, 45, 52),
        ["ControlHoverBrush"] = Color.FromRgb(56, 59, 68),
        ["ControlPressedBrush"] = Color.FromRgb(69, 73, 82),
        ["ControlStrokeBrush"] = Color.FromRgb(66, 69, 78),
        ["TrackBrush"] = Color.FromRgb(91, 96, 106)
    };

    private static readonly Dictionary<string, Color> LightPalette = new()
    {
        ["WindowBackgroundBrush"] = Color.FromArgb(250, 247, 247, 249),
        ["HeaderBackgroundBrush"] = Color.FromArgb(248, 250, 250, 251),
        ["PreviewBackgroundBrush"] = Color.FromRgb(239, 240, 243),
        ["ImageBackgroundBrush"] = Color.FromRgb(230, 232, 235),
        ["CardBackgroundBrush"] = Color.FromRgb(255, 255, 255),
        ["PlayerBarBrush"] = Color.FromArgb(242, 224, 226, 230),
        ["CardBorderBrush"] = Color.FromRgb(208, 211, 217),
        ["TextPrimaryBrush"] = Color.FromRgb(27, 28, 31),
        ["TextSecondaryBrush"] = Color.FromRgb(94, 98, 106),
        ["ControlFillBrush"] = Color.FromRgb(235, 236, 239),
        ["ControlHoverBrush"] = Color.FromRgb(222, 224, 228),
        ["ControlPressedBrush"] = Color.FromRgb(210, 213, 218),
        ["ControlStrokeBrush"] = Color.FromRgb(207, 210, 216),
        ["TrackBrush"] = Color.FromRgb(183, 188, 196)
    };

    public void Dispose()
    {
        SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int valueSize);
}
