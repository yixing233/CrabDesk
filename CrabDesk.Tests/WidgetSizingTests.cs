using CrabDesk.Core;

namespace CrabDesk.Tests;

public sealed class WidgetSizingTests
{
    [Theory]
    [InlineData(DesktopWidgetKind.Note)] [InlineData(DesktopWidgetKind.Todo)]
    [InlineData(DesktopWidgetKind.Lyrics)]
    public async Task CustomDimensionsAndContentsSurviveConfigReload(DesktopWidgetKind kind)
    {
        var directory = Path.Combine(Path.GetTempPath(), "CrabDesk.FreeSizingTests", Guid.NewGuid().ToString("N"));
        try
        {
            var widget = new DesktopWidget { Kind = kind, Size = DesktopWidgetSize.Custom,
                Bounds = new(40, 50, 327.2, 463.6), Text = "便签内容", Tasks = [new() { Text = "待办内容" }] };
            var store = new JsonLayoutStore(directory);
            await store.SaveAsync(new() { Widgets = [widget] });
            var loaded = Assert.Single((await store.LoadAsync()).Widgets);
            Assert.Equal(DesktopWidgetSize.Custom, loaded.Size); Assert.Equal(widget.Bounds, loaded.Bounds);
            Assert.Equal((327.2, 463.6), DesktopWidgets.Dimensions(loaded));
            Assert.Equal("便签内容", loaded.Text); Assert.Equal("待办内容", Assert.Single(loaded.Tasks).Text);
            var backups = new JsonBackupService(directory);
            var backup = await backups.CreateAsync(new() { Widgets = [widget] });
            var restored = Assert.Single((await backups.LoadAsync(backup.Path)).Widgets);
            Assert.Equal(DesktopWidgetSize.Custom, restored.Size); Assert.Equal(widget.Bounds, restored.Bounds);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void CustomNormalizationHonorsMinimumsAndPresetsRemainAuthoritative()
    {
        var custom = new DesktopWidget { Kind = DesktopWidgetKind.Note, Size = DesktopWidgetSize.Custom, Bounds = new(double.NaN, 80, 20, double.PositiveInfinity) };
        var preset = new DesktopWidget { Kind = DesktopWidgetKind.Todo, Size = DesktopWidgetSize.Medium, Bounds = new(10, 20, 300, 450) };
        var clock = new DesktopWidget { Kind = DesktopWidgetKind.Clock, Size = DesktopWidgetSize.Custom };
        DesktopWidgets.Normalize(new() { Widgets = [custom, preset, clock] });
        Assert.Equal(new LayoutRect(32, 80, 180, 180), custom.Bounds);
        Assert.Equal((180d, 180d), DesktopWidgets.Dimensions(preset));
        Assert.Equal(DesktopWidgetSize.Medium, clock.Size);
        Assert.DoesNotContain(DesktopWidgetSize.Custom, DesktopWidgets.SupportedSizes(DesktopWidgetKind.Clock));
    }

    [Fact]
    public void MonitorChangesConstrainCustomDimensionsAndRespectWorkAreaOffsets()
    {
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Todo, Size = DesktopWidgetSize.Custom, MonitorId = "a", Bounds = new(600, 500, 900, 800) };
        var monitor = new MonitorLayout { Id = "a", DeviceName = "a", IsPrimary = true,
            Bounds = new(1000, 0, 600, 500), WorkArea = new(1020, 30, 560, 440), PixelBounds = new(1000, 0, 600, 500), PixelWorkArea = new(1020, 30, 560, 440) };
        DesktopWidgets.NormalizeForMonitors(new() { Widgets = [widget] }, [monitor]);
        Assert.Equal(new LayoutRect(20, 30, 560, 440), widget.Bounds);
    }

    [Theory]
    [InlineData(WidgetResizeEdges.Left, 140, 100, 260, 200)]
    [InlineData(WidgetResizeEdges.Right, 100, 100, 340, 200)]
    [InlineData(WidgetResizeEdges.Top, 100, 130, 300, 170)]
    [InlineData(WidgetResizeEdges.Bottom, 100, 100, 300, 230)]
    [InlineData(WidgetResizeEdges.TopLeft, 140, 130, 260, 170)]
    [InlineData(WidgetResizeEdges.TopRight, 100, 130, 340, 170)]
    [InlineData(WidgetResizeEdges.BottomLeft, 140, 100, 260, 230)]
    [InlineData(WidgetResizeEdges.BottomRight, 100, 100, 340, 230)]
    public void EachEdgeKeepsTheOppositeEdgeFixed(WidgetResizeEdges edges, double x, double y, double width, double height) =>
        Assert.Equal(new LayoutRect(x, y, width, height), WidgetSizing.Resize(new(100, 100, 300, 200), edges, 40, 30, new(0, 0, 1000, 700)));

    [Fact]
    public void ResizeStopsAtMinimumAndWorkAreaWithoutMovingTheOppositeCorner()
    {
        var start = new LayoutRect(100, 100, 300, 200); var area = new LayoutRect(20, 30, 500, 400);
        Assert.Equal(new LayoutRect(220, 180, 180, 120), WidgetSizing.Resize(start, WidgetResizeEdges.TopLeft, 1000, 1000, area));
        Assert.Equal(new LayoutRect(20, 30, 380, 270), WidgetSizing.Resize(start, WidgetResizeEdges.TopLeft, -1000, -1000, area));
        Assert.Equal(new LayoutRect(100, 100, 420, 330), WidgetSizing.Resize(start, WidgetResizeEdges.BottomRight, 1000, 1000, area));
    }

    [Fact]
    public void HitTestingSeparatesResizeBordersFromContentsAndHonorsLocks()
    {
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Note, Bounds = new(100, 100, 300, 200) };
        Assert.Equal(WidgetResizeEdges.Left, WidgetSizing.HitTest(widget, 101, 200));
        Assert.Equal(WidgetResizeEdges.BottomRight, WidgetSizing.HitTest(widget, 393, 293));
        Assert.Equal(WidgetResizeEdges.None, WidgetSizing.HitTest(widget, 250, 180));
        Assert.Equal(WidgetResizeEdges.None, WidgetSizing.HitTest(widget, 113, 114));
        Assert.Equal(WidgetResizeEdges.None, WidgetSizing.HitTest(widget, 99, 180));
        widget.IsLocked = true; Assert.Equal(WidgetResizeEdges.None, WidgetSizing.HitTest(widget, 101, 200));
        widget.IsLocked = false; widget.Kind = DesktopWidgetKind.Weather;
        Assert.Equal(WidgetResizeEdges.None, WidgetSizing.HitTest(widget, 101, 200));
    }

    // Resizing snaps the edges the user holds, not the whole rectangle. These
    // fix the two ways that can go wrong: snapping an edge that was not being
    // dragged, and letting a snap push the card below its minimum size.
    [Fact]
    public void ResizeSnapsTheDraggedRightEdgeToAPeerEdge()
    {
        var resizing = new LayoutRect(100, 100, 200, 150);
        // The peer's left edge sits at 305, five pixels past our right edge.
        var peer = new LayoutRect(305, 50, 100, 50);
        var result = DesktopBoxAlignmentEngine.SnapResize(resizing, WidgetResizeEdges.Right, [peer], new(0, 0, 1000, 700));

        Assert.Equal(305, result.Bounds.X + result.Bounds.Width);
        Assert.Equal(100, result.Bounds.X); // The opposite edge must not move.
        Assert.Single(result.Guides);
        Assert.True(result.Guides[0].IsVertical);
        Assert.Equal(305, result.Guides[0].Position);
    }

    [Fact]
    public void ResizeSnapsTheDraggedLeftEdgeWithoutMovingTheRightOne()
    {
        var resizing = new LayoutRect(100, 100, 200, 150);
        // The peer's left edge sits at 95, five pixels left of ours.
        var peer = new LayoutRect(95, 50, 100, 50);

        var result = DesktopBoxAlignmentEngine.SnapResize(resizing, WidgetResizeEdges.Left, [peer], new(0, 0, 1000, 700));

        Assert.Equal(95, result.Bounds.X);
        Assert.Equal(300, result.Bounds.X + result.Bounds.Width); // Right stays put.
    }

    [Fact]
    public void ResizeSnapsVerticallyAndReportsAHorizontalGuide()
    {
        var resizing = new LayoutRect(100, 100, 200, 150);
        // The peer's top edge sits at 255, five pixels past our bottom edge.
        var peer = new LayoutRect(50, 255, 100, 50);

        var result = DesktopBoxAlignmentEngine.SnapResize(resizing, WidgetResizeEdges.Bottom, [peer], new(0, 0, 1000, 700));

        Assert.Equal(255, result.Bounds.Y + result.Bounds.Height);
        Assert.False(result.Guides[0].IsVertical);
    }

    [Fact]
    public void ResizeLeavesTheCardAloneWhenNoLineIsNear()
    {
        var resizing = new LayoutRect(100, 100, 200, 150);

        var result = DesktopBoxAlignmentEngine.SnapResize(resizing, WidgetResizeEdges.BottomRight,
            [new LayoutRect(800, 600, 100, 50)], new(0, 0, 1000, 700));

        Assert.Equal(resizing, result.Bounds);
        Assert.Empty(result.Guides);
    }

    [Fact]
    public void ResizeSnapCannotShrinkBelowTheMinimumSize()
    {
        var resizing = new LayoutRect(300, 100, 200, 150);
        // A line very close to the left edge would otherwise collapse the card.
        var peer = new LayoutRect(310, 50, 100, 50);

        var result = DesktopBoxAlignmentEngine.SnapResize(resizing, WidgetResizeEdges.Left, [peer], new(0, 0, 1000, 700));

        Assert.True(result.Bounds.Width >= WidgetSizing.MinimumWidth);
    }

    [Fact]
    public void ResizeSnapIgnoresEdgesThatAreNotBeingDragged()
    {
        var resizing = new LayoutRect(100, 100, 200, 150);
        var peer = new LayoutRect(96, 50, 100, 50); // Left edge nearby, but we drag the right one.

        var result = DesktopBoxAlignmentEngine.SnapResize(resizing, WidgetResizeEdges.Right, [peer], new(0, 0, 1000, 700));

        Assert.Equal(resizing, result.Bounds);
        Assert.Empty(result.Guides);
    }

    // A corner drag holds two edges at once; each axis must snap on its own so
    // one axis locking does not suppress the other.
    [Fact]
    public void CornerResizeSnapsBothAxesIndependently()
    {
        var resizing = new LayoutRect(100, 100, 200, 150);
        // Left edge of the peer at 305, top edge at 255.
        var peer = new LayoutRect(305, 255, 100, 50);

        var result = DesktopBoxAlignmentEngine.SnapResize(resizing, WidgetResizeEdges.BottomRight,
            [peer], new(0, 0, 1000, 700));

        Assert.Equal(305, result.Bounds.X + result.Bounds.Width);
        Assert.Equal(255, result.Bounds.Y + result.Bounds.Height);
        Assert.Equal(100, result.Bounds.X);
        Assert.Equal(100, result.Bounds.Y);
        Assert.Equal(2, result.Guides.Count);
    }

    [Fact]
    public void ResizeSnapRejectsDegenerateInput()
    {
        var resizing = new LayoutRect(100, 100, 200, 150);
        var peers = new[] { new LayoutRect(305, 255, 100, 50) };

        // No edges held: nothing to snap.
        Assert.Equal(resizing, DesktopBoxAlignmentEngine.SnapResize(resizing, WidgetResizeEdges.None, peers, new(0, 0, 1000, 700)).Bounds);
        // Non-finite input must be returned unchanged rather than crashing.
        Assert.Equal(resizing, DesktopBoxAlignmentEngine.SnapResize(resizing, WidgetResizeEdges.Right, peers,
            new(0, 0, double.NaN, 700)).Bounds);
        // Peers with no extent are ignored.
        Assert.Empty(DesktopBoxAlignmentEngine.SnapResize(resizing, WidgetResizeEdges.Right,
            [new LayoutRect(305, 255, 0, 0)], new(0, 0, 1000, 700)).Guides);
    }
}
