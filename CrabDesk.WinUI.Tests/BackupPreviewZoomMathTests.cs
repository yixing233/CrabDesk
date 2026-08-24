using CrabDesk.WinUI.Windows;
using Microsoft.UI.Input;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class BackupPreviewZoomMathTests
{
    [Fact]
    public void CalculateAnchoredOffsetsKeepsPointerContentInPlace()
    {
        var offsets = BackupPreviewZoomMath.CalculateAnchoredOffsets(
            horizontalOffset: 300,
            verticalOffset: 180,
            pointerX: 100,
            pointerY: 60,
            currentZoom: 2,
            targetZoom: 3);

        Assert.Equal(500, offsets.Horizontal);
        Assert.Equal(300, offsets.Vertical);
    }

    [Fact]
    public void WindowDragStartsOnlyForMouseLeftButton()
    {
        Assert.True(BackupPreviewWindow.CanStartWindowDrag(PointerDeviceType.Mouse, isLeftButtonPressed: true));
        Assert.False(BackupPreviewWindow.CanStartWindowDrag(PointerDeviceType.Mouse, isLeftButtonPressed: false));
        Assert.False(BackupPreviewWindow.CanStartWindowDrag(PointerDeviceType.Touch, isLeftButtonPressed: true));
    }

    [Fact]
    public void CalculateWindowPositionFollowsScreenCursor()
    {
        var position = BackupPreviewWindowDragMath.CalculatePositionFromScreenCursor(
            startWindowX: 400,
            startWindowY: 250,
            startCursorX: 1000,
            startCursorY: 700,
            currentCursorX: 1030,
            currentCursorY: 745);

        Assert.Equal(430, position.X);
        Assert.Equal(295, position.Y);
    }
}
