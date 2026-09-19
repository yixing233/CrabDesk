using Microsoft.UI.Xaml;

namespace CrabDesk.WinUI.Windows;

// WinUI windows do not inherit the exe's ApplicationIcon, so every window that
// appears in the taskbar or Alt+Tab has to set its own icon explicitly.
internal static class WindowIcon
{
    public static void Apply(Window window)
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "CrabDesk.ico");
        if (File.Exists(iconPath))
        {
            window.AppWindow.SetIcon(iconPath);
        }
    }
}
