using System.Management;

namespace CrabDesk.Native;

/// <summary>
/// Reads and writes the panel brightness through the WMI monitor provider.
///
/// This is the only documented way to change brightness without a vendor SDK,
/// and it reaches the internal panel only: external monitors expose no such
/// interface, so a desktop reports null and the card hides the slider. Reading
/// and writing use two different classes — WmiMonitorBrightness for the value
/// and WmiMonitorBrightnessMethods for the setter.
/// </summary>
internal static class BrightnessControl
{
    private const string Scope = @"root\WMI";

    /// <summary>Current brightness percentage, or null when no panel is dimmable.</summary>
    public static double? Read()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(Scope, "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            foreach (ManagementObject item in searcher.Get())
            {
                using (item)
                {
                    if (item["CurrentBrightness"] is { } value && TryNumber(value, out var percent)) return percent;
                }
            }
        }
        catch (Exception)
        {
            // Provider missing, or access denied on a machine that restricts WMI.
        }
        return null;
    }

    /// <summary>Sets the panel brightness. Returns false when it cannot be written.</summary>
    public static bool Write(double percent)
    {
        try
        {
            var level = (byte)Math.Clamp(Math.Round(percent), 0, 100);
            using var searcher = new ManagementObjectSearcher(Scope, "SELECT * FROM WmiMonitorBrightnessMethods");
            foreach (ManagementObject item in searcher.Get())
            {
                using (item)
                {
                    using var parameters = item.GetMethodParameters("WmiSetBrightness");
                    parameters["Timeout"] = 0;
                    parameters["Brightness"] = level;
                    using var result = item.InvokeMethod("WmiSetBrightness", parameters, null);
                    // ReturnValue 0 means success; anything else means the panel
                    // refused, which is common for external displays.
                    if (result?["ReturnValue"] is { } code && TryNumber(code, out var status) && status == 0) return true;
                }
            }
        }
        catch (Exception)
        {
            // Fall through to false: the caller keeps the old value on screen.
        }
        return false;
    }

    private static bool TryNumber(object value, out double number)
    {
        try { number = Convert.ToDouble(value); return double.IsFinite(number); }
        catch (Exception) { number = 0; return false; }
    }
}
