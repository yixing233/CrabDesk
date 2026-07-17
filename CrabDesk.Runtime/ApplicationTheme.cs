using CrabDesk.Core;
using Microsoft.Win32;

namespace CrabDesk.Runtime;

internal static class ApplicationTheme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    internal static bool ResolveIsDark(ApplicationThemeMode mode) => mode switch
    {
        ApplicationThemeMode.Light => false,
        ApplicationThemeMode.Dark => true,
        _ => IsSystemDark()
    };

    private static bool IsSystemDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        return Convert.ToInt32(key?.GetValue("AppsUseLightTheme", 1) ?? 1) == 0;
    }
}
