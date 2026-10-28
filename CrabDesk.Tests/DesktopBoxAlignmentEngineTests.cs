using CrabDesk.Core;

namespace CrabDesk.Tests;

public sealed class DesktopBoxAlignmentEngineTests
{
    private static readonly LayoutRect WorkArea = new(0, 0, 1200, 800);

    [Fact]
    public void AlignsLeftEdgeToNearbyPeerAndReturnsMatchingGuide()
    {
        var moving = new LayoutRect(104, 250, 100, 100);
        var result = DesktopBoxAlignmentEngine.Calculate(moving, [new(100, 390, 160, 100)], WorkArea);
        Assert.Equal(moving with { X = 100 }, result.Bounds);
        var guide = Assert.Single(result.Guides);
        Assert.True(guide.IsVertical);
        Assert.Equal(100, guide.Position);
        Assert.Equal(250, guide.Start);
        Assert.Equal(490, guide.End);
    }

    [Fact]
    public void AlignsCenterLineToNearbyPeer()
    {
        var result = DesktopBoxAlignmentEngine.Calculate(new(128, 100, 100, 100),
            [new(100, 230, 160, 100)], WorkArea);
        Assert.Equal(130, result.Bounds.X);
        Assert.Equal(180, Assert.Single(result.Guides).Position);
    }

    [Fact]
    public void AlignsTopEdgeIndependentlyOfHorizontalAlignment()
    {
        var moving = new LayoutRect(450, 104, 100, 100);
        var result = DesktopBoxAlignmentEngine.Calculate(moving, [new(590, 100, 160, 100)], WorkArea);
        Assert.Equal(moving with { Y = 100 }, result.Bounds);
        Assert.False(Assert.Single(result.Guides).IsVertical);
    }

    [Fact]
    public void DoesNotMagnetizeWorkAreaEdgesOrRoundFreePositionToGrid()
    {
        var moving = new LayoutRect(5.6, 693.2, 100, 100);
        var result = DesktopBoxAlignmentEngine.Calculate(moving, [], WorkArea);
        Assert.Equal(moving, result.Bounds);
        Assert.Empty(result.Guides);
    }

    [Fact]
    public void ConstrainsOutsideBoundsWithoutInventingGuides()
    {
        var result = DesktopBoxAlignmentEngine.Calculate(new(-4, 705, 100, 100), [], WorkArea);
        Assert.Equal(new LayoutRect(0, 700, 100, 100), result.Bounds);
        Assert.Empty(result.Guides);
    }

    [Fact]
    public void AlignsToADistantVerticalEdgeAndSpansTheFullGap()
    {
        var moving = new LayoutRect(104, 100, 100, 100);
        var result = DesktopBoxAlignmentEngine.Calculate(moving, [new(100, 600, 160, 100)], WorkArea);
        Assert.Equal(moving with { X = 100 }, result.Bounds);
        Assert.Equal(new DesktopBoxAlignmentGuide(true, 100, 100, 700), Assert.Single(result.Guides));
    }

    [Fact]
    public void AlignsToADistantHorizontalEdgeAndSpansTheFullGap()
    {
        var result = DesktopBoxAlignmentEngine.Calculate(new(100, 104, 100, 100),
            [new(1000, 100, 100, 160)], WorkArea);
        Assert.Equal(new LayoutRect(100, 100, 100, 100), result.Bounds);
        Assert.Equal(new DesktopBoxAlignmentGuide(false, 100, 100, 1100), Assert.Single(result.Guides));
    }

    [Fact]
    public void AlignsCenterLinesAcrossTheMonitor()
    {
        var result = DesktopBoxAlignmentEngine.Calculate(new(128, 100, 100, 100),
            [new(100, 600, 160, 100)], WorkArea);
        Assert.Equal(130, result.Bounds.X);
        Assert.Equal(new DesktopBoxAlignmentGuide(true, 180, 100, 700), Assert.Single(result.Guides));
    }

    [Fact]
    public void LeavesPositionUnchangedOutsideThreshold()
    {
        var moving = new LayoutRect(120, 250, 100, 100);
        var result = DesktopBoxAlignmentEngine.Calculate(moving, [new(100, 390, 160, 100)], WorkArea);
        Assert.Equal(moving, result.Bounds);
        Assert.Empty(result.Guides);
    }

    [Theory]
    [InlineData(108, 100, true)]
    [InlineData(108.01, 108.01, false)]
    public void ThresholdIsInclusiveAndReleasesImmediatelyOutsideIt(double x, double expected, bool guide)
    {
        var result = DesktopBoxAlignmentEngine.Calculate(new(x, 100, 100, 100),
            [new(100, 230, 160, 100)], WorkArea);
        Assert.Equal(expected, result.Bounds.X);
        Assert.Equal(guide, result.Guides.Count > 0);
    }

    [Theory]
    [InlineData(248)]
    [InlineData(248.01)]
    [InlineData(650)]
    public void AlignmentDoesNotDependOnTheGapBetweenCards(double peerY)
    {
        var result = DesktopBoxAlignmentEngine.Calculate(new(104, 100, 100, 100),
            [new(100, peerY, 160, 100)], WorkArea);
        Assert.Equal(100, result.Bounds.X);
        Assert.NotEmpty(result.Guides);
    }

    [Fact]
    public void TiesDoNotDependOnStackOrder()
    {
        var peers = new[] { new LayoutRect(100, 230, 100, 100), new LayoutRect(108, 230, 100, 100) };
        var first = DesktopBoxAlignmentEngine.Calculate(new(104, 100, 100, 100), peers, WorkArea);
        var second = DesktopBoxAlignmentEngine.Calculate(new(104, 100, 100, 100), peers.Reverse(), WorkArea);
        Assert.Equal(first.Bounds, second.Bounds);
        Assert.Equal(first.Guides.ToArray(), second.Guides.ToArray());
    }


    [Fact]
    public void CalculatesBothAxesAndBothReferenceLinesWhenNearby()
    {
        var result = DesktopBoxAlignmentEngine.Calculate(new(104, 104, 100, 100),
            [new(100, 244, 160, 100), new(244, 100, 100, 160)], WorkArea);
        Assert.Equal(new LayoutRect(100, 100, 100, 100), result.Bounds);
        Assert.Equal(2, result.Guides.Count);
        Assert.Contains(result.Guides, guide => guide.IsVertical && guide.Position == 100);
        Assert.Contains(result.Guides, guide => !guide.IsVertical && guide.Position == 100);
    }

    [Fact]
    public void ChoosesTheClosestAlignmentLineEvenWhenItsCardIsFartherAway()
    {
        var result = DesktopBoxAlignmentEngine.Calculate(new(104, 100, 100, 100),
            [new(101, 600, 100, 100), new(108, 230, 100, 100)], WorkArea);
        Assert.Equal(101, result.Bounds.X);
        Assert.Equal(700, Assert.Single(result.Guides).End);
    }

    [Fact]
    public void EqualAlignmentDistancesPreferTheNearerCardAndStayStableAcrossStackOrder()
    {
        var moving = new LayoutRect(104, 100, 100, 100);
        var peers = new[] { new LayoutRect(100, 600, 100, 100), new LayoutRect(108, 230, 100, 100) };
        var first = DesktopBoxAlignmentEngine.Calculate(moving, peers, WorkArea);
        var second = DesktopBoxAlignmentEngine.Calculate(moving, peers.Reverse(), WorkArea);
        Assert.Equal(108, first.Bounds.X);
        Assert.Equal(330, Assert.Single(first.Guides).End);
        Assert.Equal(first.Bounds, second.Bounds);
        Assert.Equal(first.Guides.ToArray(), second.Guides.ToArray());
    }
    [Fact]
    public void GuidesCannotDescribeAnAlignmentThatWouldBeClampedAway()
    {
        var result = DesktopBoxAlignmentEngine.Calculate(new(3, 100, 100, 100),
            [new(98, 230, 160, 100)], WorkArea);
        Assert.Equal(3, result.Bounds.X);
        Assert.Empty(result.Guides);
    }

    [Fact]
    public void InvalidInputsDoNotCreateGuides()
    {
        var moving = new LayoutRect(104, 250, 100, 100);
        var result = DesktopBoxAlignmentEngine.Calculate(moving, [], WorkArea, double.NaN);
        Assert.Equal(moving, result.Bounds);
        Assert.Empty(result.Guides);
        Assert.Empty(DesktopBoxAlignmentEngine.Calculate(moving,
            [new(double.NaN, 250, 100, 100)], WorkArea).Guides);
    }
}
