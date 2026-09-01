namespace CrabDesk.Core;

/// <summary>
/// Calculates the temporary position of a box while it is being dragged.
/// Alignment is intentionally independent from persistence so the desktop
/// surface can apply it on every pointer frame without changing the layout
/// model until the drag is committed.
/// </summary>
public static class DesktopBoxAlignmentEngine
{
    public const double DefaultThreshold = 8;

    public static LayoutRect Align(
        LayoutRect moving,
        IEnumerable<LayoutRect> peers,
        LayoutRect workArea,
        double threshold = DefaultThreshold)
    {
        if (!IsValid(moving) || !IsValid(workArea) ||
            !double.IsFinite(threshold) || threshold < 0)
        {
            return moving;
        }

        var peerBounds = peers
            .Where(IsValid)
            .ToArray();
        var alignedX = AlignAxis(
            moving.X,
            moving.Width,
            peerBounds.SelectMany(GetHorizontalAnchors).Append(workArea.X).Append(workArea.X + workArea.Width),
            threshold);
        var alignedY = AlignAxis(
            moving.Y,
            moving.Height,
            peerBounds.SelectMany(GetVerticalAnchors).Append(workArea.Y).Append(workArea.Y + workArea.Height),
            threshold);
        var aligned = moving with { X = alignedX, Y = alignedY };

        // A peer close to a work-area edge can produce an alignment coordinate
        // just outside the area. Clamp without changing the box dimensions.
        return aligned with
        {
            X = ClampStart(aligned.X, aligned.Width, workArea.X, workArea.Width),
            Y = ClampStart(aligned.Y, aligned.Height, workArea.Y, workArea.Height)
        };
    }

    private static double AlignAxis(
        double movingStart,
        double movingSize,
        IEnumerable<double> targetAnchors,
        double threshold)
    {
        var movingAnchors = new[]
        {
            0,
            movingSize / 2,
            movingSize
        };
        var bestStart = movingStart;
        var bestDistance = threshold;
        foreach (var movingOffset in movingAnchors)
        {
            foreach (var target in targetAnchors)
            {
                var candidateStart = target - movingOffset;
                var distance = Math.Abs(candidateStart - movingStart);
                if (distance <= bestDistance)
                {
                    if (distance < bestDistance || candidateStart == movingStart)
                    {
                        bestDistance = distance;
                        bestStart = candidateStart;
                    }
                }
            }
        }

        return bestStart;
    }

    private static IEnumerable<double> GetHorizontalAnchors(LayoutRect bounds)
    {
        yield return bounds.X;
        yield return bounds.X + bounds.Width / 2;
        yield return bounds.X + bounds.Width;
    }

    private static IEnumerable<double> GetVerticalAnchors(LayoutRect bounds)
    {
        yield return bounds.Y;
        yield return bounds.Y + bounds.Height / 2;
        yield return bounds.Y + bounds.Height;
    }

    private static double ClampStart(
        double value,
        double size,
        double areaStart,
        double areaSize)
    {
        var max = areaStart + Math.Max(0, areaSize - size);
        return Math.Clamp(value, areaStart, Math.Max(areaStart, max));
    }

    private static bool IsValid(LayoutRect bounds) =>
        double.IsFinite(bounds.X) &&
        double.IsFinite(bounds.Y) &&
        double.IsFinite(bounds.Width) &&
        double.IsFinite(bounds.Height) &&
        bounds.Width > 0 &&
        bounds.Height > 0;
}
