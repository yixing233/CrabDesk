namespace CrabDesk.Core;

public static class AnimationMath
{
    /// <summary>
    /// Decelerating ease for box height changes. It starts at about 1.6x the
    /// average speed and settles at zero.
    /// <para>
    /// An <c>EaseOutCubic</c> starts at 3x instead, which puts a quarter of the
    /// whole travel into the very first frame — the box appeared to jump and
    /// then crawl rather than glide. The cubic tail is also wasteful: it has
    /// covered 99.9% of the distance with a tenth of the time left, so those
    /// frames are spent on motion nobody can see. This curve gives the first
    /// frame an ordinary step and still lands softly.
    /// </para>
    /// </summary>
    public static double EaseOutSine(double progress) =>
        Math.Sin(Math.Clamp(progress, 0, 1) * Math.PI / 2);

    public static double Interpolate(double from, double to, double progress) =>
        from + (to - from) * EaseOutSine(progress);

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
