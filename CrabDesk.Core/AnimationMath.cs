namespace CrabDesk.Core;

public static class AnimationMath
{
    public static double EaseOutCubic(double progress)
    {
        progress = Math.Clamp(progress, 0, 1);
        var remaining = 1 - progress;
        return 1 - remaining * remaining * remaining;
    }

    public static double Interpolate(double from, double to, double progress) =>
        from + (to - from) * EaseOutCubic(progress);
}
