using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class DesktopPasteRefreshTests
{
    [Fact]
    public void DesktopPasteRepaintsOnlyTheImportedDesktopCells()
    {
        var method = ReadRuntimeMethod(
            "public async Task<FileImportBatchResult> PasteToDesktopAsync()",
            "public async Task<BoxPasteResult> PasteIntoBoxAsync(Guid boxId, Guid? targetTabId = null)");

        Assert.Contains(
            "await RefreshItemsCoreAsync(refreshSurfaces: false, applyDesktopRules: false);",
            method,
            StringComparison.Ordinal);
        Assert.Contains("NotifyDesktopItemsAdded(imported.ImportedPaths);", method, StringComparison.Ordinal);
        Assert.DoesNotContain("refreshSurfaces: true", method, StringComparison.Ordinal);
        Assert.DoesNotContain("NotifyWorkspaceChanged(true);", method, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnmatchedImportedPathStillFallsBackToTheFullSurfacePass()
    {
        var method = ReadRuntimeMethod(
            "private void NotifyDesktopItemsAdded(",
            "private IReadOnlyCollection<string> ResolveDesktopItemKeys(");

        Assert.Contains("ResolveDesktopItemKeys(importedPaths)", method, StringComparison.Ordinal);
        Assert.Contains("addedKeys.Count != importedPaths.Count", method, StringComparison.Ordinal);
        Assert.Contains("NotifyWorkspaceChanged(true);", method, StringComparison.Ordinal);
        Assert.Contains("RefreshDesktopItemsAdded(desktopKeys)", method, StringComparison.Ordinal);
    }

    [Fact]
    public void AnImportedFileClaimedByAStoredAssignmentRefreshesThatBox()
    {
        var method = ReadRuntimeMethod(
            "private void NotifyDesktopItemsAdded(",
            "private IReadOnlyCollection<string> ResolveDesktopItemKeys(");

        Assert.Contains("State.Assignments.TryGetValue(key, out var boxId)", method, StringComparison.Ordinal);
        Assert.Contains(
            "!State.Assignments.ContainsKey(key)",
            method,
            StringComparison.Ordinal);
        Assert.Contains("manager.RefreshBoxItems(assignedBoxIds)", method, StringComparison.Ordinal);
        Assert.Contains("return refreshedDesktop && refreshedBoxes;", method, StringComparison.Ordinal);
    }

    [Fact]
    public void AssigningDesktopItemsToABoxRefreshesThatBoxAndTheVacatedCells()
    {
        var assignCore = ReadRuntimeMethod(
            "private int AssignItemsCore(",
            "internal int AssignDesktopItemsAtDrop(");

        Assert.Contains(
            "NotifyDesktopItemsAssignedToBox(boxId, assignedKeys);",
            assignCore,
            StringComparison.Ordinal);
        Assert.DoesNotContain("NotifyWorkspaceChanged(true);", assignCore, StringComparison.Ordinal);

        var pasteIntoBox = ReadRuntimeMethod(
            "public async Task<BoxPasteResult> PasteIntoBoxAsync(Guid boxId, Guid? targetTabId = null)",
            "private static Guid? ResolveManualTabTarget(");
        Assert.Contains(
            "NotifyDesktopItemsAssignedToBox(boxId, assignedKeys);",
            pasteIntoBox,
            StringComparison.Ordinal);
        Assert.DoesNotContain("NotifyWorkspaceChanged(true);", pasteIntoBox, StringComparison.Ordinal);

        var managerMethod = ReadManagerMethod(
            "internal bool RefreshDesktopItemsAssigned(",
            "internal void SetDesktopIconsVisible(");
        Assert.Contains("surface.RefreshBoxItems(boxId)", managerMethod, StringComparison.Ordinal);
        Assert.Contains(
            "iconSurface.RefreshReleasedItems(assignedItemKeys)",
            managerMethod,
            StringComparison.Ordinal);
        Assert.Contains("return refreshedBox && refreshedDesktop;", managerMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("Refresh();", managerMethod, StringComparison.Ordinal);
    }

    [Fact]
    public void ATargetedRefreshThatRepaintedNothingFallsBackToTheFullPass()
    {
        var method = ReadRuntimeMethod(
            "private void NotifyTargetedWorkspaceChangedOrRefresh(",
            "private void NotifyTargetedWorkspaceChanged(Action<DesktopSurfaceManager> refresh)");

        Assert.Contains("refreshed = refresh(_surfaceManager);", method, StringComparison.Ordinal);
        Assert.Contains("_surfaceManager.Refresh();", method, StringComparison.Ordinal);
        Assert.Contains("_workspaceRevision++;", method, StringComparison.Ordinal);
        Assert.Contains("ScheduleSave();", method, StringComparison.Ordinal);
    }

    [Fact]
    public void ImportingIntoABoxOnlyRefreshesThatBoxWhenEveryFileWasAssigned()
    {
        var method = ReadRuntimeMethod(
            "public async Task<FileImportBatchResult> ImportFilesAsync(",
            "public async Task<FileImportBatchResult> ImportFilesToBoxAsync(");

        var watcherRegistration = method.IndexOf(
            "RegisterTargetedDesktopRefresh(sourcePaths);",
            StringComparison.Ordinal);
        var fileImport = method.IndexOf("_fileOperations.ImportAsync(", StringComparison.Ordinal);
        Assert.True(watcherRegistration >= 0);
        Assert.True(fileImport > watcherRegistration);
        Assert.Contains(
            "RegisterTargetedDesktopRefresh(imported.ImportedPaths);",
            method,
            StringComparison.Ordinal);
        Assert.Contains("await RefreshItemsCoreAsync(refreshSurfaces: false);", method, StringComparison.Ordinal);
        Assert.Contains("if (assignedKeys.Count == imported.SucceededCount)", method, StringComparison.Ordinal);
        Assert.Contains("NotifyBoxItemsChanged(boxId);", method, StringComparison.Ordinal);
    }

    [Fact]
    public void AChangeCrabDeskPerformedItselfDoesNotTriggerASecondFullRefresh()
    {
        var handler = ReadRuntimeMethod(
            "private async void OnDesktopItemsChanged(",
            "private async Task RefreshDesktopItemsAfterOwnedChangeAsync()");

        Assert.Contains(
            "ShouldSuppressTargetedDesktopRefresh(ownedArgs.FullPath)",
            handler,
            StringComparison.Ordinal);
        Assert.Contains(
            "await RefreshDesktopItemsAfterOwnedChangeAsync();",
            handler,
            StringComparison.Ordinal);
        var ownedBranch = handler.IndexOf(
            "await RefreshDesktopItemsAfterOwnedChangeAsync();",
            StringComparison.Ordinal);
        var fullRefresh = handler.IndexOf("await RefreshItemsAsync();", StringComparison.Ordinal);
        Assert.True(ownedBranch >= 0);
        Assert.True(fullRefresh > ownedBranch);

        var reconcile = ReadRuntimeMethod(
            "private async Task RefreshDesktopItemsAfterOwnedChangeAsync()",
            "private Dictionary<string, string> GetDesktopItemIdentities()");
        Assert.Contains(
            "await RefreshItemsCoreAsync(refreshSurfaces: false, applyDesktopRules: false);",
            reconcile,
            StringComparison.Ordinal);
        Assert.Contains("RefreshBoxItems(changedBoxIds)", reconcile, StringComparison.Ordinal);
        Assert.Contains("RefreshDesktopItemsRemoved(removedDesktopKeys)", reconcile, StringComparison.Ordinal);
        Assert.Contains("RefreshDesktopItemsAdded(addedDesktopKeys)", reconcile, StringComparison.Ordinal);
        Assert.DoesNotContain("await RefreshItemsAsync();", reconcile, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOwnedRenameThatKeepsItsItemKeyStillFallsBackToTheFullPass()
    {
        var reconcile = ReadRuntimeMethod(
            "private async Task RefreshDesktopItemsAfterOwnedChangeAsync()",
            "private Dictionary<string, string> GetDesktopItemIdentities()");

        var identityChanged = reconcile.IndexOf(
            "!string.Equals(previousIdentity, entry.Value, StringComparison.Ordinal)",
            StringComparison.Ordinal);
        var fallback = reconcile.IndexOf("_surfaceManager?.Refresh();", StringComparison.Ordinal);
        var addedKeys = reconcile.IndexOf("var addedKeys =", StringComparison.Ordinal);
        Assert.True(identityChanged >= 0);
        Assert.True(fallback > identityChanged);
        Assert.True(addedKeys > fallback);
        Assert.Contains(
            "if (addedKeys.Length == 0 && removedKeys.Length == 0)",
            reconcile,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AMonitorWithNoDirtyRegionKeepsItsCurrentFrame()
    {
        var method = ReadMethod(
            "DesktopIconSurface.cs",
            "private void RenderQueuedBoxVisualFrame()",
            "private void RequestDragRender()");

        var emptyRegion = method.IndexOf(
            "if (releaseDirtyBounds.Count == 0)",
            StringComparison.Ordinal);
        var partialFlag = method.IndexOf(
            "var partialPresentSucceeded = true;",
            StringComparison.Ordinal);
        var fallback = method.IndexOf("PresentLayer();", StringComparison.Ordinal);
        Assert.True(emptyRegion >= 0);
        Assert.True(partialFlag > emptyRegion);
        Assert.True(fallback > partialFlag);
        Assert.DoesNotContain(
            "var partialPresentSucceeded = releaseDirtyBounds.Count > 0;",
            method,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MovingDesktopItemsIntoAFolderClearsTheirCellsByItemKey()
    {
        var method = ReadRuntimeMethod(
            "internal async Task<FileImportBatchResult> ImportDesktopItemsIntoFolderAsync(",
            "internal async Task ReconcileExternalDesktopMoveAsync(");

        Assert.Contains("RefreshDesktopItemsRemoved(movedItemKeys)", method, StringComparison.Ordinal);
        Assert.Contains("item.Key.ToString()", method, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "RefreshDesktopItemsRemoved(result.ImportedPaths)",
            method,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ImportDesktopItemsIntoFolderPreservesMoveParameterSemantics(bool isMove)
    {
        var method = ReadRuntimeMethod(
            "internal async Task<FileImportBatchResult> ImportDesktopItemsIntoFolderAsync(",
            "internal async Task ReconcileExternalDesktopMoveAsync(");

        Assert.DoesNotContain("!isMove", method, StringComparison.Ordinal);
        if (isMove)
        {
            Assert.Contains("move: isMove", method, StringComparison.Ordinal);
        }
    }

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
