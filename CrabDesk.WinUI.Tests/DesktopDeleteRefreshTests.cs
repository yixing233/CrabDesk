using CrabDesk.Core;
using CrabDesk.Runtime;
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
    public void TheDeletedPathsComeFromTheBatchResultAfterTheFilesAreGone()
    {
        var method = ReadManagerMethod(
            "internal async Task DeleteSelectedItemsAsync()",
            "private void ReportDeleteOutcome(");

        var delete = method.IndexOf(
            "await _runtime.FileOperations.DeleteAsync(selection.DeletableItems);",
            StringComparison.Ordinal);
        var capture = method.IndexOf("deletedPaths = result.SucceededPaths", StringComparison.Ordinal);
        Assert.True(delete >= 0);
        Assert.True(capture > delete);

        // A failed item is still on the desktop, so only the paths the batch
        // actually removed may be reconciled as gone.
        Assert.DoesNotContain(
            "deletedPaths = selection.DeletableItems",
            method,
            StringComparison.Ordinal);
    }

    [Fact]
    public void APartialDeleteReportsTheFailuresAndOnlyRepaintsWhatLeft()
    {
        var method = ReadManagerMethod(
            "internal async Task DeleteSelectedItemsAsync()",
            "private void ReportDeleteOutcome(");

        // Refreshing is skipped entirely when nothing was removed, so a total
        // failure cannot blank cells that still hold real files.
        Assert.Contains("if (deleteAttempted && deletedPaths.Length > 0)", method, StringComparison.Ordinal);
        Assert.Contains("ReportDeleteOutcome(result, selection.BlockedCount);", method, StringComparison.Ordinal);
    }

    [Fact]
    public void DeleteOutcomeMessageNamesSuccessesFailuresAndSkippedItems()
    {
        var result = new FileDeleteBatchResult(
        [
            new FileDeleteItemResult(@"C:\Desktop\kept.txt", null),
            new FileDeleteItemResult(@"C:\Desktop\locked.txt", "文件正在使用中。")
        ]);

        var message = DesktopSurfaceManager.DescribeDeleteOutcome(result, blockedCount: 2);

        Assert.Contains("已删除 1 项", message, StringComparison.Ordinal);
        Assert.Contains("1 项失败", message, StringComparison.Ordinal);
        Assert.Contains("locked.txt", message, StringComparison.Ordinal);
        Assert.Contains("2 项系统桌面项目或只读映射目录已跳过", message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeleteOutcomeMessageStaysSilentAboutSkippedItemsWhenThereAreNone()
    {
        var result = new FileDeleteBatchResult(
        [
            new FileDeleteItemResult(@"C:\Desktop\gone.txt", null)
        ]);

        var message = DesktopSurfaceManager.DescribeDeleteOutcome(result, blockedCount: 0);

        Assert.Contains("已删除 1 项", message, StringComparison.Ordinal);
        Assert.DoesNotContain("已跳过", message, StringComparison.Ordinal);
    }

    [Fact]
    public void DroppingDesktopIconsOnTheRecycleBinRepaintsOnlyTheirCells()
    {
        var method = ReadMethod(
            "DesktopIconSurface.cs",
            "private async void CompleteRecycleBinDrop(DesktopIconSurfaceDragSession dragSession)",
            "private static bool TryGetExternalFileDrop(");

        var delete = method.IndexOf(
            "await _runtime.FileOperations.DeleteAsync(items);",
            StringComparison.Ordinal);
        var capture = method.IndexOf("result.SucceededPaths", StringComparison.Ordinal);
        Assert.True(delete >= 0);
        Assert.True(capture > delete);
        Assert.Contains(
            "await _runtime.RefreshAfterDesktopItemsDeletedAsync(result.SucceededPaths);",
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
            "await _runtime.RefreshAfterDesktopItemsDeletedAsync(result.SucceededPaths);",
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
