using System.Text.Json;
using CrabDesk.Core;

namespace CrabDesk.Tests;

public sealed class FileShelfFloatingTests
{
    [Theory]
    [InlineData(FileShelfDockEdge.Left, 1)]
    [InlineData(FileShelfDockEdge.Right, 1.5)]
    [InlineData(FileShelfDockEdge.Top, 2)]
    [InlineData(FileShelfDockEdge.Bottom, 1.25)]
    public void RetractedTabsSlideTowardTheEdgeLeavingSixtyPercentVisible(FileShelfDockEdge edge, double scale)
    {
        var area = new LayoutRect(-1920, -200, 1920, 1080);
        var expanded = FileShelfPlacement.Dock(new(-1400, 100, 380 * scale, 420 * scale), area, edge);
        var tab = FileShelfPlacement.Collapsed(expanded, area, edge, scale);
        Assert.Equal(tab, FileShelfPlacement.Retracted(tab, edge, 0));
        for (var step = 0; step <= 10; step++)
        {
            var progress = step / 10d;
            var slid = FileShelfPlacement.Retracted(tab, edge, progress);
            var visible = FileShelfPlacement.Visible(slid, area);
            Assert.Equal(tab.Width, slid.Width);
            Assert.Equal(tab.Height, slid.Height);
            AssertContained(visible, area);
            Assert.Equal(tab.Width * tab.Height * (1 - .4 * progress), visible.Width * visible.Height, 6);
            if (edge is FileShelfDockEdge.Left or FileShelfDockEdge.Right)
            {
                Assert.Equal(tab.Y, slid.Y);
                Assert.Equal(tab.Width * (1 - .4 * progress), visible.Width, 6);
            }
            else
            {
                Assert.Equal(tab.X, slid.X);
                Assert.Equal(tab.Height * (1 - .4 * progress), visible.Height, 6);
            }
        }
    }

    [Fact]
    public void UndockedTabsDoNotRetract()
    {
        var tab = new LayoutRect(100, 200, 40, 40);
        Assert.Equal(tab, FileShelfPlacement.Retracted(tab, FileShelfDockEdge.None, 1));
    }

    [Theory]
    [InlineData(-1912, 20, FileShelfDockEdge.Left)]
    [InlineData(-390, 20, FileShelfDockEdge.Right)]
    [InlineData(-1100, -190, FileShelfDockEdge.Top)]
    [InlineData(-1100, 410, FileShelfDockEdge.Bottom)]
    [InlineData(-1100, 20, FileShelfDockEdge.None)]
    public void PlacementSnapsToNearestWorkAreaEdgeIncludingNegativeMonitors(double x, double y, FileShelfDockEdge expected)
    {
        var area = new LayoutRect(-1920, -200, 1920, 1040);
        var result = FileShelfPlacement.Snap(new(x, y, 380, 420), area, 20);
        Assert.Equal(expected, result.Edge);
        AssertContained(result.Bounds, area);
        Assert.Equal(380, result.Bounds.Width); Assert.Equal(420, result.Bounds.Height);
        if (expected == FileShelfDockEdge.None) Assert.Equal(new LayoutRect(x, y, 380, 420), result.Bounds);
        else Assert.Equal(result.Bounds, FileShelfPlacement.Dock(result.Bounds, area, expected));
    }

    [Theory]
    [InlineData(FileShelfDockEdge.Left, 1)]
    [InlineData(FileShelfDockEdge.Right, 1.5)]
    [InlineData(FileShelfDockEdge.Top, 2)]
    [InlineData(FileShelfDockEdge.Bottom, 1.25)]
    public void CollapsedTabsStayInsideWorkAreaAndDoNotChangeExpandedPosition(FileShelfDockEdge edge, double scale)
    {
        var area = new LayoutRect(-1600, 40, 1600, 900);
        var expanded = FileShelfPlacement.Dock(new(-900, 200, 380 * scale, 420 * scale), area, edge);
        var tab = FileShelfPlacement.Collapsed(expanded, area, edge, scale);
        AssertContained(tab, area);
        Assert.Equal(40 * scale, tab.Width);
        Assert.Equal(40 * scale, tab.Height);
        Assert.Equal(tab, FileShelfPlacement.Dock(tab, area, edge));
        Assert.Equal(380 * scale, expanded.Width);
        Assert.Equal(420 * scale, expanded.Height);
    }

    [Fact]
    public void MonitorRemovalClampsSavedWindowWithoutLosingTheEdge()
    {
        var area = new LayoutRect(0, 48, 1280, 672);
        var restored = FileShelfPlacement.Dock(new(-1600, 1000, 760, 840), area, FileShelfDockEdge.Right);
        AssertContained(restored, area);
        Assert.Equal(1280, restored.X + restored.Width);
        Assert.Equal(672, restored.Height);
    }

    [Fact]
    public void FloatingSettingsRoundTripIndependentlyOfDesktopPositionAndFileIds()
    {
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.FileShelf, Bounds = new(40, 50, 372, 180),
            FileShelfItems = [new() { OriginalPath = @"C:\test.pdf", Mode = WidgetFileShelfMode.Copy, StoredRelativePath = "FileShelf/existing/test.pdf" }],
            FileShelfFloating = new() { Enabled = true, AutoHide = false, MonitorId = "second-monitor", Bounds = new(50, 60, 380, 420), DockEdge = FileShelfDockEdge.Bottom } };
        var loaded = JsonSerializer.Deserialize<DesktopWidget>(JsonSerializer.Serialize(widget))!;
        WidgetFileShelf.Normalize(loaded);
        Assert.Equal(widget.Id, loaded.Id); Assert.Equal(widget.Bounds, loaded.Bounds);
        Assert.Equal(widget.FileShelfItems[0].Id, loaded.FileShelfItems[0].Id);
        Assert.Equal(widget.FileShelfItems[0].StoredRelativePath, loaded.FileShelfItems[0].StoredRelativePath);
        Assert.Equal(widget.FileShelfFloating.Bounds, loaded.FileShelfFloating.Bounds);
        Assert.Equal("second-monitor", loaded.FileShelfFloating.MonitorId);
        Assert.True(loaded.FileShelfFloating.Enabled); Assert.False(loaded.FileShelfFloating.AutoHide);
        Assert.Equal(FileShelfDockEdge.Bottom, loaded.FileShelfFloating.DockEdge);
    }

    [Fact]
    public void OldDefaultsRenameWithoutChangingCustomTitlesOrEnablingFloatingWindows()
    {
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.FileShelf, Title = "文件暂存", FileShelfFloating = null! };
        WidgetFileShelf.Normalize(widget);
        Assert.Equal("文件中转站", widget.DisplayTitle);
        Assert.False(widget.FileShelfFloating.Enabled); Assert.True(widget.FileShelfFloating.AutoHide);
        widget.Title = "我的项目资料"; WidgetFileShelf.Normalize(widget); Assert.Equal("我的项目资料", widget.DisplayTitle);
    }

    [Fact]
    public void InvalidPersistedGeometryReturnsToDefaultPlacement()
    {
        var settings = new FileShelfFloatingSettings { Bounds = new(double.NaN, 0, 30, 32), DockEdge = (FileShelfDockEdge)90, MonitorId = null! };
        settings.Normalize();
        Assert.Null(settings.Bounds); Assert.Equal(FileShelfDockEdge.None, settings.DockEdge); Assert.Equal("", settings.MonitorId);
    }

    private static void AssertContained(LayoutRect bounds, LayoutRect area)
    {
        Assert.InRange(bounds.X, area.X, area.X + area.Width - bounds.Width);
        Assert.InRange(bounds.Y, area.Y, area.Y + area.Height - bounds.Height);
    }
}
