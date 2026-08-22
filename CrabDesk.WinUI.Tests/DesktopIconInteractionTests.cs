using System.Drawing;
using System.Windows.Forms;
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
    public void PureHeightAnimationUsesTheSmallOverlayInsteadOfTheFullParentLayer(
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
    [InlineData(true, false, false, 0, true)]
    [InlineData(false, false, false, 0, false)]
    [InlineData(true, true, false, 0, false)]
    [InlineData(true, false, true, 0, false)]
    [InlineData(true, false, false, 1, false)]
    public void PureBoxAnimationUsesTheSingleParentLayer(
        bool partialAnimationOnly,
        bool selecting,
        bool dragging,
        int boxDropItemCount,
        bool expected)
    {
        Assert.Equal(
            expected,
            DesktopIconSurface.ShouldPresentPartialBoxAnimationInParent(
                partialAnimationOnly,
                selecting,
                dragging,
                boxDropItemCount));
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
