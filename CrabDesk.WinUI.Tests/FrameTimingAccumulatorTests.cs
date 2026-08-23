using CrabDesk.Runtime;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class FrameTimingAccumulatorTests
{
    [Fact]
    public void TimingAccumulatorReportsP95WithoutRetainingUnboundedSamples()
    {
        var timing = new FrameTimingAccumulator(20);
        for (var value = 1; value <= 100; value++)
        {
            timing.Add(value);
        }

        var snapshot = timing.SnapshotAndReset();

        Assert.Equal(20, snapshot.Count);
        Assert.InRange(snapshot.P95Milliseconds, 95, 100);
        Assert.Equal(0, timing.Count);
    }
}
