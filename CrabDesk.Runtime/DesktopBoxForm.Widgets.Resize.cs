using System.Drawing;
using CrabDesk.Core;
using Forms = System.Windows.Forms;

namespace CrabDesk.Runtime;

internal sealed partial class DesktopBoxForm
{
    private WidgetResizeEdges _widgetResizeEdges;
    private bool _widgetResizing;

    private static Forms.Cursor WidgetResizeCursor(WidgetResizeEdges edges) => edges switch
    {
        WidgetResizeEdges.Left or WidgetResizeEdges.Right => Forms.Cursors.SizeWE,
        WidgetResizeEdges.Top or WidgetResizeEdges.Bottom => Forms.Cursors.SizeNS,
        WidgetResizeEdges.TopLeft or WidgetResizeEdges.BottomRight => Forms.Cursors.SizeNWSE,
        WidgetResizeEdges.TopRight or WidgetResizeEdges.BottomLeft => Forms.Cursors.SizeNESW,
        _ => Forms.Cursors.Default
    };

    private void UpdateWidgetResize(DesktopWidget widget, PointF point)
    {
        if (widget.IsLocked || _runtime.IsPaused || !_runtime.State.Widgets.Contains(widget)) return;
        var deltaX = SnapDipToMonitorPixel(point.X - _widgetPressPoint.X, _scale);
        var deltaY = SnapDipToMonitorPixel(point.Y - _widgetPressPoint.Y, _scale);
        if (!_widgetResizing && Math.Abs(deltaX) < 2 && Math.Abs(deltaY) < 2) return;
        var next = WidgetSizing.Resize(_widgetStartBounds, _widgetResizeEdges, deltaX, deltaY, CrabDeskRuntime.WidgetWorkArea(_monitor));
        if (next == widget.Bounds) return;
        _widgetResizing = true;
        widget.Size = DesktopWidgetSize.Custom;
        widget.Bounds = next;
        _runtime.RefreshWidgetPosition(widget, save: false);
    }
}
