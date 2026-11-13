using System.Drawing;
using System.Drawing.Drawing2D;
using CrabDesk.Core;

namespace CrabDesk.Runtime;

internal abstract partial class WidgetSurfaceController
{
    protected static Color ParseOpaqueColor(string value)
    {
        try
        {
            var hex = value.TrimStart('#');
            var offset = hex.Length == 8 ? 2 : 0;
            return Color.FromArgb(255,
                Convert.ToByte(hex.Substring(offset, 2), 16),
                Convert.ToByte(hex.Substring(offset + 2, 2), 16),
                Convert.ToByte(hex.Substring(offset + 4, 2), 16));
        }
        catch
        {
            return Color.FromArgb(40, 44, 50);
        }
    }

    internal static Color ResolveAccentColor(Color background, Color accent)
    {
        const double minimumContrast = 2.6d;
        if (ContrastRatio(background, accent) >= minimumContrast)
        {
            return Color.FromArgb(255, accent.R, accent.G, accent.B);
        }

        var target = UsesLightText(background)
            ? Color.White
            : Color.FromArgb(31, 35, 41);
        for (var step = 1; step <= 10; step++)
        {
            var amount = step / 10d;
            var adjusted = Color.FromArgb(
                255,
                (int)Math.Round(accent.R + (target.R - accent.R) * amount),
                (int)Math.Round(accent.G + (target.G - accent.G) * amount),
                (int)Math.Round(accent.B + (target.B - accent.B) * amount));
            if (ContrastRatio(background, adjusted) >= minimumContrast)
            {
                return adjusted;
            }
        }

        return target;
    }

    protected static Color ResolveAutoTextColor(Color background) => UsesLightText(background)
        ? Color.White
        : Color.FromArgb(31, 35, 41);

    protected static bool UsesLightText(Color background)
    {
        return ContrastRatio(background, Color.White) >=
            ContrastRatio(background, Color.FromArgb(31, 35, 41));
    }

    protected static double ContrastRatio(Color first, Color second)
    {
        var firstLuminance = RelativeLuminance(first);
        var secondLuminance = RelativeLuminance(second);
        return (Math.Max(firstLuminance, secondLuminance) + 0.05) /
            (Math.Min(firstLuminance, secondLuminance) + 0.05);
    }

    protected static double RelativeLuminance(Color color)
    {
        static double Linearize(byte channel)
        {
            var normalized = channel / 255d;
            return normalized <= 0.04045d
                ? normalized / 12.92d
                : Math.Pow((normalized + 0.055d) / 1.055d, 2.4d);
        }

        return 0.2126d * Linearize(color.R) +
            0.7152d * Linearize(color.G) +
            0.0722d * Linearize(color.B);
    }

    protected static Color ApplyOpacity(Color color, double opacity) =>
        Color.FromArgb((int)Math.Round(255 * Math.Clamp(opacity, 0, 1)), color.R, color.G, color.B);


    protected static GraphicsPath RoundedRectangle(RectangleF rectangle, float radius)
    {
        radius = Math.Min(Math.Max(0, radius), Math.Min(rectangle.Width, rectangle.Height) / 2);
        if (radius <= 0.1f)
        {
            var rectanglePath = new GraphicsPath();
            rectanglePath.AddRectangle(rectangle);
            return rectanglePath;
        }
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

}
