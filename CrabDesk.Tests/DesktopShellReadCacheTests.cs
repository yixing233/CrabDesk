using CrabDesk.Native;

namespace CrabDesk.Tests;

/// <summary>
/// The desktop view and icon grid spacing both originate in explorer.exe.
/// These tests pin the published snapshots used by rendering so a redraw stays
/// independent of Explorer's current responsiveness.
/// </summary>
public sealed class DesktopShellReadCacheTests
{
    [Fact]
    public void RepeatedCachedViewReadsReturnTheSameSnapshot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var first = DesktopIconPositionService.GetCachedDesktopViewState();
        var second = DesktopIconPositionService.GetCachedDesktopViewState();

        Assert.Equal(first, second);
    }

    [Fact]
    public void ALiveReadRefreshesTheSnapshotTheRenderPathObserves()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var live = DesktopIconPositionService.GetDesktopViewState();

        Assert.Equal(live, DesktopIconPositionService.GetCachedDesktopViewState());
    }

    [Fact]
    public void InvalidatingTheSnapshotMakesTheNextReaderObserveExplorerAgain()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        DesktopIconPositionService.GetDesktopViewState();
        DesktopIconPositionService.InvalidateCachedDesktopView();

        Assert.Equal(
            DesktopIconPositionService.GetDesktopViewState(),
            DesktopIconPositionService.GetCachedDesktopViewState());
    }

    [Fact]
    public void AFailedSpacingReadIsCachedSoItIsNotRetriedPerRebuild()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // A window handle that does not exist fails the same way a Shell send
        // timeout does, and the failure must be remembered: the render path
        // keeps its last valid spacing instead of waiting on the timeout again.
        var missingListView = new IntPtr(-1);

        Assert.False(DesktopIconPositionService.TryGetItemSpacing(missingListView, out _));
        Assert.False(DesktopIconPositionService.TryGetCachedItemSpacing(missingListView, out var cached));
        Assert.Equal(default, cached);
    }

    [Fact]
    public void SpacingIsCachedPerListViewHandle()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        DesktopIconPositionService.TryGetItemSpacing(new IntPtr(-1), out _);

        // A different handle cannot answer from the previous handle's entry.
        Assert.False(DesktopIconPositionService.TryGetCachedItemSpacing(new IntPtr(-2), out var spacing));
        Assert.Equal(default, spacing);
    }

    [Fact]
    public void TheSpacingFallbackStaysAtExplorerDefaultGrid()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal(
            new System.Drawing.Size(88, 96),
            DesktopIconPositionService.GetItemSpacing(new IntPtr(-1)));
    }
}
