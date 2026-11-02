using System.Text.Json;
using CrabDesk.Core;

namespace CrabDesk.Tests;

public sealed class WidgetMonitorTests
{
    [Fact]
    public void ExistingWidgetKindsKeepTheirStoredValuesAndNewKindsHaveSupportedSizes()
    {
        Assert.Equal(5, (int)DesktopWidgetKind.Weather);
        Assert.Equal(6, (int)DesktopWidgetKind.Performance);
        Assert.Equal(7, (int)DesktopWidgetKind.Network);
        Assert.Equal(DesktopWidgetSize.Large, DesktopWidgets.DefaultSize(DesktopWidgetKind.Network));
        Assert.Equal(new[] { DesktopWidgetSize.Large }, DesktopWidgets.SupportedSizes(DesktopWidgetKind.Network));
        var state = new CrabDeskState { Widgets = [new() { Kind = DesktopWidgetKind.Network, Size = DesktopWidgetSize.Medium,
            NetworkAdapterId = "adapter", NetworkLatencyTarget = " https://invalid.test/path " }] };
        DesktopWidgets.Normalize(state);
        var widget = Assert.Single(state.Widgets);
        Assert.Equal(DesktopWidgetSize.Large, widget.Size);
        Assert.Equal("1.1.1.1", widget.NetworkLatencyTarget);
        Assert.Equal("adapter", widget.NetworkAdapterId);
    }

    [Fact]
    public void OnlyMonitorPreferencesPersistAndLiveAddressesAndSamplesRemainTransient()
    {
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Network, NetworkAdapterId = "chosen", NetworkLatencyTarget = "example.com",
            Network = new(DateTimeOffset.Now) { IPv4 = "192.0.2.15", IPv6 = "2001:db8::15", DownloadBytesPerSecond = 12345 },
            PublicIp = "203.0.113.18", IsPublicIpLoading = true, PublicIpError = "transient-error", MonitorError = "counter-error",
            Performance = new(DateTimeOffset.Now, 15, 25, 123, 456, 10, 20, 30, 40, 50) };
        var json = JsonSerializer.Serialize(widget);
        Assert.DoesNotContain("192.0.2.15", json); Assert.DoesNotContain("2001:db8", json);
        Assert.DoesNotContain("203.0.113.18", json); Assert.DoesNotContain("transient-error", json); Assert.DoesNotContain("counter-error", json);
        var loaded = JsonSerializer.Deserialize<DesktopWidget>(json)!;
        Assert.Equal("chosen", loaded.NetworkAdapterId); Assert.Equal("example.com", loaded.NetworkLatencyTarget);
        Assert.Null(loaded.Network); Assert.Null(loaded.Performance); Assert.Empty(loaded.PublicIp); Assert.False(loaded.IsPublicIpLoading);
    }

    [Fact]
    public void GpuUsageAggregatesProcessesPerEngineAndChoosesBusiestEngine()
    {
        Assert.Equal(90d, WidgetMonitorMetrics.GpuUtilization([
            ("pid_1_luid_A_phys_0_eng_0_3D", 30d), ("pid_2_luid_A_phys_0_eng_0_3D", 40d),
            ("pid_1_luid_A_phys_0_eng_1_Copy", 90d), ("pid_1_luid_B_phys_0_eng_0_3D", 50d)]));
        Assert.Null(WidgetMonitorMetrics.GpuUtilization([]));
        Assert.Null(WidgetMonitorMetrics.GpuUtilization([("invalid", double.NaN)]));
        Assert.Equal(100d, WidgetMonitorMetrics.GpuUtilization([("engine", 150d)]));
    }

    [Theory]
    [InlineData(0, 4096, 2, 2048)]
    [InlineData(100, 100, 1, 0)]
    public void CounterRatesUseActualElapsedTime(long previous, long current, double seconds, double expected) =>
        Assert.Equal(expected, WidgetMonitorMetrics.Rate(previous, current, seconds));

    [Fact]
    public void InvalidCounterIntervalsAndResetsDoNotProduceFalseZeroOrNegativeRates()
    {
        Assert.Null(WidgetMonitorMetrics.Rate(100, 1, 1)); Assert.Null(WidgetMonitorMetrics.Rate(1, 5, 0));
        Assert.Null(WidgetMonitorMetrics.Rate(1, 5, double.NaN));
        Assert.Equal("—", WidgetMonitorMetrics.Speed(null)); Assert.Equal("0 B/s", WidgetMonitorMetrics.Speed(0));
        Assert.Equal("1.5 MB/s", WidgetMonitorMetrics.Speed(1.5 * 1024 * 1024));
        Assert.Equal("—", WidgetMonitorMetrics.Percent(double.NaN));
    }

    [Theory]
    [InlineData("", true)] [InlineData("1.1.1.1", true)] [InlineData("2001:db8::1", true)]
    [InlineData("example.com", true)] [InlineData("https://example.com/path", false)] [InlineData("bad target", false)]
    public void PingTargetsAreAddressesOrHostnames(string target, bool expected) =>
        Assert.Equal(expected, WidgetMonitorMetrics.IsValidLatencyTarget(target));

    [Theory]
    [InlineData(180, 0)] [InlineData(180, 20)] [InlineData(372, 0)] [InlineData(372, 20)]
    public void MonitorContentFitsRoundedCardsAndCellsDoNotOverlap(double width, double radius)
    {
        var body = WidgetMonitorPresentation.Body(width, 180, radius);
        var cells = Enumerable.Range(0, 4).Select(index => WidgetMonitorPresentation.PerformanceCell(body, index)).ToArray();
        for (var index = 0; index < cells.Length; index++)
        {
            Assert.True(cells[index].X >= body.X && cells[index].Y >= body.Y);
            Assert.True(cells[index].X + cells[index].Width <= body.X + body.Width + .01);
            Assert.True(cells[index].Y + cells[index].Height <= body.Y + body.Height + .01);
            for (var other = index + 1; other < cells.Length; other++) Assert.False(cells[index].Intersects(cells[other]));
        }
        if (width < 300) return;
        var network = WidgetMonitorPresentation.NetworkFields(body);
        var regions = new[] { network.Download, network.Upload, network.Connection, network.Latency, network.IPv4, network.IPv6, network.PublicIp };
        for (var index = 0; index < regions.Length; index++)
        {
            Assert.True(regions[index].Y + regions[index].Height <= body.Y + body.Height + .01);
            for (var other = index + 1; other < regions.Length; other++) Assert.False(regions[index].Intersects(regions[other]));
        }
    }
}
