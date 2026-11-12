using System.Text.Json;
using CrabDesk.Core;

namespace CrabDesk.Tests;

public sealed class WidgetNetworkGraphTests
{
    [Theory]
    [InlineData(NetworkWidgetDisplayMode.Details, DesktopWidgetSize.Large)]
    [InlineData(NetworkWidgetDisplayMode.Speeds, DesktopWidgetSize.Small)]
    [InlineData(NetworkWidgetDisplayMode.Curve, DesktopWidgetSize.Large)]
    public void DisplayPreferencePersistsAndNormalizesSizeWithoutPersistingSamples(NetworkWidgetDisplayMode mode, DesktopWidgetSize size)
    {
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Network, NetworkDisplayMode = mode, Size = DesktopWidgetSize.Medium };
        widget.NetworkHistory.Add(new(DateTimeOffset.Now, 123, 456));
        var loaded = JsonSerializer.Deserialize<DesktopWidget>(JsonSerializer.Serialize(widget))!;
        var state = new CrabDeskState { Widgets = [loaded] }; DesktopWidgets.Normalize(state);
        Assert.Equal(mode, loaded.NetworkDisplayMode); Assert.Equal(size, loaded.Size);
        Assert.Empty(loaded.NetworkHistory);
        Assert.Contains(size, DesktopWidgets.SupportedSizes(loaded));
    }

    [Fact]
    public void OnlyThreeStylesAreOfferedAndStoredValuesKeepTheirMeaning()
    {
        // Dropping the combined style must not renumber the others: a saved
        // "2" would otherwise silently become a different style.
        Assert.Equal(0, (int)NetworkWidgetDisplayMode.Details);
        Assert.Equal(1, (int)NetworkWidgetDisplayMode.Speeds);
        Assert.Equal(2, (int)NetworkWidgetDisplayMode.LegacyChart);
        Assert.Equal(3, (int)NetworkWidgetDisplayMode.Curve);

        Assert.Equal(
            [NetworkWidgetDisplayMode.Details, NetworkWidgetDisplayMode.Speeds, NetworkWidgetDisplayMode.Curve],
            DesktopWidgets.OfferedNetworkDisplayModes);
        Assert.Equal(["详细信息", "网速文本", "网速曲线"],
            DesktopWidgets.OfferedNetworkDisplayModes.Select(DesktopWidgets.NetworkDisplayName).ToArray());
    }

    [Fact]
    public void RetiredCombinedStyleLoadsAsTheCurveStyle()
    {
        var retired = new DesktopWidget { Kind = DesktopWidgetKind.Network, NetworkDisplayMode = NetworkWidgetDisplayMode.LegacyChart };
        var state = new CrabDeskState { Widgets = [retired] };

        DesktopWidgets.Normalize(state);

        Assert.Equal(NetworkWidgetDisplayMode.Curve, retired.NetworkDisplayMode);
        Assert.True(DesktopWidgets.IsNetworkCurveOnly(retired));
    }

    [Fact]
    public void CurveStyleKeepsItsHeadlineRowPlotAndFooterInsideTheCard()
    {
        const double width = 372, height = 180;
        var curve = WidgetNetworkGraph.CurveLayout(width, height, 8);

        // Everything stays inside the card and nothing overlaps.
        Assert.True(curve.Plot.Y >= 0);
        Assert.True(curve.Footer.Y >= curve.Plot.Y + curve.Plot.Height);
        Assert.True(curve.Footer.Y + curve.Footer.Height <= height);
        // Current speeds keep their own row above the plot: the curve shows the
        // trend, those numbers answer "how fast is it right now".
        Assert.True(curve.Download.Y + curve.Download.Height <= curve.Plot.Y);
        Assert.True(curve.Upload.Y + curve.Upload.Height <= curve.Plot.Y);
        Assert.True(curve.Upload.X >= curve.Download.X + curve.Download.Width);
        Assert.Equal(curve.Download.Y, curve.Upload.Y);
        // The plot is the point of this style, so it has to take the largest
        // single share of the card: more than the headline row and footer
        // together, which is what a reader would call "mostly curve".
        Assert.True(curve.Plot.Height > curve.Footer.Height * 4);
        Assert.True(curve.Plot.Height > curve.Download.Height * 2);
    }

    [Fact]
    public void HistoryKeepsOnlyRecentSamplesAndResetsWhenTheInterfaceChangesOrDisconnects()
    {
        var now = DateTimeOffset.UtcNow;
        var widget = new DesktopWidget { Network = new(now) { InterfaceId = "a", IsConnected = true } };
        for (var second = 0; second <= 90; second++)
            WidgetNetworkGraph.Append(widget, widget.Network with { CapturedAt = now.AddSeconds(second), DownloadBytesPerSecond = second });
        Assert.Equal(61, widget.NetworkHistory.Count);
        Assert.Equal(now.AddSeconds(30), widget.NetworkHistory[0].CapturedAt);
        var switched = widget.Network with { InterfaceId = "b", CapturedAt = now.AddSeconds(91) };
        WidgetNetworkGraph.Append(widget, switched); widget.Network = switched;
        Assert.Single(widget.NetworkHistory);
        WidgetNetworkGraph.Append(widget, switched with { IsConnected = false });
        Assert.Empty(widget.NetworkHistory);
        WidgetNetworkGraph.Append(widget, null); Assert.Empty(widget.NetworkHistory);
    }

    [Fact]
    public void CurvesShareOneScaleUseRealTimeAndDistinguishMissingSamplesFromZero()
    {
        var now = DateTimeOffset.UtcNow;
        var plot = new LayoutRect(10, 20, 300, 60);
        var graph = WidgetNetworkGraph.Project([
            new(now.AddSeconds(-61), 999999, 999999),
            new(now.AddSeconds(-60), 1024, 512),
            new(now.AddSeconds(-59), 0, 256),
            new(now.AddSeconds(-58), null, 0),
            new(now.AddSeconds(-57), 512, null),
            new(now.AddSeconds(-50), 512, 0),
            new(now.AddSeconds(-49), double.NaN, double.PositiveInfinity)
        ], plot, now);
        Assert.Equal(1024 * 1.15, graph.Maximum);
        Assert.Equal(3, graph.Download.Count); Assert.Equal(2, graph.Upload.Count);
        Assert.Equal(10, graph.Download[0][0].X);
        Assert.Equal(15, graph.Download[0][1].X);
        Assert.Equal(80, graph.Download[0][1].Y); // Real zero sits on the baseline.
        Assert.True(graph.Download[0][0].Y < graph.Upload[0][0].Y);
        Assert.All(graph.Download.Concat(graph.Upload).SelectMany(segment => segment), point =>
        {
            Assert.InRange(point.X, 10, 310); Assert.InRange(point.Y, 20, 80);
            Assert.True(double.IsFinite(point.X) && double.IsFinite(point.Y));
        });
    }

    [Fact]
    public void EmptyHistoryHasNoInventedCurve()
    {
        var graph = WidgetNetworkGraph.Project([], new(0, 0, 100, 50), DateTimeOffset.Now);
        Assert.Empty(graph.Download); Assert.Empty(graph.Upload); Assert.Equal(1024, graph.Maximum);
    }

    [Theory]
    [InlineData(DesktopWidgetSize.Small)] [InlineData(DesktopWidgetSize.Wide)]
    public void SpeedOnlyLayoutStaysInsideRoundedCard(DesktopWidgetSize size)
    {
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Network, NetworkDisplayMode = NetworkWidgetDisplayMode.Speeds, Size = size };
        var (width, height) = DesktopWidgets.Dimensions(widget);
        var layout = WidgetNetworkGraph.SpeedLayout(width, height, 20);
        Assert.False(layout.Download.Intersects(layout.Upload));
        foreach (var rect in new[] { layout.Download, layout.Upload })
        {
            Assert.True(rect.X >= 16 && rect.Y >= 16);
            Assert.True(rect.X + rect.Width <= width - 16 && rect.Y + rect.Height <= height - 16);
        }
    }

    [Theory]
    [InlineData(164)]
    [InlineData(180)]
    public void CompactSpeedPresetUsesStandardWidgetWidthAfterLoading(double savedWidth)
    {
        var widget = new DesktopWidget
        {
            Kind = DesktopWidgetKind.Network, NetworkDisplayMode = NetworkWidgetDisplayMode.Speeds,
            Size = DesktopWidgetSize.Small, Bounds = new(40, 50, savedWidth, 84)
        };

        DesktopWidgets.Normalize(new() { Widgets = [widget] });

        Assert.Equal(new LayoutRect(40, 50, 180, 84), widget.Bounds);
        Assert.Equal(DesktopWidgets.Dimensions(DesktopWidgetSize.Medium).Width, widget.Bounds.Width);
        Assert.Equal("180 × 84 DIP", DesktopWidgets.SizeLabel(widget));
        Assert.Equal("180 × 84 DIP", DesktopWidgets.SizeLabel(widget, DesktopWidgetSize.Small));
        Assert.Equal("372 × 84 DIP", DesktopWidgets.SizeLabel(widget, DesktopWidgetSize.Wide));
        Assert.Equal((180d, 84d), DesktopWidgets.Dimensions(DesktopWidgetSize.Small));
    }
}
