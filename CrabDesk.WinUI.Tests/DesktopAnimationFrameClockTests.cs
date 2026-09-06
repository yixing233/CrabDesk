using CrabDesk.Runtime;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class DesktopAnimationFrameClockTests
{
    [Fact]
    public void FrameClockStartsOnlyWhenWorkIsRequested()
    {
        using var clock = new DesktopAnimationFrameClock();

        Assert.False(clock.Enabled);
        clock.RequestFrames();
        Assert.True(clock.Enabled);
        clock.StopWhenIdle(hasActiveAnimation: false);
        Assert.False(clock.Enabled);
    }

    [Fact]
    public void TheFrameIntervalStaysInsideOneSystemTick()
    {
        // A WinForms timer is a USER32 timer, so it can only tick on a system
        // clock boundary — roughly every 15.625 ms. An interval of 16 asks for a
        // boundary that has just passed and waits for the following one, which
        // delivers a frame every other tick: measured at a 30.5 ms median with
        // an 8/35 ms spread, so half the intended rate and visibly uneven. 15
        // lands on a single boundary at a measured 15.8 ms median.
        Assert.True(
            DesktopAnimationFrameClock.IntervalMilliseconds < 15.625,
            "An interval past the tick boundary costs two ticks for every frame.");
    }

    [Theory]
    [InlineData(true, true, 1)]
    [InlineData(true, false, 1)]
    [InlineData(false, true, 1)]
    public void HeightAndScrollShareOnePresentation(bool height, bool scroll, int expected) =>
        Assert.Equal(expected, DesktopBoxForm.CalculateAnimationPresentCount(height, scroll));
}
