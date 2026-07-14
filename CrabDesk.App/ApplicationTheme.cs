using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using CrabDesk.Core;
using Microsoft.Win32;

namespace CrabDesk.App;

internal static class ApplicationTheme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    internal static bool ResolveIsDark(ApplicationThemeMode mode) => mode switch
    {
        ApplicationThemeMode.Light => false,
        ApplicationThemeMode.Dark => true,
        _ => IsSystemDark()
    };

    internal static void ApplyResources(bool isDark)
    {
        var resources = System.Windows.Application.Current.Resources;
        var colors = isDark
            ? new Dictionary<string, string>
            {
                ["WindowBackground"] = "#FF15171A",
                ["PanelBackground"] = "#FF1D2024",
                ["SurfaceBackground"] = "#FF22262B",
                ["SurfaceBorder"] = "#FF343941",
                ["ControlBackground"] = "#FF292E34",
                ["ControlHoverBackground"] = "#FF333941",
                ["ControlPressedBackground"] = "#FF20242A",
                ["ControlBorder"] = "#FF414851",
                ["ControlHoverBorder"] = "#FF5A6570",
                ["InputBackground"] = "#FF20242A",
                ["TextBrush"] = "#FFF4F6F8",
                ["MutedTextBrush"] = "#FFA9B0B9",
                ["CheckMarkBrush"] = "#FFFFFFFF",
                ["TabSelectedBackground"] = "#FF29323A",
                ["AccentBrush"] = "#FF4FAED2",
                ["AccentHoverBrush"] = "#FF5AB6D8",
                ["AccentPressedBrush"] = "#FF398FAF",
                ["AccentSoftBrush"] = "#303F8EAC",
                ["DangerBrush"] = "#FFFF756B",
                ["SuccessBrush"] = "#FF62B985"
            }
            : new Dictionary<string, string>
            {
                ["WindowBackground"] = "#FFF4F6F8",
                ["PanelBackground"] = "#FFFFFFFF",
                ["SurfaceBackground"] = "#FFF8FAFB",
                ["SurfaceBorder"] = "#FFD9DEE4",
                ["ControlBackground"] = "#FFF0F3F5",
                ["ControlHoverBackground"] = "#FFE5EAEE",
                ["ControlPressedBackground"] = "#FFDCE2E7",
                ["ControlBorder"] = "#FFCDD4DB",
                ["ControlHoverBorder"] = "#FFA8B2BC",
                ["InputBackground"] = "#FFFFFFFF",
                ["TextBrush"] = "#FF20242A",
                ["MutedTextBrush"] = "#FF626B76",
                ["CheckMarkBrush"] = "#FF20242A",
                ["TabSelectedBackground"] = "#FFE7F2F7",
                ["AccentBrush"] = "#FF318CB2",
                ["AccentHoverBrush"] = "#FF277EA2",
                ["AccentPressedBrush"] = "#FF206C8B",
                ["AccentSoftBrush"] = "#18318CB2",
                ["DangerBrush"] = "#FFC7473F",
                ["SuccessBrush"] = "#FF39855A"
            };

        foreach (var (key, value) in colors)
        {
            resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        }
    }

    internal static void ApplyWindowChrome(Window window, bool isDark)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var enabled = isDark ? 1 : 0;
        if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
        {
            DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
        }
    }

    internal static bool TryGetWindowChromeDarkState(Window window, out bool isDark)
    {
        isDark = false;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return false;
        }
        var enabled = 0;
        var result = DwmGetWindowAttribute(handle, 20, out enabled, sizeof(int));
        if (result != 0)
        {
            result = DwmGetWindowAttribute(handle, 19, out enabled, sizeof(int));
        }
        isDark = enabled != 0;
        return result == 0;
    }

    private static bool IsSystemDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        return Convert.ToInt32(key?.GetValue("AppsUseLightTheme", 1) ?? 1) == 0;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int valueSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int valueSize);
}
