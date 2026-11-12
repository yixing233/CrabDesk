using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace CrabDesk.Native;

/// <summary>Reads and applies Windows' system and application color modes.</summary>
internal static class SystemThemeState
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    internal const string SystemValue = "SystemUsesLightTheme";
    internal const string AppsValue = "AppsUseLightTheme";

    public static bool? Read()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return Read(key);
        }
        catch (Exception) { return null; }
    }

    internal static bool? Read(RegistryKey? key) => key?.GetValue(SystemValue, 1) switch
    {
        null or 1 => false,
        0 => true,
        _ => null
    };

    public static bool Write(bool dark)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(PersonalizeKey);
            return Write(key, dark, Broadcast);
        }
        catch (Exception) { return false; }
    }

    internal static bool Write(RegistryKey key, bool dark, Action notify)
    {
        try
        {
            var light = dark ? 0 : 1;
            key.SetValue(SystemValue, light, RegistryValueKind.DWord);
            key.SetValue(AppsValue, light, RegistryValueKind.DWord);
            notify();
            return Equals(key.GetValue(SystemValue), light) && Equals(key.GetValue(AppsValue), light);
        }
        catch (Exception) { return false; }
    }

    private static void Broadcast()
    {
        var setting = Marshal.StringToHGlobalUni("ImmersiveColorSet");
        try
        {
            // Called on the control worker. Bound waits for unresponsive apps;
            // the registry read-back determines whether the setting was applied.
            _ = NativeMethods.SendMessageTimeout(new IntPtr(0xFFFF), 0x001A, IntPtr.Zero,
                setting, NativeMethods.SmtoAbortIfHung, 1000, out _);
        }
        finally { Marshal.FreeHGlobal(setting); }
    }
}
