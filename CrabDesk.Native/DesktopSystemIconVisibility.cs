using Microsoft.Win32;

namespace CrabDesk.Native;

/// <summary>
/// Resolves the visibility of the standard desktop namespace icons from the
/// same Explorer settings used by the Desktop Icon Settings dialog.
/// </summary>
public static class DesktopSystemIconVisibility
{
    private const string NewStartPanelPath =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel";
    private const string ClassicStartMenuPath =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\ClassicStartMenu";

    public static bool IsVisible(string clsid)
    {
        if (string.IsNullOrWhiteSpace(clsid) || !OperatingSystem.IsWindows())
        {
            return true;
        }

        var userSetting = ReadFirstSetting(Registry.CurrentUser, clsid);
        var machineSetting = ReadFirstSetting(Registry.LocalMachine, clsid);
        return ResolveIsVisible(userSetting, machineSetting);
    }

    /// <summary>
    /// Explorer stores zero for visible and a non-zero value for hidden.
    /// A per-user value overrides the machine default; an absent value uses
    /// Explorer's default-visible behavior (used by Recycle Bin).
    /// </summary>
    public static bool ResolveIsVisible(int? userSetting, int? machineSetting) =>
        (userSetting ?? machineSetting ?? 0) == 0;

    private static int? ReadFirstSetting(RegistryKey root, string clsid) =>
        ReadSetting(root, NewStartPanelPath, clsid) ??
        ReadSetting(root, ClassicStartMenuPath, clsid);

    private static int? ReadSetting(RegistryKey root, string path, string clsid)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            return key?.GetValue(clsid) switch
            {
                int value => value,
                byte value => value,
                short value => value,
                long value when value is >= int.MinValue and <= int.MaxValue => (int)value,
                string value when int.TryParse(value, out var parsed) => parsed,
                _ => null
            };
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (System.Security.SecurityException)
        {
            return null;
        }
    }
}
