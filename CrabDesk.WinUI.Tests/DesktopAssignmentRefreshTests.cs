using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class DesktopAssignmentRefreshTests
{
    [Fact]
    public void BoxItemReorderUsesTargetedRefresh()
    {
        var source = File.ReadAllText(Path.Combine(
            FindSolutionDirectory(),
            "CrabDesk.Runtime",
            "CrabDeskRuntime.cs"));
        var methodStart = source.IndexOf(
            "public bool ReorderBoxItems(",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            "public bool MoveBoxInStack(",
            Math.Max(0, methodStart),
            StringComparison.Ordinal);

        Assert.True(methodStart >= 0);
        Assert.True(methodEnd > methodStart);
        var method = source[methodStart..methodEnd];
        Assert.Contains("NotifyBoxItemsChanged(boxId);", method, StringComparison.Ordinal);
        Assert.DoesNotContain("NotifyWorkspaceChanged(true);", method, StringComparison.Ordinal);
    }

    [Fact]
    public void BoxCreationUsesTargetedRefresh()
    {
        var solutionDirectory = FindSolutionDirectory();
        var runtimeSource = File.ReadAllText(Path.Combine(
            solutionDirectory,
            "CrabDesk.Runtime",
            "CrabDeskRuntime.cs"));
        var addStart = runtimeSource.IndexOf(
            "public DesktopBox AddBox(",
            StringComparison.Ordinal);
        var createCoreStart = runtimeSource.IndexOf(
            "private DesktopBox CreateBoxCore(",
            Math.Max(0, addStart),
            StringComparison.Ordinal);

        Assert.True(addStart >= 0);
        Assert.True(createCoreStart > addStart);
        var addMethod = runtimeSource[addStart..createCoreStart];
        Assert.Contains("NotifyBoxAdded(box.Id);", addMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("NotifyWorkspaceChanged(true);", addMethod, StringComparison.Ordinal);

        var managerSource = File.ReadAllText(Path.Combine(
            solutionDirectory,
            "CrabDesk.Runtime",
            "DesktopSurfaceManager.cs"));
        var refreshStart = managerSource.IndexOf(
            "internal bool RefreshBoxAdded(",
            StringComparison.Ordinal);
        var refreshEnd = managerSource.IndexOf(
            "internal bool RefreshDesktopItemRelease(",
            Math.Max(0, refreshStart),
            StringComparison.Ordinal);

        Assert.True(refreshStart >= 0);
        Assert.True(refreshEnd > refreshStart);
        var refreshMethod = managerSource[refreshStart..refreshEnd];
        Assert.Contains("surface.RefreshBoxItems(boxId)", refreshMethod, StringComparison.Ordinal);
        Assert.Contains("surface.UpdateInteractionRegion()", refreshMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("Refresh();", refreshMethod, StringComparison.Ordinal);
    }

    [Fact]
    public void CollapsedBoxGeometryKeepsTheActiveItemViewAndItsScrollKey()
    {
        var source = File.ReadAllText(Path.Combine(
            FindSolutionDirectory(),
            "CrabDesk.Runtime",
            "DesktopBoxForm.Geometry.cs"));
        var methodStart = source.IndexOf(
            "private BoxGeometry CreateBoxGeometry(",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            "private void EnsureGeometry()",
            Math.Max(0, methodStart),
            StringComparison.Ordinal);

        Assert.True(methodStart >= 0);
        Assert.True(methodEnd > methodStart);
        var method = source[methodStart..methodEnd];
        var activeManualView = method.IndexOf(
            "GetActiveManualTabId(box.Id, availableManualTabs)",
            StringComparison.Ordinal);
        var activeMappedView = method.IndexOf(
            "GetActiveMappedFolderCategory(box.Id, availableCategoryTabs)",
            StringComparison.Ordinal);
        var hideTabs = method.IndexOf(
            "var manualTabs = isCollapsed ? [] : availableManualTabs;",
            StringComparison.Ordinal);
        var viewKeyStart = source.IndexOf(
            "private static ItemViewKey GetItemViewKey(",
            StringComparison.Ordinal);
        var viewKeyEnd = source.IndexOf(
            "private static string GetMappedFolderCategoryLabel",
            Math.Max(0, viewKeyStart),
            StringComparison.Ordinal);

        Assert.True(activeManualView >= 0);
        Assert.True(activeMappedView >= 0);
        Assert.True(hideTabs > activeManualView);
        Assert.True(hideTabs > activeMappedView);
        Assert.True(viewKeyStart >= 0);
        Assert.True(viewKeyEnd > viewKeyStart);
        var viewKey = source[viewKeyStart..viewKeyEnd];
        Assert.Contains("geometry.Box.ManualTabs.Count > 0", viewKey, StringComparison.Ordinal);
        Assert.DoesNotContain("geometry.ManualTabs.Count > 0", viewKey, StringComparison.Ordinal);
    }

    [Fact]
    public void MovingItemsBetweenManualTabsUsesTargetedRefresh()
    {
        var source = File.ReadAllText(Path.Combine(
            FindSolutionDirectory(),
            "CrabDesk.Runtime",
            "CrabDeskRuntime.cs"));
        var methodStart = source.IndexOf(
            "public int MoveItemsToManualTab(",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            "public void DeleteBox(",
            Math.Max(0, methodStart),
            StringComparison.Ordinal);

        Assert.True(methodStart >= 0);
        Assert.True(methodEnd > methodStart);
        var method = source[methodStart..methodEnd];
        Assert.Contains("NotifyBoxItemsChanged(boxId);", method, StringComparison.Ordinal);
        Assert.DoesNotContain("NotifyWorkspaceChanged(true);", method, StringComparison.Ordinal);
    }

    [Fact]
    public void CrossBoxTransferRefreshesSourceAndTargetWithoutGlobalRefresh()
    {
        var source = File.ReadAllText(Path.Combine(
            FindSolutionDirectory(),
            "CrabDesk.Runtime",
            "CrabDeskRuntime.cs"));
        var methodStart = source.IndexOf(
            "public async Task<FileImportBatchResult> TransferBoxItemsAsync(",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            "public bool CanPasteIntoBox(",
            Math.Max(0, methodStart),
            StringComparison.Ordinal);

        Assert.True(methodStart >= 0);
        Assert.True(methodEnd > methodStart);
        var method = source[methodStart..methodEnd];
        Assert.Contains("NotifyBoxesItemsChanged([sourceBoxId, targetBoxId]);", method, StringComparison.Ordinal);
        Assert.DoesNotContain("AssignItems(", method, StringComparison.Ordinal);
        Assert.DoesNotContain("NotifyWorkspaceChanged(true);", method, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopDropUsesTheTargetedAssignmentRefreshPath()
    {
        var solutionDirectory = FindSolutionDirectory();
        var runtimeSource = File.ReadAllText(Path.Combine(
            solutionDirectory,
            "CrabDesk.Runtime",
            "CrabDeskRuntime.cs"));
        var methodStart = runtimeSource.IndexOf(
            "internal int AssignDesktopItemsAtDrop(",
            StringComparison.Ordinal);
        var methodEnd = runtimeSource.IndexOf(
            "public void UnassignItem(",
            Math.Max(0, methodStart),
            StringComparison.Ordinal);

        Assert.True(methodStart >= 0);
        Assert.True(methodEnd > methodStart);
        var method = runtimeSource[methodStart..methodEnd];
        Assert.Contains("NotifyDesktopItemAssignmentChanged(boxId);", method, StringComparison.Ordinal);
        Assert.DoesNotContain("NotifyWorkspaceChanged(true);", method, StringComparison.Ordinal);

        var dropSource = File.ReadAllText(Path.Combine(
            solutionDirectory,
            "CrabDesk.Runtime",
            "DesktopBoxForm.DragDrop.cs"));
        var dropMethodStart = dropSource.IndexOf(
            "private int AssignDesktopItemsAtDrop(",
            StringComparison.Ordinal);
        var dropMethodEnd = dropSource.IndexOf(
            "private void SetDropPreview(",
            Math.Max(0, dropMethodStart),
            StringComparison.Ordinal);

        Assert.True(dropMethodStart >= 0);
        Assert.True(dropMethodEnd > dropMethodStart);
        var dropMethod = dropSource[dropMethodStart..dropMethodEnd];
        Assert.Contains("_runtime.AssignDesktopItemsAtDrop(", dropMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("_runtime.AssignItems(", dropMethod, StringComparison.Ordinal);
    }

    [Fact]
    public void BoxAppearanceAndViewChangesUseTargetedRefresh()
    {
        var source = File.ReadAllText(Path.Combine(
            FindSolutionDirectory(),
            "CrabDesk.Runtime",
            "CrabDeskRuntime.cs"));

        foreach (var methodName in new[]
                 {
                     "SetBoxAccent",
                     "SetBoxViewMode",
                     "SetBoxSortMode",
                     "CreateManualTab",
                     "RenameManualTab",
                     "DeleteManualTab"
                 })
        {
            var methodStart = source.IndexOf($"public ", StringComparison.Ordinal);
            methodStart = source.IndexOf(methodName + "(", Math.Max(0, methodStart), StringComparison.Ordinal);
            var methodEnd = source.IndexOf("\n    public ", Math.Max(0, methodStart + 1), StringComparison.Ordinal);

            Assert.True(methodStart >= 0, $"Method {methodName} was not found.");
            Assert.True(methodEnd > methodStart, $"Method {methodName} boundary was not found.");
            var method = source[methodStart..methodEnd];
            Assert.Contains("NotifyBoxWorkspaceChanged", method, StringComparison.Ordinal);
            Assert.DoesNotContain("NotifyWorkspaceChanged(true);", method, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void BoxDropCalculatesDesktopInsertionOnlyAfterDrop()
    {
        var solutionDirectory = FindSolutionDirectory();
        var source = File.ReadAllText(Path.Combine(
            solutionDirectory,
            "CrabDesk.Runtime",
            "DesktopIconSurface.cs"));
        var updateStart = source.IndexOf(
            "private bool UpdateBoxDropPlacement(",
            StringComparison.Ordinal);
        var buildStart = source.IndexOf(
            "private IReadOnlyDictionary<string, DesktopIconLayoutSnapshot>? BuildBoxDropDesktopLayout(",
            Math.Max(0, updateStart),
            StringComparison.Ordinal);
        var clearStart = source.IndexOf(
            "private void ClearBoxDropState()",
            Math.Max(0, buildStart),
            StringComparison.Ordinal);

        Assert.True(updateStart >= 0);
        Assert.True(buildStart > updateStart);
        Assert.True(clearStart > buildStart);
        var pointerUpdate = source[updateStart..buildStart];
        var dropLayout = source[buildStart..clearStart];
        Assert.DoesNotContain("CalculateInsertion(", pointerUpdate, StringComparison.Ordinal);
        Assert.Contains("CalculateInsertion(", dropLayout, StringComparison.Ordinal);
    }

    [Fact]
    public void ExternalDropInsertsAtTheRequestedCellInsteadOfSearchingForAnEmptyCell()
    {
        var solutionDirectory = FindSolutionDirectory();
        var source = File.ReadAllText(Path.Combine(
            solutionDirectory,
            "CrabDesk.Runtime",
            "DesktopIconSurface.cs"));
        var methodStart = source.IndexOf(
            "private bool PlaceDroppedItemsAtPoint(",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            "private async Task DeleteExternalDropToRecycleBinAsync(",
            Math.Max(0, methodStart),
            StringComparison.Ordinal);

        Assert.True(methodStart >= 0);
        Assert.True(methodEnd > methodStart);
        var method = source[methodStart..methodEnd];
        Assert.Contains("DesktopIconDragLayoutEngine.CalculateInsertion(", method, StringComparison.Ordinal);
        Assert.DoesNotContain("FindFirstFreeCellAtOrAfter(", method, StringComparison.Ordinal);
    }

    [Fact]
    public void ExternalImportRefreshesTheSnapshotBeforeItsSingleSurfaceCommit()
    {
        var solutionDirectory = FindSolutionDirectory();
        var source = File.ReadAllText(Path.Combine(
            solutionDirectory,
            "CrabDesk.Runtime",
            "DesktopIconSurface.cs"));
        var methodStart = source.IndexOf(
            "private async Task ImportExternalDropToDesktopAsync(",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            "private bool PlaceDroppedItemsAtPoint(",
            Math.Max(0, methodStart),
            StringComparison.Ordinal);

        Assert.True(methodStart >= 0);
        Assert.True(methodEnd > methodStart);
        var method = source[methodStart..methodEnd];
        Assert.Contains("RefreshItemsSnapshotAsync(", method, StringComparison.Ordinal);
        Assert.DoesNotContain("RefreshItemsAsync(", method, StringComparison.Ordinal);
        var watcherRegistration = method.IndexOf(
            "_runtime.RegisterTargetedDesktopRefresh(",
            StringComparison.Ordinal);
        var fileImport = method.IndexOf(
            "_runtime.FileOperations.ImportAsync(",
            StringComparison.Ordinal);
        Assert.True(watcherRegistration >= 0);
        Assert.True(fileImport > watcherRegistration);

        var placementStart = source.IndexOf(
            "private bool PlaceDroppedItemsAtPoint(",
            Math.Max(0, methodEnd),
            StringComparison.Ordinal);
        var placementEnd = source.IndexOf(
            "internal static IReadOnlyList<string> ResolveImportedItemKeysInDropOrder(",
            Math.Max(0, placementStart),
            StringComparison.Ordinal);
        Assert.True(placementStart >= 0);
        Assert.True(placementEnd > placementStart);
        var placementMethod = source[placementStart..placementEnd];
        Assert.Contains(
            "SetDesktopIconLayout(next, refreshWorkspace: false)",
            placementMethod,
            StringComparison.Ordinal);
        Assert.Contains("RefreshDesktopItemsChanged(", placementMethod, StringComparison.Ordinal);
        Assert.Contains("PlaceExistingDesktopPathsAtPoint(", method, StringComparison.Ordinal);
        Assert.Contains("if (externalPaths.Length == 0)", method, StringComparison.Ordinal);
    }

    [Fact]
    public void PartialHeightAnimationDrawsAParentOwnedBoxBeforeTheDragGhost()
    {
        var solutionDirectory = FindSolutionDirectory();
        var source = File.ReadAllText(Path.Combine(
            solutionDirectory,
            "CrabDesk.Runtime",
            "DesktopIconSurface.cs"));
        var methodStart = source.IndexOf(
            "private bool PresentPartialBoxAnimationFrame(",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            "private bool PresentSettledPartialFrame(",
            Math.Max(0, methodStart),
            StringComparison.Ordinal);

        Assert.True(methodStart >= 0);
        Assert.True(methodEnd > methodStart);
        var method = source[methodStart..methodEnd];
        var boxRender = method.IndexOf("_dragBoxRenderer?.Invoke(", StringComparison.Ordinal);
        var ghostRender = method.IndexOf("DrawDynamicDragVisuals(", StringComparison.Ordinal);
        Assert.True(boxRender >= 0);
        Assert.True(ghostRender > boxRender);
    }

    [Fact]
    public void SettledPartialBoxFrameRemovesTheAnimationOverlayAfterPresenting()
    {
        var solutionDirectory = FindSolutionDirectory();
        var source = File.ReadAllText(Path.Combine(
            solutionDirectory,
            "CrabDesk.Runtime",
            "DesktopIconSurface.cs"));
        var methodStart = source.IndexOf(
            "private bool PresentSettledPartialFrame(",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            "private bool PresentPartialBoxAnimationFallbackFrame(",
            Math.Max(0, methodStart),
            StringComparison.Ordinal);

        Assert.True(methodStart >= 0);
        Assert.True(methodEnd > methodStart);
        var method = source[methodStart..methodEnd];
        var partialPresent = method.IndexOf(
            "LayeredWindowPresenter.TryPresentPartial(",
            StringComparison.Ordinal);
        var hideOverlay = method.IndexOf(
            "_dragOverlay.HideOverlay();",
            StringComparison.Ordinal);
        Assert.True(partialPresent >= 0);
        Assert.True(hideOverlay > partialPresent);
    }

    [Fact]
    public void HeightAnimationOverlayAndPartialPresentUseIndependentBoundsChannels()
    {
        var solutionDirectory = FindSolutionDirectory();
        var source = File.ReadAllText(Path.Combine(
            solutionDirectory,
            "CrabDesk.Runtime",
            "DesktopIconSurface.cs"));
        var partialStart = source.IndexOf(
            "private bool PresentPartialBoxAnimationFrame(",
            StringComparison.Ordinal);
        var partialEnd = source.IndexOf(
            "private bool PresentSettledPartialFrame(",
            Math.Max(0, partialStart),
            StringComparison.Ordinal);
        var overlayStart = source.IndexOf(
            "private RectangleF? GetDragOverlayBounds(",
            StringComparison.Ordinal);
        var overlayEnd = source.IndexOf(
            "private static RectangleF? UnionVisualBounds(",
            Math.Max(0, overlayStart),
            StringComparison.Ordinal);

        Assert.True(partialStart >= 0);
        Assert.True(partialEnd > partialStart);
        Assert.True(overlayStart >= 0);
        Assert.True(overlayEnd > overlayStart);
        var partialMethod = source[partialStart..partialEnd];
        var overlayMethod = source[overlayStart..overlayEnd];
        Assert.Contains("_boxDynamicDirtyBounds?.Invoke()", partialMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("_boxDynamicBounds?.Invoke()", partialMethod, StringComparison.Ordinal);
        Assert.Contains("_boxDynamicBounds?.Invoke()", overlayMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("_boxDynamicDirtyBounds?.Invoke()", overlayMethod, StringComparison.Ordinal);
    }

    [Fact]
    public void ParentOwnedBoxFramesUseBoxLocalPartialPresentation()
    {
        var solutionDirectory = FindSolutionDirectory();
        var source = File.ReadAllText(Path.Combine(
            solutionDirectory,
            "CrabDesk.Runtime",
            "DesktopIconSurface.cs"));
        var methodStart = source.IndexOf(
            "private bool PresentBoxVisualsInParentFrame(",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            "private bool PresentPartialBoxAnimationFrame(",
            Math.Max(0, methodStart),
            StringComparison.Ordinal);

        Assert.True(methodStart >= 0);
        Assert.True(methodEnd > methodStart);
        var method = source[methodStart..methodEnd];
        Assert.Contains("CalculateDynamicBoxFrameDirtyBounds(", method, StringComparison.Ordinal);
        Assert.Contains("LayeredWindowPresenter.TryPresentPartial(", method, StringComparison.Ordinal);
        Assert.DoesNotContain("LayeredWindowPresenter.TryPresent(", method, StringComparison.Ordinal);
    }

    [Fact]
    public void DynamicBoxBaseRebuildIsRestrictedToItsVisualBounds()
    {
        var solutionDirectory = FindSolutionDirectory();
        var source = File.ReadAllText(Path.Combine(
            solutionDirectory,
            "CrabDesk.Runtime",
            "DesktopIconSurface.cs"));
        var methodStart = source.IndexOf(
            "private bool TryPrepareDynamicBoxBase(",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            "private bool PresentBoxVisualsInParentFrame(",
            Math.Max(0, methodStart),
            StringComparison.Ordinal);

        Assert.True(methodStart >= 0);
        Assert.True(methodEnd > methodStart);
        var method = source[methodStart..methodEnd];
        Assert.Contains("CalculatePartialBoxAnimationDirtyPixels(", method, StringComparison.Ordinal);
        Assert.Contains("DrawDesktopItems(", method, StringComparison.Ordinal);
        Assert.DoesNotContain("DrawSettledLayer(", method, StringComparison.Ordinal);
    }

    [Fact]
    public void VirtualBoxDragPayloadIsParsedOnlyOncePerOleSession()
    {
        var solutionDirectory = FindSolutionDirectory();
        var source = File.ReadAllText(Path.Combine(
            solutionDirectory,
            "CrabDesk.Runtime",
            "DesktopIconSurface.cs"));
        var methodStart = source.IndexOf(
            "private bool TryGetVirtualBoxDrag(",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            "private bool UpdateBoxDropPlacement(",
            Math.Max(0, methodStart),
            StringComparison.Ordinal);

        Assert.True(methodStart >= 0);
        Assert.True(methodEnd > methodStart);
        var method = source[methodStart..methodEnd];
        var cachedRead = method.IndexOf("_virtualBoxDragItemKeys is { } cachedKeys", StringComparison.Ordinal);
        var dataRead = method.IndexOf("GetDataPresent(DesktopBoxForm.ItemKeysFormat)", StringComparison.Ordinal);
        Assert.True(cachedRead >= 0);
        Assert.True(dataRead > cachedRead);
        Assert.Contains("_virtualBoxDragItemKeys = itemKeys;", method, StringComparison.Ordinal);
        Assert.Contains("_virtualBoxDragSession = session;", method, StringComparison.Ordinal);
    }

    [Fact]
    public void DropPreviewTransitionsUseTheBoxLocalPartialRenderChannel()
    {
        var solutionDirectory = FindSolutionDirectory();
        var source = File.ReadAllText(Path.Combine(
            solutionDirectory,
            "CrabDesk.Runtime",
            "DesktopBoxForm.DragDrop.cs"));
        var setStart = source.IndexOf(
            "private void SetDropPreview(",
            StringComparison.Ordinal);
        var clearStart = source.IndexOf(
            "private void ClearDropPreview()",
            Math.Max(0, setStart),
            StringComparison.Ordinal);
        var invalidateStart = source.IndexOf(
            "private void InvalidateDropPreview(",
            Math.Max(0, clearStart),
            StringComparison.Ordinal);

        Assert.True(setStart >= 0);
        Assert.True(clearStart > setStart);
        Assert.True(invalidateStart > clearStart);
        var setMethod = source[setStart..clearStart];
        var clearMethod = source[clearStart..invalidateStart];
        Assert.Contains(
            "RequestDropPreviewVisualUpdate(previousPreview, preview);",
            setMethod,
            StringComparison.Ordinal);
        Assert.Contains(
            "RequestDropPreviewVisualUpdate(previousPreview, null);",
            clearMethod,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ActiveBoxDropFeedbackUsesAPartialLayeredWindowUpdate()
    {
        var solutionDirectory = FindSolutionDirectory();
        var source = File.ReadAllText(Path.Combine(
            solutionDirectory,
            "CrabDesk.Runtime",
            "DesktopIconSurface.cs"));
        var methodStart = source.IndexOf(
            "private bool PresentActiveBoxPartialFrame(",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            "private bool PresentActiveBoxPartialFallback(",
            Math.Max(0, methodStart),
            StringComparison.Ordinal);

        Assert.True(methodStart >= 0);
        Assert.True(methodEnd > methodStart);
        var method = source[methodStart..methodEnd];
        Assert.Contains("LayeredWindowPresenter.TryPresentPartial(", method, StringComparison.Ordinal);
        Assert.DoesNotContain("LayeredWindowPresenter.TryPresent(", method, StringComparison.Ordinal);
        var desktopItems = method.IndexOf("DrawDesktopItems(", StringComparison.Ordinal);
        var settledBoxes = method.IndexOf("_boxRenderer?.Invoke(", StringComparison.Ordinal);
        var dynamicBoxes = method.IndexOf("_dragBoxRenderer?.Invoke(", StringComparison.Ordinal);
        Assert.True(desktopItems >= 0);
        Assert.True(settledBoxes > desktopItems);
        Assert.True(dynamicBoxes > settledBoxes);
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
