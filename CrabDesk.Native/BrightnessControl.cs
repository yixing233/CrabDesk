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
/// DDC/CI is therefore tried first and WMI only if no external monitor answers.
/// </summary>
internal static class BrightnessControl
{
    private const string Scope = @"root\WMI";

    public readonly record struct Reading(double? Percent, string Source);

    /// <summary>
    /// Current brightness. DDC/CI monitors are preferred; the ACPI panel is the
    /// fallback for machines with no external display.
    /// </summary>
    public static Reading Read()
    {
        if (ReadDdc() is { } ddc) return new(ddc, "ddc");
        return new(ReadWmi(), "wmi");
    }

    /// <summary>
    /// Sets brightness on whatever the read path found. Returns false when no
    /// monitor answered, so the caller keeps the old value on screen rather than
    /// showing a level that was never applied.
    /// </summary>
    public static bool Write(double percent)
    {
        var level = (byte)Math.Clamp(Math.Round(percent), 0, 100);
        if (WriteDdc(level)) return true;
        return WriteWmi(level);
    }

    private static bool WriteDdc(byte level)
    {
        var wrote = false;
        ForEachMonitor(handle =>
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

    private static double? ReadDdc()
    {
        double? found = null;
        ForEachMonitor(handle =>
        {
            if (found is not null) return;
            if (!GetMonitorBrightness(handle, out var minimum, out var current, out var maximum) || maximum <= minimum) return;
            found = (current - minimum) * 100d / (maximum - minimum);
        });
        return found;
    }

    /// <summary>Enumerates every physical monitor and releases each handle.</summary>
    private static void ForEachMonitor(Action<IntPtr> action)
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

    private static double? ReadWmi()
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

    private static bool WriteWmi(byte level)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(Scope, "SELECT * FROM WmiMonitorBrightnessMethods");
            foreach (ManagementObject item in searcher.Get())
            {
                using (item)
                {
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
