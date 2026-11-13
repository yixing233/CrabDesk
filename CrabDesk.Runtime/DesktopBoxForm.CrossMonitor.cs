using System.Drawing;
using CrabDesk.Core;

namespace CrabDesk.Runtime;

internal sealed partial class DesktopBoxForm
{
    private IReadOnlyList<DesktopBoxForm> _crossMonitorBoxSurfaces = [];
    private Action<DesktopBoxForm>? _crossMonitorDragRenderRequest;

    internal void ConfigureCrossMonitorDragging(
        IReadOnlyList<DesktopBoxForm> surfaces,
        Action<DesktopBoxForm> requestFrames)
    {
        _crossMonitorBoxSurfaces = surfaces;
        _crossMonitorDragRenderRequest = requestFrames;
    }

    private IEnumerable<DesktopBoxForm> IncomingBoxTransforms => _crossMonitorBoxSurfaces.Where(source =>
        !ReferenceEquals(source, this) && !source.IsDisposed &&
        source.IsTransformActive && string.Equals(source.TransformMonitorId, _monitor.Id, StringComparison.OrdinalIgnoreCase));

    private bool IsIncomingBoxTransform(Guid boxId) => IncomingBoxTransforms.Any(source =>
        (source._movingBox ?? source._resizingBox)?.Id == boxId);

    private void DrawIncomingBoxTransforms(Graphics graphics, RectangleF clipBounds)
    {
        foreach (var source in IncomingBoxTransforms)
        {
            var box = (source._movingBox ?? source._resizingBox)!;
            // The capture owner retains the active tab, scroll offset and item
            // cache. Render that view into the destination's DPI-scaled frame.
            var geometry = source.CreateBoxGeometry(box, (float)source.GetVisualBoxHeight(box), source.IsEffectivelyCollapsed(box));
            source.DrawBox(graphics, geometry, clipBounds,
                includeItemHoverFeedback: false, includeCompositedHeaderActions: true);
            source.DrawBoxAlignmentGuides(graphics, _monitor.Id);
        }
    }

    internal void RefreshTransferredBoxVisuals()
    {
        if (_resourcesDisposed || IsDisposed) return;
        _geometryDirty = true;
        PresentLayer();
    }

    protected override void UpdateMovingWidget(DesktopWidget widget, PointF point)
    {
        var screenX = _monitor.PixelBounds.X + point.X * _scale;
        var screenY = _monitor.PixelBounds.Y + point.Y * _scale;
        var target = _runtime.Monitors.FirstOrDefault(m => m.PixelBounds.Contains(screenX, screenY));
        // Unevenly arranged displays can leave a gap in the virtual desktop.
        // Keep the last valid placement until the pointer reaches a display.
        if (target is null) return;
        var targetScale = Math.Max(.1, target.DpiScale);
        var bounds = new LayoutRect(
            (screenX - target.PixelBounds.X - (_widgetPressPoint.X - _widgetStartBounds.X) * _scale) / targetScale,
            (screenY - target.PixelBounds.Y - (_widgetPressPoint.Y - _widgetStartBounds.Y) * _scale) / targetScale,
            _widgetStartBounds.Width, _widgetStartBounds.Height);
        ApplyWidgetDragPosition(widget, bounds, target);
    }
}
