namespace CrabDesk.Core;

public static class MonitorCoordinateConverter
{
    public static LayoutRect PixelsToDips(LayoutRect pixels, double dpiScale)
    {
        var scale = NormalizeScale(dpiScale);
        return new LayoutRect(
            pixels.X / scale,
            pixels.Y / scale,
            pixels.Width / scale,
            pixels.Height / scale);
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
