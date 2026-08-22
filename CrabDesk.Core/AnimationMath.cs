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

    public static TimeSpan ScaleDurationByDistance(
        double remainingDistance,
        double fullDistance,
        TimeSpan fullDuration,
        TimeSpan minimumDuration)
    {
        if (fullDuration <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        if (fullDistance <= 0 || remainingDistance <= 0)
        {
            return minimumDuration <= fullDuration ? minimumDuration : fullDuration;
        }

        var ratio = Math.Clamp(remainingDistance / fullDistance, 0, 1);
        var milliseconds = Math.Max(
            minimumDuration.TotalMilliseconds,
            fullDuration.TotalMilliseconds * ratio);
        return TimeSpan.FromMilliseconds(Math.Min(
            fullDuration.TotalMilliseconds,
            milliseconds));
    }
}
