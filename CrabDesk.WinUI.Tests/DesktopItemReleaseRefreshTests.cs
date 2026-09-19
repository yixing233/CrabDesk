using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class DesktopItemReleaseRefreshTests
{
    [Fact]
    public void BoxDropOnDesktopUsesOneTargetedReleaseTransaction()
    {
        var solutionDirectory = FindSolutionDirectory();
        var runtimeSource = File.ReadAllText(Path.Combine(
            solutionDirectory,
            "CrabDesk.Runtime",
            "CrabDeskRuntime.cs"));
        var releaseCoreStart = runtimeSource.IndexOf(
            "private bool ReleaseAssignedItemsToDesktopCore(",
            StringComparison.Ordinal);
        var releaseCoreEnd = runtimeSource.IndexOf(
            "private void RestoreLegacyAssignedDesktopItemVisibility(",
            Math.Max(0, releaseCoreStart),
            StringComparison.Ordinal);

        Assert.True(releaseCoreStart >= 0);
        Assert.True(releaseCoreEnd > releaseCoreStart);
        var releaseCore = runtimeSource[releaseCoreStart..releaseCoreEnd];
        Assert.Contains("NotifyDesktopItemReleaseChanged(", releaseCore, StringComparison.Ordinal);
        Assert.DoesNotContain("NotifyWorkspaceChanged(true);", releaseCore, StringComparison.Ordinal);

        var iconSource = File.ReadAllText(Path.Combine(
            solutionDirectory,
            "CrabDesk.Runtime",
            "DesktopIconSurface.cs"));
        var dropStart = iconSource.IndexOf(
            "private async void OnDragDrop(",
            StringComparison.Ordinal);
        var dropEnd = iconSource.IndexOf(
            "private bool TryGetVirtualBoxDrag(",
            Math.Max(0, dropStart),
            StringComparison.Ordinal);

        Assert.True(dropStart >= 0);
        Assert.True(dropEnd > dropStart);
        var dropMethod = iconSource[dropStart..dropEnd];
        Assert.Contains(
            "_runtime.ReleaseAssignedItemsToDesktopAtDropAsync(",
            dropMethod,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "_runtime.SetDesktopIconLayout(layout);",
            dropMethod,
            StringComparison.Ordinal);

        var managerSource = File.ReadAllText(Path.Combine(
            solutionDirectory,
            "CrabDesk.Runtime",
            "DesktopSurfaceManager.cs"));
        Assert.Contains(
            "RefreshDesktopItemRelease(",
            managerSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "RefreshReleasedItems(",
            iconSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BoxFormReleaseUsesSourceBoxIdsInsteadOfAWorkspaceRefresh()
    {
        var solutionDirectory = FindSolutionDirectory();
        var runtimeSource = File.ReadAllText(Path.Combine(
            solutionDirectory,
            "CrabDesk.Runtime",
            "CrabDeskRuntime.cs"));
        var releaseStart = runtimeSource.IndexOf(
            "public Task<bool> ReleaseAssignedItemsToDesktopAsync(",
            StringComparison.Ordinal);
        var releaseEnd = runtimeSource.IndexOf(
            "internal static bool PathsOverlapForTargetedDesktopRefresh(",
            Math.Max(0, releaseStart),
            StringComparison.Ordinal);

        Assert.True(releaseStart >= 0);
        Assert.True(releaseEnd > releaseStart);
        var releaseMethod = runtimeSource[releaseStart..releaseEnd];
        Assert.Contains("sourceBoxIds", releaseMethod, StringComparison.Ordinal);
        Assert.Contains(
            "NotifyDesktopItemReleaseChanged(unassignedKeys, sourceBoxIds);",
            releaseMethod,
            StringComparison.Ordinal);
        Assert.DoesNotContain("UnassignItems(", releaseMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("NotifyWorkspaceChanged(true);", releaseMethod, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopFolderDropRefreshesOnlyTheMovedDesktopItems()
    {
        var solutionDirectory = FindSolutionDirectory();
        var runtimeSource = File.ReadAllText(Path.Combine(
            solutionDirectory,
            "CrabDesk.Runtime",
            "CrabDeskRuntime.cs"));
        var importStart = runtimeSource.IndexOf(
            "internal async Task<FileImportBatchResult> ImportDesktopItemsIntoFolderAsync(",
            StringComparison.Ordinal);
        var importEnd = runtimeSource.IndexOf(
            "public async Task<FileImportBatchResult> TransferBoxItemsAsync(",
            Math.Max(0, importStart),
            StringComparison.Ordinal);

        Assert.True(importStart >= 0);
        Assert.True(importEnd > importStart);
        var importMethod = runtimeSource[importStart..importEnd];
        Assert.Contains("RefreshItemsCoreAsync(", importMethod, StringComparison.Ordinal);
        Assert.Contains("refreshSurfaces: false", importMethod, StringComparison.Ordinal);
        Assert.Contains("RefreshDesktopItemsRemoved(", importMethod, StringComparison.Ordinal);
        Assert.Contains("RegisterTargetedDesktopRefresh(", importMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("NotifyWorkspaceChanged(true);", importMethod, StringComparison.Ordinal);

        var iconSource = File.ReadAllText(Path.Combine(
            solutionDirectory,
            "CrabDesk.Runtime",
            "DesktopIconSurface.cs"));
        Assert.Contains("RefreshRemovedItems(", iconSource, StringComparison.Ordinal);
        Assert.Contains("ImportDesktopItemsIntoFolderAsync(", iconSource, StringComparison.Ordinal);
        Assert.Contains("ShouldSuppressTargetedDesktopRefresh(", runtimeSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplorerMoveOutOfDesktopReconcilesOnlyTheMovedItems()
    {
        var solutionDirectory = FindSolutionDirectory();
        var iconSource = File.ReadAllText(Path.Combine(
            solutionDirectory,
            "CrabDesk.Runtime",
            "DesktopIconSurface.cs"));
        var dragStart = iconSource.IndexOf(
            "private bool TryStartDesktopOleDrag()",
            StringComparison.Ordinal);
        var dragEnd = iconSource.IndexOf(
            "private void UpdateDesktopOleDropPreview(",
            Math.Max(0, dragStart),
            StringComparison.Ordinal);

        Assert.True(dragStart >= 0);
        Assert.True(dragEnd > dragStart);
        var dragMethod = iconSource[dragStart..dragEnd];
        Assert.Contains("var completedEffect =", dragMethod, StringComparison.Ordinal);
        // Explorer's folder-to-folder rule copies across volumes; the preferred effect
        // makes leaving the desktop a move regardless, so the source icon goes away.
        Assert.Contains("FileClipboardCodec.WritePreferredDropEffect(data, move: true)", dragMethod, StringComparison.Ordinal);
        Assert.Contains("_runtime.ReconcileExternalDesktopMoveAsync(", dragMethod, StringComparison.Ordinal);
        Assert.Contains("!dragSession.HandledByBox", dragMethod, StringComparison.Ordinal);
        Assert.Contains("!dragSession.HandledByDesktop", dragMethod, StringComparison.Ordinal);

        var runtimeSource = File.ReadAllText(Path.Combine(
            solutionDirectory,
            "CrabDesk.Runtime",
            "CrabDeskRuntime.cs"));
        var reconcileStart = runtimeSource.IndexOf(
            "internal async Task ReconcileExternalDesktopMoveAsync(",
            StringComparison.Ordinal);
        var reconcileEnd = runtimeSource.IndexOf(
            "public async Task<FileImportBatchResult> TransferBoxItemsAsync(",
            Math.Max(0, reconcileStart),
            StringComparison.Ordinal);

        Assert.True(reconcileStart >= 0);
        Assert.True(reconcileEnd > reconcileStart);
        var reconcileMethod = runtimeSource[reconcileStart..reconcileEnd];
        Assert.Contains("refreshSurfaces: false", reconcileMethod, StringComparison.Ordinal);
        Assert.Contains("RefreshDesktopItemsRemoved(", reconcileMethod, StringComparison.Ordinal);
        Assert.Contains(
            "Environment.SpecialFolder.DesktopDirectory",
            reconcileMethod,
            StringComparison.Ordinal);
        Assert.DoesNotContain("RefreshItemsAsync(", reconcileMethod, StringComparison.Ordinal);
    }

    private static string FindSolutionDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CrabDesk.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the CrabDesk solution directory.");
    }
}
