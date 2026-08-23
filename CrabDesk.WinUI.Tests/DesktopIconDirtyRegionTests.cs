using System.Drawing;
using CrabDesk.Runtime;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class DesktopIconDirtyRegionTests
{
    [Fact]
    public void DirtySelectionReturnsOnlyIntersectingVisuals()
    {
        var visuals = new[]
        {
            new RectangleF(0, 0, 40, 40),
            new RectangleF(100, 100, 40, 40),
            new RectangleF(200, 200, 40, 40)
        };

        Assert.Equal(
            new[] { 1 },
            DesktopIconSurface.SelectDirtyItemIndexes(visuals, new RectangleF(95, 95, 60, 60)));
    }

    [Fact]
    public void FocusReorderPreservesItemGeometry()
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };

        Assert.Equal(
            new[] { ids[0], ids[2], ids[1] },
            DesktopBoxForm.OrderFocusedGeometry(ids, ids[1]));
    }
}
