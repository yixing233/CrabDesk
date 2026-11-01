using CrabDesk.Core;

namespace CrabDesk.Tests;

public sealed class WidgetClockPresentationTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 90)]
    [InlineData(6, 180)]
    [InlineData(9, 270)]
    [InlineData(12, 0)]
    [InlineData(15, 90)]
    public void HourHandUsesTwelveHourDial(int hour, double angle)
    {
        var hands = WidgetClockPresentation.Hands(new(2026, 10, 31, hour, 0, 0));
        Assert.Equal(angle, hands.Hour, 6);
        Assert.Equal(0, hands.Minute);
        Assert.Equal(0, hands.Second);
    }

    [Fact]
    public void HandsIncludePartialHoursAndMinutes()
    {
        var hands = WidgetClockPresentation.Hands(new(2026, 10, 31, 3, 15, 30));
        Assert.Equal(97.75, hands.Hour, 6);
        Assert.Equal(93, hands.Minute, 6);
        Assert.Equal(180, hands.Second);
        var beforeMidnight = WidgetClockPresentation.Hands(new(2026, 10, 31, 23, 59, 59));
        Assert.InRange(beforeMidnight.Hour, 359.99, 360);
        Assert.Equal(359.9, beforeMidnight.Minute, 6);
        Assert.Equal(354, beforeMidnight.Second);
    }

    [Theory]
    [InlineData(0, 60, 20)]
    [InlineData(90, 110, 70)]
    [InlineData(180, 60, 120)]
    [InlineData(270, 10, 70)]
    public void AnglesStartAtTwelveAndTurnClockwise(double angle, double x, double y)
    {
        var point = WidgetClockPresentation.PointOnFace(new(10, 20, 100, 100), angle, 1);
        Assert.Equal(x, point.X, 6);
        Assert.Equal(y, point.Y, 6);
    }

    [Theory]
    [InlineData(DesktopWidgetSize.Medium)]
    [InlineData(DesktopWidgetSize.Landscape)]
    public void DialRemainsCircularAndFitsSupportedSizes(DesktopWidgetSize size)
    {
        var (width, height) = DesktopWidgets.Dimensions(size);
        width -= 16; height -= 16;
        var face = WidgetClockPresentation.Face(width, height);
        Assert.Equal(face.Width, face.Height);
        Assert.True(face.Width > 0 && face.X >= 0 && face.Y >= 0);
        Assert.True(face.X + face.Width <= width && face.Y + face.Height <= height);
        if (size == DesktopWidgetSize.Landscape)
        {
            Assert.True(WidgetClockPresentation.IsLandscape(width, height));
            Assert.Equal(100, face.Width);
            Assert.True(width - face.X - face.Width - 18 >= 140);
        }
        else
        {
            Assert.False(WidgetClockPresentation.IsLandscape(width, height));
            Assert.Equal(width, height);
            Assert.True(face.Y + face.Height + 22 <= height);
        }
    }

    [Theory]
    [InlineData(DesktopWidgetSize.Small, DesktopWidgetSize.Landscape)]
    [InlineData(DesktopWidgetSize.Wide, DesktopWidgetSize.Landscape)]
    [InlineData(DesktopWidgetSize.Compact, DesktopWidgetSize.Medium)]
    [InlineData(DesktopWidgetSize.Medium, DesktopWidgetSize.Medium)]
    [InlineData(DesktopWidgetSize.Landscape, DesktopWidgetSize.Landscape)]
    public void ClockKeepsLayoutPositionAndOptionsAcrossNormalization(DesktopWidgetSize size, DesktopWidgetSize expected)
    {
        var dimensions = DesktopWidgets.Dimensions(size);
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Clock, Size = size, Bounds = new(700, 300, dimensions.Width, dimensions.Height), IsLocked = true, ShowSeconds = true };
        var id = widget.Id;
        var state = new CrabDeskState(); state.Widgets.Add(widget);
        DesktopWidgets.Normalize(state);
        Assert.Equal(expected, widget.Size);
        var target = DesktopWidgets.Dimensions(expected);
        Assert.Equal(new LayoutRect(700, 300, target.Width, target.Height), widget.Bounds);
        Assert.Equal(id, widget.Id);
        Assert.True(widget.IsLocked && widget.ShowSeconds);
        DesktopWidgets.Normalize(state);
        Assert.Equal(expected, widget.Size);
        Assert.Equal(DesktopWidgetSize.Medium, DesktopWidgets.DefaultSize(DesktopWidgetKind.Clock));
    }

    [Fact]
    public void ClockOffersSquareAndLandscapeLayoutsWithoutChangingOtherWidgetSizes()
    {
        Assert.Equal(new[] { DesktopWidgetSize.Medium, DesktopWidgetSize.Landscape }, DesktopWidgets.SupportedSizes(DesktopWidgetKind.Clock));
        Assert.Equal((180d, 180d), DesktopWidgets.Dimensions(DesktopWidgetSize.Medium));
        Assert.Equal((288d, 120d), DesktopWidgets.Dimensions(DesktopWidgetSize.Landscape));
        foreach (var kind in Enum.GetValues<DesktopWidgetKind>().Where(kind => kind != DesktopWidgetKind.Clock))
        {
            Assert.DoesNotContain(DesktopWidgetSize.Compact, DesktopWidgets.SupportedSizes(kind));
            Assert.DoesNotContain(DesktopWidgetSize.Landscape, DesktopWidgets.SupportedSizes(kind));
        }
    }
}
