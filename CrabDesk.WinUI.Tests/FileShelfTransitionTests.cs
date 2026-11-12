using CrabDesk.Core;
using CrabDesk.WinUI.Windows;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class FileShelfTransitionTests
{
    [Fact]
    public void OpeningStartsGentlyBeforeReachingFullSpeed()
    {
        var motion = new FileShelfTransition();
        motion.Reset(false);
        motion.SetTarget(true);
        motion.Advance(.05);
        Assert.InRange(motion.Progress, .1, .28);
        motion.Advance(.2);
        Assert.InRange(motion.Progress, .9, 1);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(144)]
    public void BothDirectionsSettleWithoutOvershootAtDifferentFrameRates(int framesPerSecond)
    {
        var motion = new FileShelfTransition();
        foreach (var expanded in new[] { false, true })
        {
            motion.SetTarget(expanded);
            var previous = motion.Progress;
            for (var frame = 0; frame < framesPerSecond && motion.IsRunning; frame++)
            {
                motion.Advance(1d / framesPerSecond);
                Assert.InRange(motion.Progress, 0, 1);
                Assert.True(expanded ? motion.Progress >= previous : motion.Progress <= previous);
                previous = motion.Progress;
            }
            Assert.False(motion.IsRunning);
            Assert.Equal(expanded ? 1d : 0d, motion.Progress);
        }
    }

    [Fact]
    public void ReversalPreservesPositionAndMomentum()
    {
        var motion = new FileShelfTransition();
        motion.SetTarget(false);
        motion.Advance(.06);
        var progress = motion.Progress;
        motion.SetTarget(true);
        Assert.Equal(progress, motion.Progress);
        // Reversing first decelerates the existing motion instead of snapping.
        motion.Advance(.001);
        Assert.InRange(motion.Progress, progress - .02, progress);
        motion.Advance(1);
        Assert.Equal(1, motion.Progress);
        Assert.False(motion.IsRunning);
    }

    [Fact]
    public void DuplicateHoverRequestsDoNotRestartExpansion()
    {
        var motion = new FileShelfTransition();
        motion.Reset(false);
        motion.SetTarget(true);
        for (var frame = 0; frame < 60; frame++)
        {
            motion.SetTarget(true);
            motion.Advance(1d / 60);
        }
        Assert.Equal(1, motion.Progress);
        Assert.False(motion.IsRunning);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImmediateChangesClearMotionAndKeepExactEndpoint(bool expanded)
    {
        var motion = new FileShelfTransition();
        motion.SetTarget(false);
        motion.Advance(.04);
        motion.Reset(expanded);
        motion.Advance(.1);
        Assert.Equal(expanded ? 1d : 0d, motion.Progress);
        Assert.False(motion.IsRunning);
    }

    [Theory]
    [InlineData(FileShelfDockEdge.Left, 1)]
    [InlineData(FileShelfDockEdge.Right, 1.5)]
    [InlineData(FileShelfDockEdge.Top, 2)]
    [InlineData(FileShelfDockEdge.Bottom, 1.25)]
    public void IntermediateBoundsKeepTheDockEdgeFixed(FileShelfDockEdge edge, double scale)
    {
        var area = new LayoutRect(-1920, -200, 1920, 1080);
        var expanded = FileShelfPlacement.Dock(new(-1500, 20, 380 * scale, 420 * scale), area, edge);
        var collapsed = FileShelfPlacement.Collapsed(expanded, area, edge, scale);
        Assert.Equal(collapsed, FileShelfTransition.Interpolate(collapsed, expanded, 0));
        Assert.Equal(expanded, FileShelfTransition.Interpolate(collapsed, expanded, 1));
        for (var step = 1; step < 20; step++)
        {
            var bounds = FileShelfTransition.Interpolate(collapsed, expanded, step / 20d);
            Assert.InRange(bounds.Width, collapsed.Width, expanded.Width);
            Assert.InRange(bounds.Height, collapsed.Height, expanded.Height);
            Assert.Equal(bounds, FileShelfPlacement.Dock(bounds, area, edge));
        }
    }
}
