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

    [Theory]
    [InlineData(true, true, 1)]
    [InlineData(true, false, 1)]
    [InlineData(false, true, 1)]
    public void HeightAndScrollShareOnePresentation(bool height, bool scroll, int expected) =>
        Assert.Equal(expected, DesktopBoxForm.CalculateAnimationPresentCount(height, scroll));
}
