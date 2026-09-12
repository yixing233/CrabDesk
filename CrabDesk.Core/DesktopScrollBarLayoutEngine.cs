namespace CrabDesk.Core;

public sealed record VerticalScrollBarLayout(
    LayoutRect Track,
    LayoutRect Thumb,
    double ScrollOffset,
    double MaxScroll);

/// <summary>
/// Calculates a compact vertical scrollbar for a virtualized box viewport.
/// The result only carries geometry; input and drawing remain in the runtime.
/// </summary>
public static class DesktopScrollBarLayoutEngine
{
    public const double DefaultThickness = 4;
    public const double DefaultInset = 2;
    public const double DefaultMinimumThumbHeight = 28;

    public static VerticalScrollBarLayout? CalculateVertical(
        LayoutRect viewport,
        double requestedScroll,
        double maxScroll,
        double thickness = DefaultThickness,
        double inset = DefaultInset,
        double minimumThumbHeight = DefaultMinimumThumbHeight)
    {
        maxScroll = Math.Max(0, maxScroll);
        thickness = Math.Clamp(thickness, 2, 24);
        inset = Math.Max(0, inset);
        if (maxScroll <= 0 ||
            viewport.Width < thickness + inset * 2 ||
            viewport.Height <= inset * 2)
        {
            return null;
        }

        var trackHeight = viewport.Height - inset * 2;
        if (trackHeight <= 0)
        {
            return null;
        }

        var track = new LayoutRect(
            viewport.X + viewport.Width - thickness - inset,
            viewport.Y + inset,
            thickness,
            trackHeight);
        var contentHeight = viewport.Height + maxScroll;
        var proportionalThumbHeight = track.Height * viewport.Height / contentHeight;
        var minimumHeight = Math.Min(track.Height, Math.Max(1, minimumThumbHeight));
        var thumbHeight = Math.Clamp(proportionalThumbHeight, minimumHeight, track.Height);
        var scrollOffset = Math.Clamp(requestedScroll, 0, maxScroll);
        var travel = track.Height - thumbHeight;
        var thumbY = travel <= 0
            ? track.Y
            : track.Y + travel * (scrollOffset / maxScroll);
        var thumb = new LayoutRect(track.X, thumbY, track.Width, thumbHeight);
        return new VerticalScrollBarLayout(track, thumb, scrollOffset, maxScroll);
    }

    public static double GetScrollOffsetForThumbTop(
        VerticalScrollBarLayout layout,
        double requestedThumbTop)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (layout.MaxScroll <= 0)
        {
            return 0;
        }

        var travel = layout.Track.Height - layout.Thumb.Height;
        if (travel <= 0)
        {
            return 0;
        }

        var normalized = Math.Clamp(
            (requestedThumbTop - layout.Track.Y) / travel,
            0,
            1);
        return normalized * layout.MaxScroll;
    }
}
