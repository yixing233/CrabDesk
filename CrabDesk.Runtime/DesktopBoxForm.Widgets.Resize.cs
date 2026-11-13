using System.Drawing;
using CrabDesk.Core;
using Forms = System.Windows.Forms;

namespace CrabDesk.Runtime;

internal sealed partial class DesktopBoxForm
{
    protected override void UpdateWidgetResize(DesktopWidget widget, PointF point)
    {
        if (widget.IsLocked || _runtime.IsPaused || !_runtime.State.Widgets.Contains(widget)) return;
        var deltaX = SnapDipToMonitorPixel(point.X - _widgetPressPoint.X, _scale);
        var deltaY = SnapDipToMonitorPixel(point.Y - _widgetPressPoint.Y, _scale);
        if (!_widgetResizing && Math.Abs(deltaX) < 2 && Math.Abs(deltaY) < 2) return;
        var workArea = CrabDeskRuntime.WidgetWorkArea(_monitor);
        var next = WidgetSizing.Resize(_widgetStartBounds, _widgetResizeEdges, deltaX, deltaY, workArea);
        // Snap the edges being dragged to the same lines a dragged card uses, so
        // a resized note can line up with a box or another widget instead of
        // only landing on whole pixels.
        var peers = _runtime.State.Widgets.Where(peer => peer.Placement == WidgetPlacement.Desktop && peer.Id != widget.Id &&
                string.Equals(peer.MonitorId, _monitor.Id, StringComparison.OrdinalIgnoreCase)).Select(peer => peer.Bounds)
            .Concat(_runtime.State.Boxes.Where(peer =>
                string.Equals(peer.MonitorId, _monitor.Id, StringComparison.OrdinalIgnoreCase))
                .Select(peer => peer.Bounds with { Height = GetVisualBoxHeight(peer) }));
        var snapped = DesktopBoxAlignmentEngine.SnapResize(next, _widgetResizeEdges, peers, workArea);
        next = snapped.Bounds;
        // Set the guides before the equality check: a snap that lands the card
        // back on its current bounds still has to show the line it locked onto.
        if (SetBoxAlignmentGuides(snapped.Guides, _monitor.Id) && next == widget.Bounds)
        {
            RequestDragRender();
            return;
        }
        if (next == widget.Bounds) return;
        _widgetResizing = true;
        widget.Size = DesktopWidgetSize.Custom;
        widget.Bounds = next;
        _runtime.RefreshWidgetPosition(widget, save: false);
    }
}
