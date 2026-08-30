using CrabDesk.Core;

namespace CrabDesk.Tests;

public sealed class DesktopScrollBarLayoutTests
{
    [Fact]
    public void CalculateVerticalReturnsNullWhenContentDoesNotOverflow()
    {
        var layout = DesktopScrollBarLayoutEngine.CalculateVertical(
            new LayoutRect(20, 40, 240, 180),
            requestedScroll: 0,
            maxScroll: 0);

        Assert.Null(layout);
    }

    [Fact]
    public void ScrollOffsetForThumbTopMapsBothTrackEnds()
    {
        var layout = Assert.IsType<VerticalScrollBarLayout>(
            DesktopScrollBarLayoutEngine.CalculateVertical(
                new LayoutRect(20, 40, 240, 180),
                requestedScroll: 160,
                maxScroll: 720));

        Assert.Equal(0, DesktopScrollBarLayoutEngine.GetScrollOffsetForThumbTop(
            layout,
            layout.Track.Y));
        Assert.Equal(layout.MaxScroll, DesktopScrollBarLayoutEngine.GetScrollOffsetForThumbTop(
            layout,
            layout.Track.Y + layout.Track.Height - layout.Thumb.Height));
    }

    [Fact]
    public void CalculateVerticalClampsTheRequestedOffsetAndKeepsThumbInsideTrack()
    {
        var layout = Assert.IsType<VerticalScrollBarLayout>(
            DesktopScrollBarLayoutEngine.CalculateVertical(
                new LayoutRect(20, 40, 240, 180),
                requestedScroll: 9_999,
                maxScroll: 720));

        Assert.Equal(720, layout.ScrollOffset);
        Assert.True(layout.Thumb.Y >= layout.Track.Y);
        Assert.True(layout.Thumb.Y + layout.Thumb.Height <= layout.Track.Y + layout.Track.Height);
    }

    [Fact]
    public void DefaultScrollbarIsNarrowAndSitsCloseToTheViewportEdge()
    {
        var layout = Assert.IsType<VerticalScrollBarLayout>(
            DesktopScrollBarLayoutEngine.CalculateVertical(
                new LayoutRect(20, 40, 240, 180),
                requestedScroll: 0,
                maxScroll: 720));

        Assert.Equal(4, layout.Track.Width);
        Assert.Equal(2, (20 + 240) - (layout.Track.X + layout.Track.Width));
        Assert.Equal(2, layout.Track.Y - 40);
    }
}
