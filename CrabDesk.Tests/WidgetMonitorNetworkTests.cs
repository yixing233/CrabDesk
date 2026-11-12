using System.Diagnostics;
using System.Net.NetworkInformation;
using CrabDesk.Native;

namespace CrabDesk.Tests;

public sealed class WidgetMonitorNetworkTests
{
    [Fact]
    public void AutomaticSelectionUsesInternetLinkInsteadOfVirtualTunnelRoute()
    {
        WidgetNetworkMonitor.AdapterCandidate[] adapters =
        [new("tunnel", (NetworkInterfaceType)53, true), new("ethernet", NetworkInterfaceType.Ethernet, true),
            new("local-switch", NetworkInterfaceType.Ethernet, false)];
        Assert.Equal("ethernet", WidgetNetworkMonitor.SelectAutomaticAdapterId(adapters, "tunnel"));
        Assert.Equal("ethernet", WidgetNetworkMonitor.SelectAutomaticAdapterId(adapters, "ethernet"));
    }

    [Fact]
    public void AutomaticSelectionPrefersRoutedLinkAndKeepsTunnelOnlyFallback()
    {
        WidgetNetworkMonitor.AdapterCandidate[] adapters =
        [new("ethernet", NetworkInterfaceType.Ethernet, true), new("wifi", NetworkInterfaceType.Wireless80211, true),
            new("tunnel", (NetworkInterfaceType)53, true)];
        Assert.Equal("wifi", WidgetNetworkMonitor.SelectAutomaticAdapterId(adapters, "wifi"));
        Assert.Equal("tunnel", WidgetNetworkMonitor.SelectAutomaticAdapterId([adapters[2]], "tunnel"));
        Assert.Null(WidgetNetworkMonitor.SelectAutomaticAdapterId([], null));
    }

    [Fact]
    public void RatesUseActualElapsedTimeAndKeepDirectionsSeparate()
    {
        var sampler = new WidgetNetworkMonitor.RateSampler();
        Assert.Equal((null, null), sampler.Sample("adapter", 100, 200, 0));
        var result = sampler.Sample("adapter", 600, 350, Stopwatch.Frequency * 2);
        Assert.Equal(250d, result.Download);
        Assert.Equal(75d, result.Upload);
    }

    [Fact]
    public void AdapterChangeAndDisconnectStartNewBaselines()
    {
        var sampler = new WidgetNetworkMonitor.RateSampler();
        sampler.Sample("first", 100, 100, 0);
        Assert.Equal((null, null), sampler.Sample("second", 9000, 9000, Stopwatch.Frequency));
        Assert.Equal((100d, 200d), sampler.Sample("second", 9100, 9200, Stopwatch.Frequency * 2));
        sampler.Reset();
        Assert.Equal((null, null), sampler.Sample("second", 9200, 9300, Stopwatch.Frequency * 3));
    }

    [Fact]
    public void LongGapsAndCounterRollbackResetBothDirections()
    {
        var sampler = new WidgetNetworkMonitor.RateSampler();
        sampler.Sample("adapter", 1000, 1000, 0);
        Assert.Equal((null, null), sampler.Sample("adapter", 2000, 2000, Stopwatch.Frequency * 11));
        Assert.Equal((null, null), sampler.Sample("adapter", 100, 2100, Stopwatch.Frequency * 12));
        Assert.Equal((100d, 100d), sampler.Sample("adapter", 200, 2200, Stopwatch.Frequency * 13));
    }

    [Fact]
    public void DuplicateTimestampDoesNotFabricateAZeroRate()
    {
        var sampler = new WidgetNetworkMonitor.RateSampler();
        sampler.Sample("adapter", 100, 100, 0);
        Assert.Equal((null, null), sampler.Sample("adapter", 200, 200, 0));
        Assert.Equal((0d, 0d), sampler.Sample("adapter", 200, 200, Stopwatch.Frequency));
    }
}
