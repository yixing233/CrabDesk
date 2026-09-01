using CrabDesk.Core;

namespace CrabDesk.Tests;

public sealed class DesktopBoxAlignmentEngineTests
{
    private static readonly LayoutRect WorkArea = new(0, 0, 1200, 800);

    [Fact]
    public void AlignsLeftEdgeToPeerWhenWithinThreshold()
    {
        var moving = new LayoutRect(104, 250, 100, 100);
        var peer = new LayoutRect(100, 420, 160, 100);

        var result = DesktopBoxAlignmentEngine.Align(moving, [peer], WorkArea);

        Assert.Equal(100, result.X);
        Assert.Equal(moving.Y, result.Y);
        Assert.Equal(moving.Width, result.Width);
        Assert.Equal(moving.Height, result.Height);
    }

    [Fact]
    public void AlignsCenterLineToPeerWhenWithinThreshold()
    {
        var moving = new LayoutRect(202, 250, 100, 100);
        var peer = new LayoutRect(300, 180, 100, 100);

        var result = DesktopBoxAlignmentEngine.Align(moving, [peer], WorkArea);

        Assert.Equal(200, result.X);
        Assert.Equal(moving.Y, result.Y);
    }

    [Fact]
    public void AlignsTopEdgeToPeerIndependentlyOfHorizontalAlignment()
    {
        var moving = new LayoutRect(450, 104, 100, 100);
        var peer = new LayoutRect(700, 100, 160, 100);

        var result = DesktopBoxAlignmentEngine.Align(moving, [peer], WorkArea);

        Assert.Equal(moving.X, result.X);
        Assert.Equal(100, result.Y);
    }

    [Fact]
    public void AlignsToWorkAreaEdgesWhenWithinThreshold()
    {
        var moving = new LayoutRect(5, 694, 100, 100);

        var result = DesktopBoxAlignmentEngine.Align(moving, [], WorkArea);

        Assert.Equal(0, result.X);
        Assert.Equal(700, result.Y);
    }

    [Fact]
    public void LeavesPositionUnchangedOutsideThreshold()
    {
        var moving = new LayoutRect(120, 250, 100, 100);
        var peer = new LayoutRect(100, 420, 160, 100);

        var result = DesktopBoxAlignmentEngine.Align(moving, [peer], WorkArea);

        Assert.Equal(moving, result);
    }

    [Fact]
    public void IgnoresInvalidThresholdAndPreservesOriginalBounds()
    {
        var moving = new LayoutRect(104, 250, 100, 100);
        var peer = new LayoutRect(100, 420, 160, 100);

        var result = DesktopBoxAlignmentEngine.Align(moving, [peer], WorkArea, double.NaN);

        Assert.Equal(moving, result);
    }
}
