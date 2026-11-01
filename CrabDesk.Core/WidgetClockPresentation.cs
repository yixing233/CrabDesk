namespace CrabDesk.Core;

public static class WidgetClockPresentation
{
    public static bool IsLandscape(double width, double height) => width > height * 1.4;

    public static LayoutRect Face(double width, double height)
    {
        var landscape = IsLandscape(width, height);
        var diameter = Math.Max(0, Math.Min(width, height - (landscape ? 4 : 26)));
        return new(landscape ? 2 : (width - diameter) / 2, 2, diameter, diameter);
    }

    public static (double Hour, double Minute, double Second) Hands(DateTime time)
    {
        var minutes = time.Minute + time.Second / 60d;
        return ((time.Hour % 12 + minutes / 60) * 30, minutes * 6, time.Second * 6);
    }

    public static (double X, double Y) PointOnFace(LayoutRect face, double degrees, double radiusFraction)
    {
        var radians = degrees * Math.PI / 180;
        var radius = face.Width / 2 * radiusFraction;
        return (face.X + face.Width / 2 + Math.Sin(radians) * radius,
            face.Y + face.Height / 2 - Math.Cos(radians) * radius);
    }
}
