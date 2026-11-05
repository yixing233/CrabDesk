using System.Management;
using System.Runtime.InteropServices;

namespace CrabDesk.Native;

/// <summary>
/// Reads and writes monitor brightness.
///
/// Two entirely different mechanisms are needed. Windows reports panel backlight
/// for the internal display through WMI, and nothing else; external monitors
/// expose brightness over DDC/CI, which WMI cannot reach. With a laptop lid shut
/// and two external screens, the WMI path returns the (dark) internal panel and
/// adjusting it does nothing visible - which is exactly the case that made
/// brightness look broken.
///
/// A matching WMI panel is preferred; external displays use their own DDC/CI
/// handle. Both paths stay bound to the selected display.
/// </summary>
internal static class BrightnessControl
{
    private const string Scope = @"root\WMI";

    public readonly record struct Reading(double? Percent, string Source);

    /// <summary>
    /// Current brightness. Prefer the selected panel's WMI backlight reading;
    /// some internal panels also answer DDC/CI with an unrelated fixed value.
    /// </summary>
    public static Reading Read(string displayId = "")
    {
        if (ReadWmi(displayId) is { } panel) return new(panel, "wmi");
        if (ReadDdc(displayId) is { } ddc) return new(ddc, "ddc");
        return new(null, "unavailable");
    }

    /// <summary>
    /// Sets brightness on whatever the read path found. Returns false when no
    /// monitor answered, so the caller keeps the old value on screen rather than
    /// showing a level that was never applied.
    /// </summary>
    public static bool Write(double percent, string displayId = "")
    {
        var level = (byte)Math.Clamp(Math.Round(percent), 0, 100);
        return ReadWmi(displayId) is not null ? WriteWmi(level, displayId) : WriteDdc(level, displayId);
    }

    private static bool WriteDdc(byte level, string displayId)
    {
        var wrote = false;
        ForEachMonitor(displayId, handle =>
        {
            if (wrote) return;
            if (!GetMonitorBrightness(handle, out var minimum, out _, out var maximum) || maximum <= minimum) return;
            var scaled = (uint)Math.Round(minimum + (maximum - minimum) * (level / 100d));
            // A DDC write returns before the panel has applied it, so success here
            // only means the request was accepted.
            if (SetMonitorBrightness(handle, scaled)) wrote = true;
        });
        return wrote;
    }

    private static double? ReadDdc(string displayId)
    {
        double? found = null;
        ForEachMonitor(displayId, handle =>
        {
            if (found is not null) return;
            if (!GetMonitorBrightness(handle, out var minimum, out var current, out var maximum) || maximum <= minimum) return;
            found = (current - minimum) * 100d / (maximum - minimum);
        });
        return found;
    }

    /// <summary>Enumerates every physical monitor and releases each handle.</summary>
    private static void ForEachMonitor(string displayId, Action<IntPtr> action)
    {
        var handles = new List<IntPtr>();
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
            {
                handles.Add(monitor);
                return true;
            }, IntPtr.Zero);
            foreach (var monitor in handles)
            {
                var info = new NativeMethods.MonitorInfoEx { Size = Marshal.SizeOf<NativeMethods.MonitorInfoEx>(), DeviceName = "" };
                if (!NativeMethods.GetMonitorInfo(monitor, ref info) ||
                    !string.IsNullOrEmpty(displayId) && !string.Equals(info.DeviceName, displayId, StringComparison.OrdinalIgnoreCase)) continue;
                if (!GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out var count) || count == 0) continue;
                var physical = new PHYSICAL_MONITOR[count];
                if (!GetPhysicalMonitorsFromHMONITOR(monitor, count, physical)) continue;
                foreach (var entry in physical)
                {
                    // A monitor without DDC/CI returns false for both get and set;
                    // the handle is harmless to close regardless.
                    try { action(entry.Handle); }
                    finally { DestroyPhysicalMonitor(entry.Handle); }
                }
            }
        }
        catch (Exception)
        {
            // A display change mid-enumeration ends the sweep; whatever was read
            // before that still stands.
        }
    }

    private static double? ReadWmi(string displayId)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(Scope, "SELECT CurrentBrightness, InstanceName FROM WmiMonitorBrightness");
            foreach (ManagementObject item in searcher.Get())
            {
                using (item)
                {
                    if (!MatchesDisplay(displayId, item["InstanceName"] as string)) continue;
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

    private static bool WriteWmi(byte level, string displayId)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(Scope, "SELECT * FROM WmiMonitorBrightnessMethods");
            foreach (ManagementObject item in searcher.Get())
            {
                using (item)
                {
                    if (!MatchesDisplay(displayId, item["InstanceName"] as string)) continue;
                    using var parameters = item.GetMethodParameters("WmiSetBrightness");
                    parameters["Timeout"] = 0;
                    parameters["Brightness"] = level;
                    using var result = item.InvokeMethod("WmiSetBrightness", parameters, null);
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

    // Bind WMI's panel instance to the selected logical display. A failed match
    // must not fall back to another screen (e.g. the laptop's closed lid).
    private static bool MatchesDisplay(string displayId, string? instance)
    {
        if (string.IsNullOrEmpty(displayId)) return true;
        if (!ActiveDisplayPaths.Read().TryGetValue(displayId, out var target)) return false;
        var parts = target.DevicePath.Replace(@"\\?\", "").Split('#');
        return parts.Length >= 3 && InstanceMatches(instance, string.Join("\\", parts.Take(3)));
    }

    internal static bool InstanceMatches(string? instance, string deviceInstance) =>
        !string.IsNullOrEmpty(deviceInstance) && instance is not null &&
        (instance.Equals(deviceInstance, StringComparison.OrdinalIgnoreCase) || instance.StartsWith(deviceInstance + "_", StringComparison.OrdinalIgnoreCase));

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr Handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr deviceContext, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr deviceContext, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("Dxva2.dll", SetLastError = true)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, out uint count);

    [DllImport("Dxva2.dll", SetLastError = true)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr monitor, uint count,
        [Out] PHYSICAL_MONITOR[] monitors);

    [DllImport("Dxva2.dll", SetLastError = true)]
    private static extern bool GetMonitorBrightness(IntPtr monitor, out uint minimum, out uint current, out uint maximum);

    [DllImport("Dxva2.dll", SetLastError = true)]
    private static extern bool SetMonitorBrightness(IntPtr monitor, uint value);

    [DllImport("Dxva2.dll", SetLastError = true)]
    private static extern bool DestroyPhysicalMonitor(IntPtr monitor);
}
