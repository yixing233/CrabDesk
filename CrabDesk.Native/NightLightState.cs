using Microsoft.Win32;

namespace CrabDesk.Native;

/// <summary>
/// Night light on/off through its CloudStore state blob.
///
/// Windows has no API for this. The setting lives in a binary blob under the
/// per-user CloudStore, and what the shell reads is whether the byte at offset
/// 18 has bit 0x02 set. Enabling inserts two bytes; disabling removes them,
/// which is why the blob's length changes rather than a single flag flipping.
///
/// This is undocumented and tied to the current shell data format. Every failure
/// path returns null or false, so a Windows update that changes the layout makes
/// the control read "unavailable" rather than misbehave.
/// </summary>
internal static class NightLightState
{
    private const string Key =
        @"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current\default$windows.data.bluelightreduction.bluelightreductionstate";
    private const string SettingsKey =
        @"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current\default$windows.data.bluelightreduction.settings";

    private const int FlagOffset = 18;
    private const byte EnabledFlag = 0x02;

    /// <summary>Current state, or null when the blob is absent or unreadable.</summary>
    public static bool? Read()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key);
            if (key is null) return null;
            if (key.GetValue("Data") is not byte[] data) return false;
            // A machine that has never enabled night light has no usable blob;
            // that is a known "off", not an unknown.
            if (data.Length <= FlagOffset) return false;
            return (data[FlagOffset] & EnabledFlag) != 0;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Turns night light on or off. Returns false when it cannot be done.</summary>
    public static bool Write(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key, writable: true);
            if (key is null) return false;
            var data = key.GetValue("Data") as byte[];
            // The blob only exists once night light has been switched on at least
            // once. A machine that has never used it therefore has nothing to
            // modify, and no documented shape to fabricate: report failure rather
            // than write a guess into the shell's state store.
            if (data is null || data.Length <= FlagOffset + 1) return false;
            var current = (data[FlagOffset] & EnabledFlag) != 0;
            if (current == enabled) return true;

            var updated = enabled ? Enable(data) : Disable(data);
            if (updated is null) return false;
            key.SetValue("Data", updated, RegistryValueKind.Binary);
            // The shell caches the state; without this the toggle reads back the
            // old value until the next sign-in.
            Broadcast();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Sets the flag by inserting the two-byte sequence the shell expects.</summary>
    private static byte[]? Enable(byte[] data)
    {
        if (data.Length < FlagOffset + 2) return null;
        var updated = new byte[data.Length + 2];
        Array.Copy(data, 0, updated, 0, FlagOffset);
        updated[FlagOffset] = EnabledFlag;
        updated[FlagOffset + 1] = 0x01;
        Array.Copy(data, FlagOffset, updated, FlagOffset + 2, data.Length - FlagOffset);
        return updated;
    }

    private static byte[]? Disable(byte[] data)
    {
        if (data.Length < FlagOffset + 2) return null;
        var updated = new byte[data.Length - 2];
        Array.Copy(data, 0, updated, 0, FlagOffset);
        Array.Copy(data, FlagOffset + 2, updated, FlagOffset, data.Length - FlagOffset - 2);
        return updated;
    }

    /// <summary>Nudges the shell into re-reading the setting.</summary>
    private static void Broadcast()
    {
        // Toggling the settings key's timestamp is the signal the shell watches;
        // the open/close alone is enough to raise the change notification.
        try { Registry.CurrentUser.OpenSubKey(SettingsKey, writable: true)?.Dispose(); }
        catch (Exception) { /* the state is already written */ }
    }
}
