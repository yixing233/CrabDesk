using CrabDesk.Core;

namespace CrabDesk.Native;

public sealed class MonitorTopologyService : IMonitorTopologyService
{
    public IReadOnlyList<MonitorLayout> GetMonitors()
    {
        var monitors = new List<MonitorLayout>();
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr hdc, ref NativeMethods.Rect monitorRect, IntPtr data) =>
        {
            var info = new NativeMethods.MonitorInfoEx
            {
                Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfoEx>(),
                DeviceName = string.Empty
            };
            if (!NativeMethods.GetMonitorInfo(monitor, ref info))
            {
                return true;
            }

            var dpi = GetDpi(monitor);
            var scale = dpi / 96d;
            var pixelBounds = ToRect(info.Monitor);
            var pixelWorkArea = ToRect(info.WorkArea);
            monitors.Add(new MonitorLayout
            {
                Id = info.DeviceName,
                DeviceName = info.DeviceName,
                PixelBounds = pixelBounds,
                PixelWorkArea = pixelWorkArea,
                Bounds = MonitorCoordinateConverter.PixelsToDips(pixelBounds, scale),
                WorkArea = MonitorCoordinateConverter.PixelsToDips(pixelWorkArea, scale),
                DpiScale = scale,
                IsPrimary = (info.Flags & 1) != 0
            });
            return true;
        }, IntPtr.Zero);
        return monitors;
    }

    private static uint GetDpi(IntPtr monitor)
    {
        try
        {
            return NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MonitorDpiType.Effective, out var dpiX, out _) == 0
                ? dpiX
                : 96;
        }
        catch (DllNotFoundException)
        {
            return 96;
        }
        catch (EntryPointNotFoundException)
        {
            return 96;
        }
    }

    private static LayoutRect ToRect(NativeMethods.Rect rect) =>
        new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);

}
