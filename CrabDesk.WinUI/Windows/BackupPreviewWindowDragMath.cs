namespace CrabDesk.WinUI.Windows;

internal static class BackupPreviewWindowDragMath
{
    internal static (int X, int Y) CalculatePositionFromScreenCursor(
        int startWindowX,
        int startWindowY,
        int startCursorX,
        int startCursorY,
        int currentCursorX,
        int currentCursorY) =>
        (
            startWindowX + currentCursorX - startCursorX,
            startWindowY + currentCursorY - startCursorY);
}
