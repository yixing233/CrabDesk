using CrabDesk.Core;

namespace CrabDesk.Tests;

public sealed class DesktopIconGridLayoutTests
{
    [Fact]
    public void AlignSnapsOutlierToDominantExplorerGrid()
    {
        var positions = new[]
        {
            new DesktopIconPositionSnapshot("A", 20, 2),
            new DesktopIconPositionSnapshot("B", 96, 87),
            new DesktopIconPositionSnapshot("C", 178, 174)
        };

        var aligned = DesktopIconGridLayout.Align(positions, 76, 85);

        Assert.Equal((20, 2), (aligned[0].X, aligned[0].Y));
        Assert.Equal((96, 87), (aligned[1].X, aligned[1].Y));
        Assert.Equal((172, 172), (aligned[2].X, aligned[2].Y));
    }

    [Fact]
    public void AlignMovesRoundedCollisionsToNearestFreeCells()
    {
        var positions = new[]
        {
            new DesktopIconPositionSnapshot("A", 20, 2),
            new DesktopIconPositionSnapshot("B", 30, 10)
        };

        var aligned = DesktopIconGridLayout.Align(positions, 76, 85);

        Assert.Equal(2, aligned.Select(position => (position.X, position.Y)).Distinct().Count());
        Assert.All(aligned, position =>
        {
            Assert.Equal(20, position.X % 76);
            Assert.Equal(2, position.Y % 85);
        });
    }
}
