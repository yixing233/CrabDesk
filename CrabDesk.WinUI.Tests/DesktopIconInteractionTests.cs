using System.Windows.Forms;
using CrabDesk.Runtime;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class DesktopIconInteractionTests
{
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
}
