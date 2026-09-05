using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class DesktopDeleteRefreshTests
{
    [Fact]
    public void DeletingASelectionRepaintsOnlyTheVacatedCellsAndBoxes()
    {
        var method = ReadManagerMethod(
            "internal async Task DeleteSelectedItemsAsync()",
            "private async Task PasteToDesktopAsync()");

        Assert.Contains(
            "await _runtime.RefreshAfterDesktopItemsDeletedAsync(deletedPaths);",
            method,
            StringComparison.Ordinal);
        Assert.DoesNotContain("_runtime.RefreshItemsAsync(", method, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDeletedPathsAreCapturedBeforeTheFilesAreGone()
    {
        var method = ReadManagerMethod(
            "internal async Task DeleteSelectedItemsAsync()",
            "private async Task PasteToDesktopAsync()");

        var capture = method.IndexOf(
            "deletedPaths = selection.DeletableItems",
            StringComparison.Ordinal);
        var delete = method.IndexOf(
            "await _runtime.FileOperations.DeleteAsync(selection.DeletableItems);",
            StringComparison.Ordinal);
        Assert.True(capture >= 0);
        Assert.True(delete > capture);
    }

    [Fact]
    public void DroppingDesktopIconsOnTheRecycleBinRepaintsOnlyTheirCells()
    {
        var method = ReadMethod(
            "DesktopIconSurface.cs",
            "private async void CompleteRecycleBinDrop(DesktopIconSurfaceDragSession dragSession)",
            "private static bool TryGetExternalFileDrop(");

        var capture = method.IndexOf("var deletedPaths = items.Select(", StringComparison.Ordinal);
        var delete = method.IndexOf(
            "await _runtime.FileOperations.DeleteAsync(items);",
            StringComparison.Ordinal);
        Assert.True(capture >= 0);
        Assert.True(delete > capture);
        Assert.Contains(
            "await _runtime.RefreshAfterDesktopItemsDeletedAsync(deletedPaths);",
            method,
            StringComparison.Ordinal);
        Assert.DoesNotContain("_runtime.RefreshItemsAsync(", method, StringComparison.Ordinal);
    }

    [Fact]
    public void DeletingAnExternalDropRepaintsOnlyWhatTheSurfacesRendered()
    {
        var method = ReadMethod(
            "DesktopIconSurface.cs",
            "private async Task DeleteExternalDropToRecycleBinAsync(IReadOnlyList<string> paths)",
            "private void ClearExternalDragPreview()");

        Assert.Contains(
            "await _runtime.RefreshAfterDesktopItemsDeletedAsync(paths);",
            method,
            StringComparison.Ordinal);
        Assert.DoesNotContain("_runtime.RefreshItemsAsync(", method, StringComparison.Ordinal);
    }

    [Fact]
    public void ADeletionResolvesItsItemKeysBeforeReplacingTheSnapshot()
    {
        var method = ReadDeletionRefresh();

        var resolveKeys = method.IndexOf("ResolveDesktopItemKeys(paths)", StringComparison.Ordinal);
        var mappedSnapshot = method.IndexOf("GetMappedFolderSnapshot(box.Id)", StringComparison.Ordinal);
        var replaceSnapshot = method.IndexOf(
            "await RefreshItemsCoreAsync(refreshSurfaces: false, applyDesktopRules: false);",
            StringComparison.Ordinal);
        Assert.True(resolveKeys >= 0);
        Assert.True(mappedSnapshot > resolveKeys);
        Assert.True(replaceSnapshot > mappedSnapshot);
    }

    [Fact]
    public void ADeletionRegistersItsPathsSoTheWatcherDoesNotRefreshAgain()
    {
        var method = ReadDeletionRefresh();

        Assert.Contains("RegisterTargetedDesktopRefresh(paths);", method, StringComparison.Ordinal);
        Assert.DoesNotContain("refreshSurfaces: true", method, StringComparison.Ordinal);
    }

    [Fact]
    public void ADeletedItemThatBelongedToABoxRefreshesThatBox()
    {
        var method = ReadDeletionRefresh();

        Assert.Contains(
            "State.Assignments.TryGetValue(key, out var boxId)",
            method,
            StringComparison.Ordinal);
        Assert.Contains("!State.Assignments.ContainsKey(key)", method, StringComparison.Ordinal);
        Assert.Contains(
            "_surfaceManager?.RefreshBoxItems(changedBoxIds) == true",
            method,
            StringComparison.Ordinal);
        Assert.Contains(
            "_surfaceManager?.RefreshDesktopItemsRemoved(removedDesktopKeys) == true",
            method,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ADeletionInsideAMappedBoxIsMatchedByPathAgainstItsSnapshot()
    {
        var method = ReadDeletionRefresh();

        Assert.Contains("State.Boxes.Where(box => box.IsMappedFolder)", method, StringComparison.Ordinal);
        Assert.Contains("changedBoxIds.Add(box.Id);", method, StringComparison.Ordinal);
    }

    [Fact]
    public void ADeletionOfPathsNoSurfaceRenderedLeavesEverySurfaceUntouched()
    {
        var method = ReadDeletionRefresh();

        var noSurfaceRendered = method.IndexOf(
            "if (removedKeys.Count == 0 && changedBoxIds.Count == 0)",
            StringComparison.Ordinal);
        var targetedRefresh = method.IndexOf("var refreshedDesktop =", StringComparison.Ordinal);
        Assert.True(noSurfaceRendered >= 0);
        Assert.True(targetedRefresh > noSurfaceRendered);
    }

    [Fact]
    public void ADeletionThatRepaintedNothingFallsBackToTheFullPass()
    {
        var method = ReadDeletionRefresh();

        var succeeded = method.IndexOf(
            "if (refreshedDesktop && refreshedBoxes)",
            StringComparison.Ordinal);
        var fallback = method.IndexOf("_surfaceManager?.Refresh();", StringComparison.Ordinal);
        Assert.True(succeeded >= 0);
        Assert.True(fallback > succeeded);
    }

    private static string ReadDeletionRefresh() => ReadRuntimeMethod(
        "internal async Task RefreshAfterDesktopItemsDeletedAsync(IEnumerable<string> deletedPaths)",
        "private IReadOnlyCollection<string> ResolveDesktopItemKeys(");

    private static string ReadRuntimeMethod(string startAnchor, string endAnchor) =>
        ReadMethod("CrabDeskRuntime.cs", startAnchor, endAnchor);

    private static string ReadManagerMethod(string startAnchor, string endAnchor) =>
        ReadMethod("DesktopSurfaceManager.cs", startAnchor, endAnchor);

    private static string ReadMethod(string fileName, string startAnchor, string endAnchor)
    {
        var source = File.ReadAllText(Path.Combine(
            FindSolutionDirectory(),
            "CrabDesk.Runtime",
            fileName));
        var methodStart = source.IndexOf(startAnchor, StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            endAnchor,
            Math.Max(0, methodStart),
            StringComparison.Ordinal);

        Assert.True(methodStart >= 0, $"{startAnchor} was not found in {fileName}.");
        Assert.True(methodEnd > methodStart, $"{endAnchor} was not found after {startAnchor}.");
        return source[methodStart..methodEnd];
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
