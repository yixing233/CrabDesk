using System.Diagnostics;
using System.Net;
using System.Text.Json;
using CrabDesk.Core;
using CrabDesk.Native;

namespace CrabDesk.Tests;

public sealed class WidgetNetworkProcessTests
{
    [Fact]
    public void RankingUsesChosenDirectionAndOmitsIdleAndInvalidReadings()
    {
        var snapshot = new WidgetNetworkProcesses(NetworkProcessStatus.Ready,
            [new(1, "browser", 1000, 10), new(2, "sync", 20, 2000),
             new(3, "game", 100, 30), new(4, "idle", 0, 0),
             new(5, "invalid", double.NaN, double.PositiveInfinity), new(6, "negative", -1, -1),
             new(7, "chat", 50, 40)]);
        Assert.Equal([1, 3, 7], snapshot.Top(NetworkProcessSort.Download).Select(process => process.ProcessId));
        Assert.Equal([2, 7, 3], snapshot.Top(NetworkProcessSort.Upload).Select(process => process.ProcessId));
    }

    [Fact]
    public void SortPersistsButLiveProcessDataDoesNot()
    {
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Network, NetworkProcessSort = NetworkProcessSort.Upload,
            Network = new(DateTimeOffset.Now) { Processes = new(NetworkProcessStatus.Ready, [new(123, "private-app", 1, 2)]) } };
        var json = JsonSerializer.Serialize(widget);
        var loaded = JsonSerializer.Deserialize<DesktopWidget>(json)!;
        DesktopWidgets.Normalize(new CrabDeskState { Widgets = [loaded] });
        Assert.Equal(NetworkProcessSort.Upload, loaded.NetworkProcessSort);
        Assert.DoesNotContain("private-app", json);
        Assert.Null(loaded.Network);
        loaded.NetworkProcessSort = (NetworkProcessSort)123;
        DesktopWidgets.Normalize(new CrabDeskState { Widgets = [loaded] });
        Assert.Equal(NetworkProcessSort.Download, loaded.NetworkProcessSort);
    }

    [Fact]
    public void TrafficUsesActualElapsedTimeAndCombinesProtocolsByProcess()
    {
        var traffic = new WidgetNetworkProcessAccumulator(id => $"process-{id}");
        Assert.Equal(NetworkProcessStatus.WarmingUp, traffic.Sample(0).Status);
        traffic.Record(1, 1000, false, IPAddress.Parse("203.0.113.5"), IPAddress.Parse("192.0.2.1"));
        traffic.Record(1, 2000, false, IPAddress.Parse("2001:db8::5"), IPAddress.Parse("2001:db8::1"));
        traffic.Record(1, 600, true, IPAddress.Parse("192.0.2.1"), IPAddress.Parse("203.0.113.5"));
        var result = Assert.Single(traffic.Sample(Stopwatch.Frequency * 2).Processes);
        Assert.Equal("process-1", result.Name);
        Assert.Equal(1500, result.DownloadBytesPerSecond);
        Assert.Equal(300, result.UploadBytesPerSecond);
        Assert.Empty(traffic.Sample(Stopwatch.Frequency * 3).Processes);
    }

    [Theory]
    [InlineData("127.0.0.1")] [InlineData("::1")] [InlineData("::ffff:127.0.0.1")]
    public void LoopbackIsExcludedInBothDirections(string address)
    {
        var traffic = new WidgetNetworkProcessAccumulator();
        traffic.Sample(0);
        traffic.Record(1, 100, true, IPAddress.Parse(address), IPAddress.Parse("192.0.2.1"));
        traffic.Record(1, 100, false, IPAddress.Parse("192.0.2.1"), IPAddress.Parse(address));
        Assert.Empty(traffic.Sample(Stopwatch.Frequency).Processes);
    }

    [Fact]
    public void SamplingGapAndResetDiscardStaleTraffic()
    {
        var traffic = new WidgetNetworkProcessAccumulator(id => id.ToString());
        traffic.Sample(0);
        traffic.Record(1, 1000, false, IPAddress.Parse("192.0.2.1"), IPAddress.Parse("203.0.113.5"));
        Assert.Equal(NetworkProcessStatus.WarmingUp, traffic.Sample(Stopwatch.Frequency * 10).Status);
        Assert.Empty(traffic.Sample(Stopwatch.Frequency * 11).Processes);
        traffic.Reset();
        Assert.Equal(NetworkProcessStatus.WarmingUp, traffic.Sample(Stopwatch.Frequency * 12).Status);
    }

    [Fact]
    public void MissingPrivilegeLeavesAdapterSamplingAvailable()
    {
        if (Microsoft.Diagnostics.Tracing.Session.TraceEventSession.IsElevated() == true) return;
        using var monitor = new WidgetNetworkMonitor();
        var snapshot = monitor.Sample("", "", includeProcesses: true);
        Assert.Equal(NetworkProcessStatus.PermissionRequired, snapshot.Processes.Status);
        Assert.Empty(snapshot.Processes.Processes);
        Assert.NotNull(monitor.Sample("", ""));
    }
}
