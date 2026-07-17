namespace CrabDesk.Core;

public static class LayoutGrid
{
    public const double DefaultStep = 4;

    public static double Snap(double value, double step = DefaultStep)
    {
        if (step <= 0 || double.IsNaN(value) || double.IsInfinity(value))
        {
            return value;
        }
        return Math.Round(value / step, MidpointRounding.AwayFromZero) * step;
    }

    public static double SnapUp(double value, double step = DefaultStep)
    {
        if (step <= 0 || double.IsNaN(value) || double.IsInfinity(value))
        {
            return value;
        }
        return Math.Ceiling(value / step) * step;
    }
}
