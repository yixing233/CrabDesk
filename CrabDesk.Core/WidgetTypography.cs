namespace CrabDesk.Core;

public static class WidgetTypography
{
    public static string NormalizeFamily(string? value) => string.IsNullOrWhiteSpace(value)
        ? BoxAppearance.DefaultFontFamily : value.Trim();

    public static double NormalizeScale(double value) => double.IsFinite(value) ? Math.Clamp(value, .75, 1.5) : 1;

    public static int NormalizeWeight(int value) => value is 400 or 700 ? value : 0;

    public static void Normalize(DesktopWidget widget)
    {
        widget.FontFamily = NormalizeFamily(widget.FontFamily);
        widget.FontScale = NormalizeScale(widget.FontScale);
        widget.FontWeight = NormalizeWeight(widget.FontWeight);
    }
}
