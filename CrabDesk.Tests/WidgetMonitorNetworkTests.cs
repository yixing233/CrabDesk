using System.Diagnostics;
using CrabDesk.Native;

namespace CrabDesk.Tests;

public sealed class WidgetMonitorNetworkTests
{
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
