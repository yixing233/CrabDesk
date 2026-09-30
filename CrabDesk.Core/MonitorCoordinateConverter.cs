namespace CrabDesk.Core;

public static class MonitorCoordinateConverter
{
    /// <summary>
    /// Converts a monitor work area to coordinates relative to that monitor's
    /// desktop-child surface.
    /// </summary>
    public static LayoutRect GetMonitorRelativeWorkArea(MonitorLayout monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);

        return PixelsToDips(
            new LayoutRect(
                monitor.PixelWorkArea.X - monitor.PixelBounds.X,
                monitor.PixelWorkArea.Y - monitor.PixelBounds.Y,
                monitor.PixelWorkArea.Width,
                monitor.PixelWorkArea.Height),
            monitor.DpiScale);
    }

    public static LayoutRect PixelsToDips(LayoutRect pixels, double dpiScale)
    {
        var scale = NormalizeScale(dpiScale);
        return new LayoutRect(
            pixels.X / scale,
            pixels.Y / scale,
            pixels.Width / scale,
            pixels.Height / scale);
    }

    public static bool TryGetWorkAreaDip(
        MonitorLayout monitor,
        int screenPixelX,
        int screenPixelY,
        out double x,
        out double y)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        if (screenPixelX < monitor.PixelBounds.X ||
            screenPixelX >= monitor.PixelBounds.X + monitor.PixelBounds.Width ||
            screenPixelY < monitor.PixelBounds.Y ||
            screenPixelY >= monitor.PixelBounds.Y + monitor.PixelBounds.Height)
        {
            x = 0;
            y = 0;
            return false;
        }

        var scale = NormalizeScale(monitor.DpiScale);
        x = (screenPixelX - monitor.PixelWorkArea.X) / scale;
        y = (screenPixelY - monitor.PixelWorkArea.Y) / scale;
        return true;
    }

    public static LayoutRect DipsToPixels(LayoutRect dips, double dpiScale)
    {
        var scale = NormalizeScale(dpiScale);
        return new LayoutRect(
            dips.X * scale,
            dips.Y * scale,
            dips.Width * scale,
            dips.Height * scale);
    }

    private static double NormalizeScale(double dpiScale) =>
        double.IsFinite(dpiScale) && dpiScale > 0 ? dpiScale : 1;
}
