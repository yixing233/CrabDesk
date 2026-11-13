using CrabDesk.Core;

namespace CrabDesk.Tests;

public sealed class WidgetCenterReorderingTests
{
    private static readonly Guid First = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Second = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid Third = Guid.Parse("00000000-0000-0000-0000-000000000003");

    [Theory]
    [InlineData(0, 2, 1, 2, 0)]
    [InlineData(2, 0, 2, 0, 1)]
    [InlineData(0, int.MaxValue, 1, 2, 0)]
    [InlineData(2, int.MinValue, 2, 0, 1)]
    public void MoveMovesHeadAndTailAndClampsTarget(int source, int target, int first, int second, int third)
    {
        Guid[] order = [First, Second, Third];
        var moved = WidgetCenterReordering.Move(order, order[source], target);
        Assert.Equal(new[] { order[first], order[second], order[third] }, moved);
        Assert.Equal(new[] { First, Second, Third }, order);
        Assert.NotSame(order, moved);
    }

    [Fact]
    public void MoveUnknownIdAndCurrentIndexReturnAnUnchangedCopy()
    {
        Guid[] order = [First, Second, Third];
        var unknown = WidgetCenterReordering.Move(order, Guid.NewGuid(), 0);
        var unchanged = WidgetCenterReordering.Move(order, Second, 1);
        Assert.Equal(order, unknown);
        Assert.Equal(order, unchanged);
        Assert.NotSame(order, unknown);
        Assert.NotSame(order, unchanged);
        Assert.Empty(WidgetCenterReordering.Move([], First, 0));
        Assert.Equal(new[] { First }, WidgetCenterReordering.Move([First], First, int.MaxValue));
    }

    [Fact]
    public void MoveDoesNotDuplicateIds()
    {
        Guid[] order = [First, Second, First, Third, Second];
        var moved = WidgetCenterReordering.Move(order, First, int.MaxValue);
        Assert.Equal(new[] { Second, Third, First }, moved);
        Assert.Equal(moved.Count, moved.Distinct().Count());
        Assert.Equal(new[] { First, Second, First, Third, Second }, order);
    }

    [Theory]
    [InlineData(90, 90, 0)]
    [InlineData(286, 90, 1)]
    [InlineData(187, 90, 0)]
    [InlineData(189, 90, 1)]
    [InlineData(-50, 90, 0)]
    [InlineData(500, 90, 1)]
    public void TargetIndexDistinguishesAdjacentSmallCards(double x, double y, int expected)
    {
        Guid[] order = [First, Second];
        var bounds = new Dictionary<Guid, LayoutRect>
        {
            [First] = new(0, 0, 180, 180),
            [Second] = new(196, 0, 180, 180)
        };
        Assert.Equal(expected, WidgetCenterReordering.TargetIndex(order, bounds, x, y));
    }

    [Fact]
    public void SmallCardsSwapUsingTheOriginalSnapshot()
    {
        DesktopWidget[] widgets = [Widget(First, 0), Widget(Second, 1)];
        Guid[] order = [First, Second];
        var snapshot = WidgetCenterLayout.Arrange(widgets, 376, gripHeight: 16);
        var target = WidgetCenterReordering.TargetIndex(order, snapshot.Bounds, 286, 106);
        var previewOrder = WidgetCenterReordering.Move(order, First, target);
        var preview = WidgetCenterLayout.Arrange(widgets, 376, gripHeight: 16, order: previewOrder);
        Assert.Equal(new[] { Second, First }, previewOrder);
        Assert.Equal(new LayoutRect(196, 16, 180, 180), preview.Bounds[First]);
        Assert.Equal(new LayoutRect(0, 16, 180, 180), snapshot.Bounds[First]);
        Assert.Equal(target, WidgetCenterReordering.TargetIndex(order, snapshot.Bounds, 286, 106));
        Assert.Equal(new[] { First, Second }, order);
        Assert.Equal(0, widgets[0].CenterOrder);
        Assert.Equal(1, widgets[1].CenterOrder);
    }

    [Theory]
    [InlineData(90, 106, 0)]
    [InlineData(286, 106, 1)]
    [InlineData(350, 318, 2)]
    [InlineData(90, 204, 0)]
    [InlineData(90, 214, 2)]
    [InlineData(1000, 15, 0)]
    [InlineData(-1000, 409, 2)]
    public void TargetIndexHandlesWideCardsAcrossRowsAndVerticalExtremes(double x, double y, int expected)
    {
        var snapshot = WidgetCenterLayout.Arrange([Widget(First, 0), Widget(Second, 1), Widget(Third, 2, 372)],
            376, gripHeight: 16);
        Assert.Equal(expected, WidgetCenterReordering.TargetIndex([First, Second, Third], snapshot.Bounds, x, y));
    }

    [Fact]
    public void WideCardMovesAcrossRowsWithoutMutatingTheModelOrSnapshot()
    {
        DesktopWidget[] widgets = [Widget(First, 0), Widget(Second, 1), Widget(Third, 2, 372)];
        Guid[] order = [First, Second, Third];
        var originals = widgets.ToDictionary(widget => widget.Id, widget => (widget.Bounds, widget.CenterOrder, widget.MonitorId));
        var snapshot = WidgetCenterLayout.Arrange(widgets, 376, gripHeight: 16);
        var target = WidgetCenterReordering.TargetIndex(order, snapshot.Bounds, 90, 106);
        var previewOrder = WidgetCenterReordering.Move(order, Third, target);
        var preview = WidgetCenterLayout.Arrange(widgets, 376, gripHeight: 16, order: previewOrder);
        Assert.Equal(new[] { Third, First, Second }, previewOrder);
        Assert.Equal(new LayoutRect(0, 16, 372, 180), preview.Bounds[Third]);
        Assert.Equal(new LayoutRect(0, 228, 180, 180), preview.Bounds[First]);
        Assert.Equal(new LayoutRect(196, 228, 180, 180), preview.Bounds[Second]);
        Assert.Equal(new LayoutRect(0, 228, 372, 180), snapshot.Bounds[Third]);
        Assert.Equal(408, preview.Height);
        foreach (var widget in widgets)
        {
            Assert.Equal(originals[widget.Id].Bounds, widget.Bounds);
            Assert.Equal(originals[widget.Id].CenterOrder, widget.CenterOrder);
            Assert.Equal(originals[widget.Id].MonitorId, widget.MonitorId);
        }
    }

    [Theory]
    [InlineData(double.NaN, 0)]
    [InlineData(0, double.NaN)]
    [InlineData(double.PositiveInfinity, 0)]
    [InlineData(0, double.NegativeInfinity)]
    public void TargetIndexRejectsNonfiniteCoordinates(double x, double y)
    {
        var bounds = new Dictionary<Guid, LayoutRect> { [First] = new(0, 0, 180, 180) };
        Assert.Equal(-1, WidgetCenterReordering.TargetIndex([First], bounds, x, y));
    }

    [Fact]
    public void TargetIndexReturnsMinusOneWithoutUsableSnapshotCards()
    {
        var bounds = new Dictionary<Guid, LayoutRect> { [First] = new(0, 0, 180, 180) };
        Assert.Equal(-1, WidgetCenterReordering.TargetIndex([], bounds, 0, 0));
        Assert.Equal(-1, WidgetCenterReordering.TargetIndex([Second], bounds, 0, 0));
        Assert.Equal(-1, WidgetCenterReordering.TargetIndex([First], new Dictionary<Guid, LayoutRect>(), 0, 0));
    }

    [Theory]
    [InlineData(double.NaN, 0, 180, 180)]
    [InlineData(0, double.PositiveInfinity, 180, 180)]
    [InlineData(0, 0, double.NegativeInfinity, 180)]
    [InlineData(0, 0, 180, double.NaN)]
    [InlineData(0, 0, 0, 180)]
    [InlineData(0, 0, 180, -1)]
    public void TargetIndexIgnoresInvalidBounds(double x, double y, double width, double height)
    {
        var bounds = new Dictionary<Guid, LayoutRect> { [First] = new(x, y, width, height) };
        Assert.Equal(-1, WidgetCenterReordering.TargetIndex([First], bounds, 10, 10));
        bounds[Second] = new(196, 0, 180, 180);
        Assert.Equal(1, WidgetCenterReordering.TargetIndex([First, Second], bounds, 286, 90));
    }

    [Fact]
    public void TargetIndexUsesCardEdgesForMixedWidthsAndOriginalOrderIndices()
    {
        var bounds = new Dictionary<Guid, LayoutRect>
        {
            [Second] = new(396, 0, 180, 180),
            [First] = new(0, 0, 380, 180)
        };
        Assert.Equal(0, WidgetCenterReordering.TargetIndex([First, Second], bounds, 375, 90));
        Assert.Equal(1, WidgetCenterReordering.TargetIndex([Second, First], bounds, 375, 90));
    }

    [Fact]
    public void ArrangeReservesGripForEachCardAndIncludesItInRowHeight()
    {
        DesktopWidget[] widgets = [Widget(First, 0, height: 240), Widget(Second, 1), Widget(Third, 2, 372)];
        var result = WidgetCenterLayout.Arrange(widgets, 376, gripHeight: 16);
        Assert.Equal(new LayoutRect(0, 16, 180, 240), result.Bounds[First]);
        Assert.Equal(new LayoutRect(196, 16, 180, 180), result.Bounds[Second]);
        Assert.Equal(new LayoutRect(0, 288, 372, 180), result.Bounds[Third]);
        Assert.Equal(468, result.Height);
        Assert.Equal(0, WidgetCenterLayout.Arrange([], 376, gripHeight: 16).Height);
    }

    [Theory]
    [InlineData(-16)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ArrangeNormalizesInvalidGripWithoutChangingDefaultLayout(double gripHeight)
    {
        DesktopWidget[] widgets = [Widget(First, 0), Widget(Second, 1)];
        var normal = WidgetCenterLayout.Arrange(widgets, 376);
        var result = WidgetCenterLayout.Arrange(widgets, 376, gripHeight: gripHeight);
        Assert.Equal(normal.Height, result.Height);
        foreach (var widget in widgets) Assert.Equal(normal.Bounds[widget.Id], result.Bounds[widget.Id]);
    }

    [Fact]
    public void ArrangeExplicitOrderOverridesSavedOrderAndAppendsUnspecifiedCards()
    {
        var desktop = Widget(Guid.NewGuid(), 0);
        desktop.Placement = WidgetPlacement.Desktop;
        DesktopWidget[] widgets = [Widget(Third, 2), Widget(Second, 1), desktop, Widget(First, 1)];
        Guid[] order = [Third, Guid.NewGuid(), Third, desktop.Id];
        var originals = widgets.ToDictionary(widget => widget.Id, widget => (widget.Bounds, widget.CenterOrder));
        var normal = WidgetCenterLayout.Arrange(widgets, 600);
        var preview = WidgetCenterLayout.Arrange(widgets, 600, order: order);
        Assert.Equal(0, normal.Bounds[First].X);
        Assert.Equal(196, normal.Bounds[Second].X);
        Assert.Equal(392, normal.Bounds[Third].X);
        Assert.Equal(0, preview.Bounds[Third].X);
        Assert.Equal(196, preview.Bounds[First].X);
        Assert.Equal(392, preview.Bounds[Second].X);
        Assert.DoesNotContain(desktop.Id, preview.Bounds.Keys);
        Assert.Equal(4, order.Length);
        foreach (var widget in widgets)
        {
            Assert.Equal(originals[widget.Id].Bounds, widget.Bounds);
            Assert.Equal(originals[widget.Id].CenterOrder, widget.CenterOrder);
        }
    }

    private static DesktopWidget Widget(Guid id, int order, double width = 180, double height = 180) => new()
    {
        Id = id, Kind = DesktopWidgetKind.Note, Size = DesktopWidgetSize.Custom,
        Placement = WidgetPlacement.Center, CenterOrder = order, MonitorId = "saved-desktop",
        Bounds = new(48, 72, width, height)
    };
}
