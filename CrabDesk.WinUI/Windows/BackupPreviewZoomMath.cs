namespace CrabDesk.WinUI.Windows;

internal static class BackupPreviewZoomMath
{
    internal static (double Horizontal, double Vertical) CalculateAnchoredOffsets(
        double horizontalOffset,
        double verticalOffset,
        double pointerX,
        double pointerY,
        float currentZoom,
        float targetZoom)
    {
        var scale = targetZoom / currentZoom;
        return (
            Math.Max(0, (horizontalOffset + pointerX) * scale - pointerX),
            Math.Max(0, (verticalOffset + pointerY) * scale - pointerY));
    }
}
