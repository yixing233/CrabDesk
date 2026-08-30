using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using CrabDesk.Core;
using CrabDesk.Native;
using CrabDesk.Runtime;
using CrabDesk.WinUI.Controls;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class DesktopIconInteractionTests
{
    [Theory]
    [InlineData(LucideIconName.Archive)]
    [InlineData(LucideIconName.ArrowDown)]
    [InlineData(LucideIconName.ArrowRight)]
    [InlineData(LucideIconName.ArrowUp)]
    [InlineData(LucideIconName.Copy)]
    [InlineData(LucideIconName.Download)]
    [InlineData(LucideIconName.ExternalLink)]
    [InlineData(LucideIconName.Eye)]
    [InlineData(LucideIconName.FolderGit2)]
    [InlineData(LucideIconName.FolderOpen)]
    [InlineData(LucideIconName.House)]
    [InlineData(LucideIconName.Info)]
    [InlineData(LucideIconName.Keyboard)]
    [InlineData(LucideIconName.LayoutGrid)]
    [InlineData(LucideIconName.Pencil)]
    [InlineData(LucideIconName.Plus)]
    [InlineData(LucideIconName.RefreshCw)]
    [InlineData(LucideIconName.RotateCcw)]
    [InlineData(LucideIconName.Sparkles)]
    [InlineData(LucideIconName.Trash2)]
    [InlineData(LucideIconName.Upload)]
    [InlineData(LucideIconName.X)]
    public void WinUiLucideIconsUsedByXamlExposeGlyphs(LucideIconName icon) =>
        Assert.NotEmpty(LucideGlyphs.ToGlyph(icon));

    [Theory]
    [InlineData(nameof(LucideRuntimeIcon.Menu), "\uE115")]
    [InlineData(nameof(LucideRuntimeIcon.Search), "\uE151")]
    [InlineData(nameof(LucideRuntimeIcon.ChevronsUpDown), "\uE211")]
    [InlineData(nameof(LucideRuntimeIcon.Check), "\uE06C")]
    [InlineData(nameof(LucideRuntimeIcon.ChevronRight), "\uE06F")]
    [InlineData(nameof(LucideRuntimeIcon.TriangleAlert), "\uE193")]
    [InlineData(nameof(LucideRuntimeIcon.CircleAlert), "\uE077")]
    public void RuntimeLucideIconsExposeExpectedFontGlyphs(
        string iconName,
        string expected)
    {
        var icon = Enum.Parse<LucideRuntimeIcon>(iconName);
        Assert.Equal(expected, LucideRuntimeIcons.GetGlyph(icon));
    }

    [Fact]
    public void EveryRuntimeLucideIconExposesAGlyph()
    {
        foreach (var icon in Enum.GetValues<LucideRuntimeIcon>())
        {
            Assert.NotEmpty(LucideRuntimeIcons.GetGlyph(icon));
        }
    }

    [Theory]
    [InlineData(15f, 1f, 15f)]
    [InlineData(15f, 1.25f, 15.2f)]
    [InlineData(15f, 1.5f, 15.333333f)]
    public void RuntimeLucideFontSizeAlignsToPhysicalPixels(
        float requestedEmSize,
        float dpiScale,
        float expectedEmSize)
    {
        Assert.Equal(
            expectedEmSize,
            LucideRuntimeIcons.AlignEmSizeToPhysicalPixels(requestedEmSize, dpiScale),
            precision: 3);
    }

    [Theory]
    [InlineData("Quarterly Report.PDF", "report", true)]
    [InlineData("Quarterly Report.PDF", "REPORT.PDF", true)]
    [InlineData("Quarterly Report.PDF", "image", false)]
    [InlineData("Quarterly Report.PDF", "  ", true)]
    public void BoxSearchMatchesDisplayNamesCaseInsensitively(
        string displayName,
        string query,
        bool expected)
    {
        Assert.Equal(
            expected,
            BoxItemSearchFilter.MatchesDisplayName(displayName, query));
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    public void BoxSearchClosesOnlyForClicksOutsideItsActiveBox(
        bool searchVisible,
        bool pointerInsideActiveBox,
        bool expected)
    {
        Assert.Equal(
            expected,
            DesktopBoxForm.ShouldCloseBoxSearchForPointer(
                searchVisible,
                pointerInsideActiveBox));
    }

    [Theory]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, true, false)]
    [InlineData(false, true, false, false)]
    public void HoverExpansionOnlyTargetsTheTopmostVisibleCollapsedHeader(
        bool isTopmostVisualBox,
        bool isCollapsed,
        bool pointerOverHeaderAction,
        bool expected)
    {
        var candidateBoxId = Guid.NewGuid();
        var topmostVisualBoxId = isTopmostVisualBox ? candidateBoxId : Guid.NewGuid();

        Assert.Equal(
            expected,
            DesktopBoxForm.IsCollapsedHeaderHoverTarget(
                candidateBoxId,
                topmostVisualBoxId,
                expandsOnHover: true,
                isCollapsed,
                pointerInHeader: true,
                pointerOverHeaderAction));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public void HeaderActionsAppearOnlyForHoverOrActiveSearch(
        bool isHovered,
        bool isSearching,
        bool expected)
    {
        var boxId = Guid.NewGuid();

        Assert.Equal(
            expected,
            DesktopBoxForm.ShouldShowHeaderActions(
                boxId,
                isHovered ? boxId : null,
                isSearching ? boxId : null));
    }

    [Theory]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    [InlineData(false, true, false, false)]
    [InlineData(true, false, false, false)]
    public void HeaderActionOverlayIsRestoredOnlyWhenMissing(
        bool hoverTargetUnchanged,
        bool hasActiveHeaderActions,
        bool overlayVisible,
        bool expected)
    {
        Assert.Equal(
            expected,
            DesktopBoxForm.ShouldRestoreHeaderActionOverlay(
                hoverTargetUnchanged,
                hasActiveHeaderActions,
                overlayVisible));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void MenuOrInlineRenameSuspendsHoverStateUntilInteractionCompletes(
        bool menuOpen,
        bool inlineRenameActive,
        bool expected)
    {
        Assert.Equal(
            expected,
            DesktopBoxForm.ShouldSuspendHoverState(
                menuOpen ? Guid.NewGuid() : null,
                inlineRenameActive));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void PendingBoxHoverReconcileFlushesAfterPointerInteractionCompletes(
        bool reconcilePending,
        bool pointerInteractionActive,
        bool expected)
    {
        Assert.Equal(
            expected,
            DesktopSurfaceManager.ShouldFlushPendingBoxHoverReconcile(
                reconcilePending,
                pointerInteractionActive));
    }

    [Fact]
    public void DesktopDropTargetVisualIgnoresPointerMovementInsideTheSameBox()
    {
        var boxId = Guid.NewGuid();

        Assert.False(DesktopBoxForm.HasDesktopDropTargetVisualChanged(
            boxId,
            previousAcceptsDrop: true,
            previousManualTabIndex: null,
            boxId,
            currentAcceptsDrop: true,
            currentManualTabIndex: null));
    }

    [Theory]
    [InlineData(false, null, true, null, true)]
    [InlineData(true, null, true, 0, true)]
    [InlineData(true, 0, true, 1, true)]
    [InlineData(true, 0, false, 0, true)]
    public void DesktopDropTargetVisualUpdatesOnlyForVisibleTargetState(
        bool hasPreviousTarget,
        int? previousManualTabIndex,
        bool currentAcceptsDrop,
        int? currentManualTabIndex,
        bool expected)
    {
        var currentBoxId = Guid.NewGuid();

        Assert.Equal(
            expected,
            DesktopBoxForm.HasDesktopDropTargetVisualChanged(
                hasPreviousTarget ? currentBoxId : null,
                previousAcceptsDrop: true,
                previousManualTabIndex,
                currentBoxId,
                currentAcceptsDrop,
                currentManualTabIndex));
    }

    [Theory]
    [InlineData(false, false, false, true, false)]
    [InlineData(false, true, false, true, true)]
    [InlineData(false, false, true, false, true)]
    [InlineData(true, false, false, true, true)]
    public void OleDropPreviewRendersOnlyWhenItsVisibleFeedbackChanges(
        bool floatingCard,
        bool targetVisualChanged,
        bool folderTargetChanged,
        bool pointerChanged,
        bool expected)
    {
        Assert.Equal(
            expected,
            DesktopBoxForm.ShouldRenderOleDropPreview(
                floatingCard,
                targetVisualChanged,
                folderTargetChanged,
                pointerChanged));
    }

    [Theory]
    [InlineData(false, true, false, false, true, true, 7, 7, false)]
    [InlineData(true, false, false, false, true, true, 7, 7, false)]
    [InlineData(true, true, true, false, true, true, 7, 7, false)]
    [InlineData(true, true, false, true, true, true, 7, 7, false)]
    [InlineData(true, true, false, false, false, true, 7, 7, false)]
    [InlineData(true, true, false, false, true, false, 7, 7, false)]
    [InlineData(true, true, false, false, true, true, 6, 7, false)]
    [InlineData(true, true, false, false, true, true, 7, 7, true)]
    public void UnchangedParentBoxFrameIsReusedDuringPointerGhostDrag(
        bool visualsInParent,
        bool pointerGhostOverlayActive,
        bool selecting,
        bool staticFrameChanged,
        bool parentFrameAvailable,
        bool lastPresentSucceeded,
        int presentedVersion,
        int currentVersion,
        bool expected)
    {
        Assert.Equal(
            expected,
            DesktopIconSurface.ShouldReuseBoxParentFrame(
                visualsInParent,
                pointerGhostOverlayActive,
                selecting,
                staticFrameChanged,
                parentFrameAvailable,
                lastPresentSucceeded,
                presentedVersion,
                currentVersion));
    }

    [Fact]
    public void ParentBoxFrameIsNotReusedWhileItsScrollAnimationIsAdvancing()
    {
        Assert.False(DesktopIconSurface.ShouldReuseBoxParentFrame(
            visualsInParent: true,
            pointerGhostOverlayActive: true,
            selecting: false,
            staticFrameChanged: false,
            parentFrameAvailable: true,
            lastPresentSucceeded: true,
            presentedVersion: 7,
            currentVersion: 7,
            dynamicAnimationActive: true));
    }

    [Theory]
    [InlineData(true, true, true, true, false, true)]
    [InlineData(false, true, true, true, false, false)]
    [InlineData(true, false, true, true, false, false)]
    [InlineData(true, true, false, true, false, false)]
    [InlineData(true, true, true, false, false, false)]
    [InlineData(true, true, true, true, true, false)]
    public void BoxLocalDropFeedbackKeepsTheExistingDragBase(
        bool dragBaseReady,
        bool partialUpdatePending,
        bool visualsInParent,
        bool pointerGhostOverlayActive,
        bool selecting,
        bool expected)
    {
        Assert.Equal(
            expected,
            DesktopIconSurface.ShouldKeepDragBaseForPartialBoxUpdate(
                dragBaseReady,
                partialUpdatePending,
                visualsInParent,
                pointerGhostOverlayActive,
                selecting));
    }

    [Theory]
    [InlineData(false, 10, 20, 30, 40, false)]
    [InlineData(true, 10, 20, 10, 20, false)]
    [InlineData(true, 10, 20, 30, 40, true)]
    public void ActiveDragGhostSynchronizesToAChangedPhysicalCursor(
        bool dragActive,
        float currentX,
        float currentY,
        float cursorX,
        float cursorY,
        bool expected)
    {
        Assert.Equal(
            expected,
            DesktopIconSurface.ShouldSynchronizeDragPointer(
                dragActive,
            new PointF(currentX, currentY),
            new PointF(cursorX, cursorY)));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public void OleInitializesVirtualBoxGhostWithoutDrivingEveryPointerFrame(
        bool keysChanged,
        bool pointerInitialized,
        bool expected)
    {
        Assert.Equal(
            expected,
            DesktopIconSurface.ShouldPublishVirtualBoxGhostFromOle(
                keysChanged,
                pointerInitialized));
    }

    [Theory]
    [InlineData(120, 30, true)]
    [InlineData(180, 150, true)]
    [InlineData(99, 100, false)]
    [InlineData(301, 100, false)]
    public void HeaderActionsActivateAcrossTheEntireVisualBox(
        float x,
        float y,
        bool expected)
    {
        var boxBounds = new RectangleF(100, 20, 200, 180);

        Assert.Equal(
            expected,
            DesktopBoxForm.IsPointerInsideVisualBox(boxBounds, new PointF(x, y)));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    public void HeaderActionsStayOutOfCompositedBaseLayerWhenOverlayIsAvailable(
        bool compositedByIconSurface,
        bool overlayUnavailable,
        bool expected)
    {
        Assert.Equal(
            expected,
            DesktopBoxForm.ShouldDrawHeaderActionsInBaseLayer(
                compositedByIconSurface,
                overlayUnavailable));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    public void HeaderActionOverlayStaysVisibleDuringBoxLocalAnimation(
        bool hasDynamicVisual,
        bool partialAnimationOnly,
        bool expected)
    {
        Assert.Equal(
            expected,
            DesktopBoxForm.ShouldPresentHeaderActionOverlay(
                hasDynamicVisual,
                partialAnimationOnly));
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(false, false, false, true)]
    public void DynamicBoxTransformCarriesItsHeaderActions(
        bool compositedByIconSurface,
        bool overlayUnavailable,
        bool dynamicTransform,
        bool expected)
    {
        Assert.Equal(
            expected,
            DesktopBoxForm.ShouldDrawHeaderActionsOnCurrentLayer(
                compositedByIconSurface,
                overlayUnavailable,
                dynamicTransform));
    }

    [Fact]
    public void BoxSearchEditorUsesWpfTextBoxToAvoidDisposedWinFormsFontHandles()
    {
        var searchInputField = typeof(DesktopBoxForm).GetField(
            "_searchInput",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(searchInputField);
        Assert.Equal(typeof(System.Windows.Controls.TextBox), searchInputField.FieldType);
    }

    [Fact]
    public void CenteredBoxTitleUsesSymmetricHeaderBounds()
    {
        var header = new RectangleF(100, 20, 320, 38);

        var title = DesktopBoxForm.CalculateTitleTextBounds(header, centered: true);

        Assert.Equal(header.Left + header.Width / 2, title.Left + title.Width / 2);
        Assert.Equal(title.Left - header.Left, header.Right - title.Right);
    }

    [Fact]
    public void HeaderActionsAreDistributedAcrossBothSidesOfTitle()
    {
        var header = new RectangleF(100, 20, 320, 38);

        var actions = DesktopBoxForm.CalculateHeaderActionBounds(header);
        var center = header.Left + header.Width / 2;

        Assert.True(actions.Search.Right < center);
        Assert.True(actions.AutoExpand.Left > center);
        Assert.True(actions.Menu.Left > center);
        Assert.Equal(actions.Search.Left - header.Left, header.Right - actions.Menu.Right);
        Assert.Equal(
            actions.AutoExpand.Left + actions.AutoExpand.Width / 2,
            actions.Menu.Left + actions.Menu.Width / 2 - 30);
    }

    [Fact]
    public void HoverFocusInvalidatesOnlyPreviousAndCurrentBoxes()
    {
        var previous = new RectangleF(10, 20, 100, 80);
        var current = new RectangleF(80, 40, 120, 100);

        var dirtyBounds = DesktopBoxForm.CalculateFocusDirtyBounds(previous, current);

        Assert.Equal(RectangleF.FromLTRB(10, 20, 200, 140), dirtyBounds);
    }

    [Theory]
    [InlineData(true, true, true, true, false, true)]
    [InlineData(false, true, true, true, false, false)]
    [InlineData(true, false, true, true, false, false)]
    [InlineData(true, true, false, true, false, false)]
    [InlineData(true, true, true, false, false, false)]
    [InlineData(true, true, true, true, true, false)]
    public void HeightAnimationCachePrewarmsOnlyForEligibleCollapsedBoxes(
        bool compositedByIconSurface,
        bool animationEnabled,
        bool expandOnHover,
        bool effectivelyCollapsed,
        bool cacheExists,
        bool expected)
    {
        Assert.Equal(
            expected,
            DesktopBoxForm.ShouldPrewarmHeightAnimationCache(
                compositedByIconSurface,
                animationEnabled,
                expandOnHover,
                effectivelyCollapsed,
                cacheExists));
    }

    [Theory]
    [InlineData(0x00000000, 0x08000080)]
    [InlineData(0x00040000, 0x08000080)]
    [InlineData(0x00100008, 0x08100088)]
    public void ContextMenuUsesNonActivatingToolWindowStyle(
        int extendedStyle,
        int expected)
    {
        Assert.Equal(
            expected,
            FluentContextMenuStrip.NormalizeExtendedWindowStyle(extendedStyle));
    }

    [Theory]
    [InlineData(false, true, true, true, false)]
    [InlineData(true, true, true, true, true)]
    [InlineData(true, false, true, true, false)]
    [InlineData(true, true, false, true, false)]
    [InlineData(true, true, true, false, false)]
    public void ContextMenuOpacityAnimationRequiresOwnerAndSystemOptIn(
        bool allowedByOwner,
        bool appAnimationsEnabled,
        bool systemMenuAnimationEnabled,
        bool systemMenuFadeEnabled,
        bool expected)
    {
        Assert.Equal(
            expected,
            FluentContextMenuStrip.ShouldAnimateOpacity(
                allowedByOwner,
                appAnimationsEnabled,
                systemMenuAnimationEnabled,
                systemMenuFadeEnabled));
    }

    [Theory]
    [InlineData(false, 96, 1.5f, 1.5f)]
    [InlineData(true, 144, null, 1.5f)]
    [InlineData(false, 144, null, 1f)]
    public void ContextMenuMetricsPreferKnownMonitorDpiBeforeHandleCreation(
        bool handleCreated,
        int deviceDpi,
        float? preferredDpiScale,
        float expected)
    {
        Assert.Equal(
            expected,
            FluentContextMenuStrip.ResolveMetricsDpiScale(
                handleCreated,
                deviceDpi,
                preferredDpiScale));
    }

    [Fact]
    public void FluentMenuItemsCreateFluentSubmenus()
    {
        using var item = new FluentToolStripMenuItem("父级");
        item.DropDownItems.Add("子项");

        var submenu = Assert.IsType<FluentToolStripDropDownMenu>(item.DropDown);
        var extendedStyle = submenu.ExtendedWindowStyleForTesting;

        Assert.Equal(0x08000080, extendedStyle & 0x08040080);
    }

    [Fact]
    public void FluentMenusReserveBalancedOuterVerticalSpace()
    {
        using var rootMenu = new FluentContextMenuStrip();
        using var subMenu = new FluentToolStripDropDownMenu();

        AssertBalancedOuterVerticalSpace(rootMenu);
        AssertBalancedOuterVerticalSpace(subMenu);
    }

    [Fact]
    public void CompositedBoxTransformCommitsThroughPartialFrame()
    {
        Assert.True(DesktopBoxForm.ShouldUsePartialTransformCommit(
            isCompositedByIconSurface: true,
            hasPartialRenderer: true));
        Assert.False(DesktopBoxForm.ShouldRebuildWorkspaceAfterBoxTransform());
        Assert.False(DesktopBoxForm.ShouldPresentAfterRegionUpdate(
            isCompositedByIconSurface: true,
            hitMaskPresented: true));
    }

    [Theory]
    [InlineData(30, 1, 16)]
    [InlineData(52.5, 1.75, 28)]
    public void MenuIconsScaleWithTheMenuDpi(
        float itemHeight,
        float dpiScale,
        float expected)
    {
        Assert.Equal(
            expected,
            FluentMenuRenderer.CalculateIconSize(itemHeight, dpiScale),
            precision: 3);
    }

    [Fact]
    public void MenuItemColumnsDoNotOverlapAtHighDpi()
    {
        var layout = FluentMenuRenderer.CalculateItemLayout(
            new Size(260, 53),
            dpiScale: 1.75f,
            hasArrow: true);

        Assert.True(layout.IconBounds.Right < layout.TextBounds.Left);
        Assert.True(layout.TextBounds.Right < layout.ArrowBounds.Left);
    }

    [Fact]
    public void ExternalFileDragOffersMoveForRecycleBinAndCopyForFileTargets()
    {
        var effects = DesktopIconSurface.ExternalFileDropEffects;

        Assert.True(effects.HasFlag(DragDropEffects.Copy));
        Assert.True(effects.HasFlag(DragDropEffects.Move));
    }

    [Fact]
    public void DesktopFolderDropAcceptsFilesystemFilesFromTheDesktop()
    {
        var draggedItems = new[]
        {
            CreateDesktopDropItem("file-a", @"C:\Users\Test\Desktop\a.txt", DesktopItemKind.File),
            CreateDesktopDropItem("file-b", @"C:\Users\Test\Desktop\b.txt", DesktopItemKind.File)
        };
        var target = CreateDesktopDropItem(
            "folder-target",
            @"C:\Users\Test\Desktop\Archive",
            DesktopItemKind.Folder);

        Assert.True(DesktopFolderDropPolicy.CanAccept(draggedItems, target));
    }

    [Theory]
    [InlineData(DesktopItemKind.File, @"C:\Users\Test\Desktop\Archive")]
    [InlineData(DesktopItemKind.Folder, null)]
    public void DesktopFolderDropRejectsTargetsThatAreNotFilesystemFolders(
        DesktopItemKind targetKind,
        string? targetPath)
    {
        var draggedItems = new[]
        {
            CreateDesktopDropItem("file-a", @"C:\Users\Test\Desktop\a.txt", DesktopItemKind.File)
        };
        var target = CreateDesktopDropItem("target", targetPath, targetKind);

        Assert.False(DesktopFolderDropPolicy.CanAccept(draggedItems, target));
    }

    [Fact]
    public void DesktopFolderDropRejectsAFolderIncludedInTheDraggedSelection()
    {
        var target = CreateDesktopDropItem(
            "folder-target",
            @"C:\Users\Test\Desktop\Archive",
            DesktopItemKind.Folder);

        Assert.False(DesktopFolderDropPolicy.CanAccept([target], target));
    }

    [Fact]
    public void DesktopFolderDropRejectsMovingAParentFolderIntoItsDescendant()
    {
        var draggedFolder = CreateDesktopDropItem(
            "folder-source",
            @"C:\Users\Test\Desktop\Project",
            DesktopItemKind.Folder);
        var nestedTarget = CreateDesktopDropItem(
            "folder-target",
            @"C:\Users\Test\Desktop\Project\Archive",
            DesktopItemKind.Folder);

        Assert.False(DesktopFolderDropPolicy.CanAccept([draggedFolder], nestedTarget));
    }

    [Theory]
    [InlineData(true, false, false, DragDropEffects.Move)]
    [InlineData(true, false, true, DragDropEffects.Copy)]
    [InlineData(false, true, true, DragDropEffects.Move)]
    [InlineData(false, false, false, DragDropEffects.Copy)]
    public void DesktopFolderDropUsesExplorerStyleEffects(
        bool acceptsFolder,
        bool overRecycleBin,
        bool controlPressed,
        DragDropEffects expected)
    {
        Assert.Equal(
            expected,
            DesktopIconSurface.ResolveDesktopDragEffect(
                DragDropEffects.Copy | DragDropEffects.Move,
                acceptsFolder,
                overRecycleBin,
                controlPressed));
    }

    [Fact]
    public void DesktopFolderDropRejectsAnEffectNotOfferedByTheSource()
    {
        Assert.Equal(
            DragDropEffects.None,
            DesktopIconSurface.ResolveDesktopDragEffect(
                DragDropEffects.Copy,
                acceptsFolder: true,
                overRecycleBin: false,
                controlPressed: false));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    public void FolderTargetDoesNotReclassifyAnInternalBoxDragAsAFileImport(
        bool folderTargetAvailable,
        bool internalBoxItemDrag,
        bool expected)
    {
        Assert.Equal(
            expected,
            DesktopBoxForm.ShouldUseFolderDropTarget(
                folderTargetAvailable,
                internalBoxItemDrag));
    }

    [Fact]
    public void DesktopFolderDropHighlightUsesOnlyLocalIconBounds()
    {
        var iconBounds = new RectangleF(100, 50, 48, 48);

        Assert.Equal(
            new RectangleF(88, 38, 72, 72),
            DesktopIconSurface.CalculateDesktopFolderDropHighlightBounds(iconBounds));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    public void HoverTimerOnlyBypassesActiveDesktopPointerGestures(
        bool desktopPointerInteractionActive,
        bool desktopItemDragActive,
        bool expected)
    {
        Assert.Equal(
            expected,
            DesktopBoxForm.ShouldPollHoverDuringDesktopInteraction(
                desktopPointerInteractionActive,
                desktopItemDragActive));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    public void ItemHoverIsSuppressedWhileBoxScrollAnimationIsActive(
        bool scrollAnimationActive,
        bool hoverResumePending,
        bool expected)
    {
        Assert.Equal(
            expected,
            DesktopBoxForm.ShouldTrackItemHoverDuringScroll(
                scrollAnimationActive,
                hoverResumePending));
    }

    [Theory]
    [InlineData(100, 3, 120, 75)]
    [InlineData(100, 1, 120, 25)]
    [InlineData(100, 3, 60, 37.5)]
    [InlineData(100, 3, 0, 0)]
    public void SmoothScrollStepUsesFineWheelGranularity(
        double itemUnit,
        int configuredScrollLines,
        int wheelDelta,
        double expected)
    {
        Assert.Equal(
            expected,
            DesktopBoxForm.CalculateSmoothScrollStep(
                itemUnit,
                configuredScrollLines,
                wheelDelta),
            precision: 3);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void MovingBoxVisualCacheBoundsAlignToPhysicalPixels(double scale)
    {
        var boxBounds = new RectangleF(10.3f, 20.2f, 201.4f, 119.7f);

        var cacheBounds = DesktopBoxForm.CalculateMovingBoxVisualCacheBounds(
            boxBounds,
            scale);

        Assert.Equal(Math.Floor((boxBounds.Left - 2) * scale), cacheBounds.Left * scale, 3);
        Assert.Equal(Math.Floor((boxBounds.Top - 2) * scale), cacheBounds.Top * scale, 3);
        Assert.Equal(Math.Ceiling((boxBounds.Right + 2) * scale), cacheBounds.Right * scale, 3);
        Assert.Equal(Math.Ceiling((boxBounds.Bottom + 2) * scale), cacheBounds.Bottom * scale, 3);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void DropPreviewDoesNotRedrawABoxAlreadyOwnedByAnotherDynamicPass(
        bool isTransformBox,
        bool isAnimatedBox,
        bool expected)
    {
        var previewBoxId = Guid.NewGuid();
        var transformBoxId = isTransformBox ? previewBoxId : Guid.NewGuid();
        IReadOnlySet<Guid> animatedBoxIds = isAnimatedBox
            ? new HashSet<Guid> { previewBoxId }
            : new HashSet<Guid>();

        Assert.Equal(
            expected,
            DesktopBoxForm.ShouldRenderDropPreviewSeparately(
                previewBoxId,
                transformBoxId,
                animatedBoxIds));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void PureHeightAnimationSkipsFullParentRedrawOwnership(
        bool hasDynamicVisual,
        bool heightAnimationOnly,
        bool expected)
    {
        Assert.Equal(
            expected,
            DesktopBoxForm.ShouldCompositeBoxVisualsInParent(
                hasDynamicVisual,
                heightAnimationOnly));
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(true, true, false, true)]
    [InlineData(false, true, true, false)]
    public void ScrollAndHeightAnimationsUsePartialBoxComposition(
        bool heightAnimationActive,
        bool scrollAnimationActive,
        bool otherDynamicVisualActive,
        bool expected)
    {
        Assert.Equal(
            expected,
            DesktopBoxForm.IsPartialBoxAnimationOnly(
                heightAnimationActive,
                scrollAnimationActive,
                otherDynamicVisualActive));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void HeightAnimationOnlyRebuildsFullGeometryWhenRequired(
        bool compositedByIconSurface,
        bool animationCompleted,
        bool expected)
    {
        Assert.Equal(
            expected,
            DesktopBoxForm.ShouldRebuildHeightAnimationGeometry(
                compositedByIconSurface,
                animationCompleted));
    }

    [Theory]
    [InlineData(300, 340)]
    [InlineData(340, 300)]
    public void HeightAnimationOnlyInvalidatesTheMovingBottomEdge(
        float previousHeight,
        float currentHeight)
    {
        var dirtyBounds = DesktopBoxForm.CalculateHeightAnimationFrameDirtyBounds(
            new RectangleF(100, 50, 300, 400),
            previousHeight,
            currentHeight,
            cornerRadius: 12);

        Assert.Equal(new RectangleF(100, 338, 300, 52), dirtyBounds);
    }

    [Theory]
    [InlineData(true, true, false, false, true)]
    [InlineData(false, true, false, false, false)]
    [InlineData(true, false, false, false, false)]
    [InlineData(true, true, true, false, false)]
    [InlineData(true, true, false, true, false)]
    public void CompletedSingleHeightAnimationCommitsThroughPartialFrame(
        bool compositedByIconSurface,
        bool hasCompletedAnimation,
        bool animationStillActive,
        bool otherDynamicVisualActive,
        bool expected)
    {
        Assert.Equal(
            expected,
            DesktopBoxForm.ShouldCommitCompletedHeightAnimationPartially(
                compositedByIconSurface,
                hasCompletedAnimation,
                animationStillActive,
                otherDynamicVisualActive,
                hasPartialRenderer: true));
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(4, 3, false)]
    [InlineData(4, 4, true)]
    public void HeightAnimationCacheWaitsForEveryRealIcon(
        int requiredIconCount,
        int loadedIconCount,
        bool expected)
    {
        Assert.Equal(
            expected,
            DesktopBoxForm.ShouldCreateHeightAnimationVisualCache(
                requiredIconCount,
                loadedIconCount));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void LoadedIconOnlyRequestsAFrameWhenItsBoxIsSettledAndVisible(
        bool effectivelyCollapsed,
        bool heightAnimationActive,
        bool expected)
    {
        Assert.Equal(
            expected,
            DesktopBoxForm.ShouldPresentLoadedBoxIcon(
                effectivelyCollapsed,
                heightAnimationActive));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void ExpandedBoxRetainsAnimationCacheForItsLaterCollapse(
        bool effectivelyCollapsed,
        bool expected)
    {
        Assert.Equal(
            expected,
            DesktopBoxForm.ShouldRetainHeightAnimationVisualCache(effectivelyCollapsed));
    }

    [Theory]
    [InlineData(248, 248, 150)]
    [InlineData(124, 248, 75)]
    [InlineData(20, 248, 60)]
    public void BoxHeightAnimationUsesResponsiveDistanceScaledTiming(
        double remainingDistance,
        double fullDistance,
        double expectedMilliseconds)
    {
        Assert.Equal(
            expectedMilliseconds,
            DesktopBoxForm.CalculateBoxHeightAnimationDuration(
                remainingDistance,
                fullDistance).TotalMilliseconds,
            precision: 3);
    }

    [Theory]
    [InlineData(420, 0, true, 420)]
    [InlineData(420, 300, false, 300)]
    [InlineData(0, 0, true, 0)]
    public void HeightAnimationDoesNotOverwriteTheSettledScrollOffset(
        double storedOffset,
        double calculatedOffset,
        bool heightAnimationActive,
        double expected)
    {
        Assert.Equal(
            expected,
            DesktopBoxForm.ResolvePersistedScrollOffset(
                storedOffset,
                calculatedOffset,
                heightAnimationActive));
    }

    [Theory]
    [InlineData(false, true, true, true, 120, true)]
    [InlineData(true, true, true, true, 120, false)]
    [InlineData(false, false, true, true, 120, true)]
    [InlineData(false, true, false, true, 120, false)]
    [InlineData(false, true, true, false, 120, false)]
    [InlineData(false, true, true, true, 0, false)]
    public void BoxDragWheelRoutesOnlyToTheBoxUnderThePointer(
        bool controlPressed,
        bool isDesktopSurface,
        bool pointerOverBox,
        bool boxItemDragActive,
        int delta,
        bool expected)
    {
        Assert.Equal(
            expected,
            DesktopInputMonitor.ShouldRouteBoxDragWheel(
                controlPressed,
                isDesktopSurface,
                pointerOverBox,
                boxItemDragActive,
                delta));
    }

    [Fact]
    public void ScrollBarViewportReachesBoxEdgeWithoutExpandingTheContentBody()
    {
        var boxBounds = new RectangleF(20, 40, 240, 180);
        var bodyBounds = new RectangleF(28, 76, 224, 136);

        var viewport = DesktopBoxForm.CalculateScrollBarViewport(boxBounds, bodyBounds);

        Assert.Equal(bodyBounds.X, viewport.X);
        Assert.Equal(bodyBounds.Y, viewport.Y);
        Assert.Equal(bodyBounds.Height, viewport.Height);
        Assert.Equal(boxBounds.Right, viewport.X + viewport.Width);
        Assert.True(viewport.Width > bodyBounds.Width);
    }

    [Theory]
    [InlineData(true, false, false, false, true, true)]
    [InlineData(true, false, false, false, false, true)]
    [InlineData(true, true, true, false, true, false)]
    [InlineData(true, true, true, false, false, true)]
    [InlineData(true, false, true, false, false, false)]
    [InlineData(true, true, false, true, false, false)]
    [InlineData(false, true, false, false, false, false)]
    public void HeightAnimationUsesOneParentLayerWithoutACompositorHandoff(
        bool partialAnimationEligible,
        bool boxVisualsInParent,
        bool pointerGhostOverlayActive,
        bool selecting,
        bool staticFrameChanged,
        bool expected)
    {
        Assert.Equal(
            expected,
            DesktopIconSurface.ShouldPresentPartialBoxAnimationInParent(
                partialAnimationEligible,
                boxVisualsInParent,
                pointerGhostOverlayActive,
                selecting,
                staticFrameChanged));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void PureHeightAnimationDoesNotUseTheChildDragOverlay(
        bool pointerGhostOverlayActive,
        bool expected)
    {
        Assert.Equal(
            expected,
            DesktopIconSurface.ShouldHideDragOverlayAfterPartialBoxAnimation(
                pointerGhostOverlayActive));
    }

    [Theory]
    [InlineData(true, false, false, false, true)]
    [InlineData(false, false, false, false, false)]
    [InlineData(true, true, false, false, false)]
    [InlineData(true, false, true, false, false)]
    [InlineData(true, false, false, true, false)]
    public void HeightAnimationRemainsPartialWithPassiveDropFeedback(
        bool heightAnimationActive,
        bool transformActive,
        bool selectionActive,
        bool scrollAnimationActive,
        bool expected)
    {
        Assert.Equal(
            expected,
            DesktopBoxForm.CanUsePartialHeightAnimationComposition(
                heightAnimationActive,
                transformActive,
                selectionActive,
                scrollAnimationActive));
    }

    [Theory]
    [InlineData(false, false, false, false, false, false)]
    [InlineData(true, false, false, false, false, true)]
    [InlineData(false, false, false, true, false, true)]
    [InlineData(false, true, false, true, false, false)]
    [InlineData(false, false, true, true, false, false)]
    [InlineData(false, false, false, true, true, false)]
    public void PureScrollAndHeightAnimationsStayOnTheParentLayer(
        bool heightAnimationActive,
        bool transformActive,
        bool selectionActive,
        bool scrollAnimationActive,
        bool otherDynamicVisualActive,
        bool expected)
    {
        Assert.Equal(
            expected,
            DesktopBoxForm.CanUsePartialBoxAnimationComposition(
                heightAnimationActive,
                transformActive,
                selectionActive,
                scrollAnimationActive,
                otherDynamicVisualActive));
    }

    [Theory]
    [InlineData(true, HorizontalAlignment.Center)]
    [InlineData(false, HorizontalAlignment.Left)]
    public void InlineRenameAlignmentMatchesWrappedAndListLabels(
        bool wordWrap,
        HorizontalAlignment expected)
    {
        Assert.Equal(expected, DesktopRenameEditor.ResolveTextAlignment(wordWrap));
    }

    [Theory]
    [InlineData(20, 88, 22)]
    [InlineData(20, 18, 18)]
    public void SingleLineRenameEditorUsesLabelHeight(
        float lineHeight,
        float rowHeight,
        float expected)
    {
        Assert.Equal(
            expected,
            DesktopRenameEditor.CalculateSingleLineEditorHeight(lineHeight, rowHeight));
    }

    [Theory]
    [InlineData(128, 20, 128)]
    [InlineData(20, 128, 20)]
    public void SelectedBoxLabelKeepsItsMeasuredHeightBeyondTheVisibleBody(
        float measuredHeight,
        float availableHeight,
        float expected)
    {
        Assert.Equal(
            expected,
            DesktopBoxForm.ResolveSelectedGridLabelHeight(measuredHeight, availableHeight));
    }

    [Theory]
    [InlineData(10.25, 20.5, 100.25, 80.25, 1.0, 8, 18, 105, 85)]
    [InlineData(10.25, 20.5, 100.25, 80.25, 1.5, 12, 27, 157, 128)]
    [InlineData(-20, -10, 60, 50, 2.0, 0, 0, 84, 84)]
    public void PartialBoxAnimationDirtyBoundsAreInflatedAlignedAndClipped(
        double x,
        double y,
        double width,
        double height,
        double scale,
        int expectedX,
        int expectedY,
        int expectedWidth,
        int expectedHeight)
    {
        var dirtyBounds = DesktopIconSurface.CalculatePartialBoxAnimationDirtyPixels(
            new RectangleF((float)x, (float)y, (float)width, (float)height),
            scale,
            new Size(1920, 1080));

        Assert.Equal(new Rectangle(
            expectedX,
            expectedY,
            expectedWidth,
            expectedHeight), dirtyBounds);
    }

    [Fact]
    public void MovingBoxFrameInvalidatesItsPreviousAndCurrentVisualBounds()
    {
        var previous = new RectangleF(100, 80, 260, 320);
        var current = new RectangleF(130, 110, 260, 320);

        Assert.Equal(
            new RectangleF(100, 80, 290, 350),
            DesktopIconSurface.CalculateDynamicBoxFrameDirtyBounds(previous, current));
    }

    [Fact]
    public void DesktopDropDirtyBoundsOnlyContainMovedAndDraggedIcons()
    {
        var before = new Dictionary<string, RectangleF>(StringComparer.OrdinalIgnoreCase)
        {
            ["dragged"] = new RectangleF(10, 20, 70, 84),
            ["displaced"] = new RectangleF(110, 20, 70, 84),
            ["unchanged"] = new RectangleF(610, 20, 70, 84)
        };
        var after = new Dictionary<string, RectangleF>(StringComparer.OrdinalIgnoreCase)
        {
            ["dragged"] = new RectangleF(110, 20, 70, 84),
            ["displaced"] = new RectangleF(10, 20, 70, 84),
            ["unchanged"] = new RectangleF(610, 20, 70, 84)
        };

        var dirtyBounds = DesktopIconSurface.CalculateDesktopDropDirtyBounds(
            before,
            after,
            ["dragged"])
            .OrderBy(bounds => bounds.X)
            .ToArray();

        Assert.Equal(
            [
                new RectangleF(10, 20, 70, 84),
                new RectangleF(110, 20, 70, 84)
            ],
            dirtyBounds);
    }

    [Fact]
    public void CancelledDesktopDropStillRestoresOnlyTheDraggedIconRegion()
    {
        var settledBounds = new Dictionary<string, RectangleF>(StringComparer.OrdinalIgnoreCase)
        {
            ["dragged"] = new RectangleF(10, 20, 70, 84),
            ["unchanged"] = new RectangleF(610, 20, 70, 84)
        };

        var dirtyBounds = DesktopIconSurface.CalculateDesktopDropDirtyBounds(
            settledBounds,
            settledBounds,
            ["dragged"]);

        Assert.Equal([new RectangleF(10, 20, 70, 84)], dirtyBounds);
    }

    [Fact]
    public void BoxAssignmentAddsTheTargetBoxToDesktopDropDirtyBounds()
    {
        var before = new Dictionary<string, RectangleF>(StringComparer.OrdinalIgnoreCase)
        {
            ["dragged"] = new RectangleF(10, 20, 70, 84),
            ["unchanged"] = new RectangleF(610, 20, 70, 84)
        };
        var after = new Dictionary<string, RectangleF>(StringComparer.OrdinalIgnoreCase)
        {
            ["unchanged"] = new RectangleF(610, 20, 70, 84)
        };
        var targetBoxBounds = new RectangleF(300, 220, 240, 180);

        var dirtyBounds = DesktopIconSurface.CalculateDesktopDropDirtyBounds(
            before,
            after,
            ["dragged"],
            targetBoxBounds)
            .OrderBy(bounds => bounds.X)
            .ToArray();

        Assert.Equal(
            [
                new RectangleF(10, 20, 70, 84),
                targetBoxBounds
            ],
            dirtyBounds);
    }

    [Fact]
    public void BoxReleaseDirtyBoundsIncludeTheNewAndDisplacedDesktopIcons()
    {
        var before = new Dictionary<string, RectangleF>(StringComparer.OrdinalIgnoreCase)
        {
            ["displaced"] = new RectangleF(10, 20, 70, 84),
            ["unchanged"] = new RectangleF(610, 20, 70, 84)
        };
        var after = new Dictionary<string, RectangleF>(StringComparer.OrdinalIgnoreCase)
        {
            ["released"] = new RectangleF(10, 20, 70, 84),
            ["displaced"] = new RectangleF(110, 20, 70, 84),
            ["unchanged"] = new RectangleF(610, 20, 70, 84)
        };

        var dirtyBounds = DesktopIconSurface.CalculateDesktopDropDirtyBounds(
            before,
            after,
            ["released"])
            .OrderBy(bounds => bounds.X)
            .ToArray();

        Assert.Equal(
            [
                new RectangleF(10, 20, 70, 84),
                new RectangleF(110, 20, 70, 84)
            ],
            dirtyBounds);
    }

    [Theory]
    [InlineData(70, 120, 70)]
    [InlineData(120, 500, 120)]
    [InlineData(180, 120, 120)]
    public void RenameEditorWidthMatchesStaticLabelLayout(
        float labelLayoutWidth,
        float availableWidth,
        float expected)
    {
        Assert.Equal(
            expected,
            DesktopRenameEditor.CalculateEditorWidth(
                labelLayoutWidth,
                availableWidth));
    }

    [Theory]
    [InlineData(48, 16, 200, 50)]
    [InlineData(8, 16, 200, 18)]
    [InlineData(240, 16, 80, 80)]
    public void RenameEditorHeightFitsTheCompleteWrappedName(
        float wrappedTextHeight,
        float lineHeight,
        float availableHeight,
        float expected)
    {
        Assert.Equal(
            expected,
            DesktopRenameEditor.CalculateEditorHeight(
                wrappedTextHeight,
                lineHeight,
                availableHeight));
    }

    [Theory]
    [InlineData("item-a", "item-a", 400, false)]
    [InlineData("item-a", "item-a", 401, true)]
    [InlineData("item-a", "ITEM-A", 899, true)]
    [InlineData("item-a", "item-a", 900, false)]
    [InlineData("item-a", "item-b", 500, false)]
    public void SlowDoubleClickRenameRequiresTheSameItemInsideTheRenameWindow(
        string previousItemKey,
        string currentItemKey,
        int elapsedMilliseconds,
        bool expected)
    {
        var previousClickUtc = new DateTime(2026, 8, 21, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(
            expected,
            SlowDoubleClickRenamePolicy.IsSlowDoubleClick(
                previousItemKey,
                previousClickUtc,
                currentItemKey,
                previousClickUtc.AddMilliseconds(elapsedMilliseconds),
                systemDoubleClickTimeMilliseconds: 400));
    }

    private static DesktopItemRef CreateDesktopDropItem(
        string key,
        string? path,
        DesktopItemKind kind) => new()
    {
        Key = new DesktopItemKey("test", key),
        DisplayName = path is null ? key : Path.GetFileName(path),
        ParsingName = path ?? $"shell:{key}",
        FileSystemPath = path,
        Kind = kind
    };

    private static void AssertBalancedOuterVerticalSpace(ToolStripDropDownMenu menu)
    {
        menu.ShowImageMargin = false;
        menu.ShowCheckMargin = false;
        for (var index = 0; index < 3; index++)
        {
            menu.Items.Add(new ToolStripMenuItem($"Item {index}")
            {
                AutoSize = false,
                Size = new Size(180, 30),
                Margin = new Padding(1, 0, 1, 0)
            });
        }

        menu.PerformLayout();

        var topGap = menu.Items[0].Bounds.Top;
        var bottomGap = menu.ClientSize.Height - menu.Items[^1].Bounds.Bottom;
        Assert.True(topGap >= 6, $"Expected at least 6 px above the first item, got {topGap}.");
        Assert.Equal(topGap, bottomGap);
    }
}
