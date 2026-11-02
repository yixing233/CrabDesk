using System.Text.Json;
using CrabDesk.Core;

namespace CrabDesk.Tests;

public sealed class WidgetNetworkGraphTests
{
    [Theory]
    [InlineData(NetworkWidgetDisplayMode.Details, DesktopWidgetSize.Large)]
    [InlineData(NetworkWidgetDisplayMode.Speeds, DesktopWidgetSize.Small)]
    [InlineData(NetworkWidgetDisplayMode.Chart, DesktopWidgetSize.Large)]
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
    public void CurveOnlyStyleReclaimsTheTitleRowForThePlot()
    {
        // The curve-only style drops the card title, so its plot has to be taller
        // than the chart's, which spends the top rows on headline figures.
        const double width = 372, height = 180, radius = 8;
        var chart = WidgetNetworkGraph.ChartLayout(width, height, radius);
        var curve = WidgetNetworkGraph.CurveLayout(width, height, radius);

        Assert.True(curve.Plot.Height > chart.Plot.Height);
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
    }

    [Fact]
    public void CurveOnlyStyleIsRecognisedAndNamed()
    {
        var curve = new DesktopWidget { Kind = DesktopWidgetKind.Network, NetworkDisplayMode = NetworkWidgetDisplayMode.Curve };
        var chart = new DesktopWidget { Kind = DesktopWidgetKind.Network, NetworkDisplayMode = NetworkWidgetDisplayMode.Chart };
        var speeds = new DesktopWidget { Kind = DesktopWidgetKind.Network, NetworkDisplayMode = NetworkWidgetDisplayMode.Speeds };

        Assert.True(DesktopWidgets.IsNetworkCurveOnly(curve));
        Assert.True(DesktopWidgets.IsNetworkCurveOnly(chart));
        Assert.False(DesktopWidgets.IsNetworkCurveOnly(speeds));
        Assert.Equal("仅上下行曲线", DesktopWidgets.NetworkDisplayName(NetworkWidgetDisplayMode.Curve));
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
        var (width, height) = DesktopWidgets.Dimensions(size);
        var layout = WidgetNetworkGraph.SpeedLayout(width, height, 20);
        Assert.False(layout.Download.Intersects(layout.Upload));
        foreach (var rect in new[] { layout.Download, layout.Upload })
        {
            Assert.True(rect.X >= 16 && rect.Y >= 16);
            Assert.True(rect.X + rect.Width <= width - 16 && rect.Y + rect.Height <= height - 16);
        }
    }
}
