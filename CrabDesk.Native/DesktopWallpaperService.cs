using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace CrabDesk.Native;

public static class DesktopWallpaperService
{
    private const uint SpiGetDeskWallpaper = 0x0073;
    private const uint SpiSetDeskWallpaper = 0x0014;
    private const uint SpifUpdateIniFile = 0x0001;
    private const uint SpifSendChange = 0x0002;

    public static string GetCurrentWallpaperPath()
    {
        var buffer = new char[260];
        if (SystemParametersInfo(
                SpiGetDeskWallpaper,
                (uint)buffer.Length,
                buffer,
                0) &&
            !string.IsNullOrWhiteSpace(new string(buffer).TrimEnd('\0')))
        {
            return new string(buffer).TrimEnd('\0');
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            return key?.GetValue("WallPaper") as string ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public static bool SetWallpaper(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        return SystemParametersInfo(
            SpiSetDeskWallpaper,
            0,
            path,
            SpifUpdateIniFile | SpifSendChange);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(
        uint action,
        uint parameter,
        [Out] char[]? value,
        uint winIni);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(
        uint action,
        uint parameter,
        string value,
        uint winIni);
}
