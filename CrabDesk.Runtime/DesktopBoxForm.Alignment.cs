using System.Drawing;
using System.Drawing.Drawing2D;
using CrabDesk.Core;

namespace CrabDesk.Runtime;

internal sealed partial class DesktopBoxForm
{
    private IReadOnlyList<DesktopBoxAlignmentGuide> _boxAlignmentGuides = [];
    private string? _boxAlignmentMonitorId;

    private void ApplyBoxDragPosition(DesktopBox box, LayoutRect bounds, MonitorLayout monitor)
    {
        var area = new LayoutRect(0, 0, monitor.WorkArea.Width, monitor.WorkArea.Height);
        bounds = bounds.Clamp(area, GetMinimumBoxWidth(box));
        var height = GetVisualBoxHeight(box);
        var peers = _runtime.State.Boxes.Where(peer => peer.Id != box.Id &&
            string.Equals(peer.MonitorId, monitor.Id, StringComparison.OrdinalIgnoreCase))
            .Select(peer => peer.Bounds with { Height = GetVisualBoxHeight(peer) });
        // Use the visible title strip for a collapsed box, but keep enough
        // room for its stored expanded height so committing cannot jump it up.
        var alignmentArea = area with { Height = Math.Max(height, area.Height - (bounds.Height - height)) };
        var result = DesktopBoxAlignmentEngine.Calculate(bounds with { Height = height }, peers, alignmentArea);
        var next = bounds with { X = result.Bounds.X, Y = result.Bounds.Y };
        var guidesChanged = SetBoxAlignmentGuides(result.Guides, monitor.Id);
        var moved = box.Bounds != next;
        ApplyBoxTransform(box, next);
        if (!moved && guidesChanged) RequestDragRender();
    }

    private bool SetBoxAlignmentGuides(IReadOnlyList<DesktopBoxAlignmentGuide> guides, string? monitorId)
    {
        if (_boxAlignmentMonitorId == monitorId && _boxAlignmentGuides.SequenceEqual(guides)) return false;
        if (GetBoxAlignmentGuideBounds() is { } previous) AccumulateGuideDirtyBounds(previous);
        _boxAlignmentGuides = guides;
        _boxAlignmentMonitorId = monitorId;
        if (GetBoxAlignmentGuideBounds() is { } current) AccumulateGuideDirtyBounds(current);
        _dynamicVisualVersion++;
        return true;
    }

    private void ClearBoxAlignmentGuides() => SetBoxAlignmentGuides([], null);

    private void AccumulateGuideDirtyBounds(RectangleF bounds) =>
        AccumulateTransformDirtyBounds(new LayoutRect(bounds.X, bounds.Y, bounds.Width, bounds.Height));

    private RectangleF? GetBoxAlignmentGuideBounds()
    {
        RectangleF? bounds = null;
        foreach (var guide in _boxAlignmentGuides)
        {
            var line = guide.IsVertical
                ? new RectangleF((float)guide.Position, (float)guide.Start, 0, (float)(guide.End - guide.Start))
                : new RectangleF((float)guide.Start, (float)guide.Position, (float)(guide.End - guide.Start), 0);
            // The composited icon path may render only this dirty rectangle.
            // Include every dashed pen pixel and endpoint cap.
            line.Inflate(8, 8);
            bounds = bounds is { } previous ? RectangleF.Union(previous, line) : line;
        }
        return bounds;
    }

    private void DrawBoxAlignmentGuides(Graphics graphics, string monitorId)
    {
        if (_movingBox is null || _boxAlignmentGuides.Count == 0 ||
            !string.Equals(_boxAlignmentMonitorId, monitorId, StringComparison.OrdinalIgnoreCase)) return;
        var state = graphics.Save();
        try
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var shadow = new Pen(Color.FromArgb(145, 15, 30, 45), 2) { DashStyle = DashStyle.Dash };
            using var line = new Pen(Color.FromArgb(235, 90, 200, 255), 1) { DashStyle = DashStyle.Dash };
            foreach (var guide in _boxAlignmentGuides)
            {
                var start = guide.IsVertical ? new PointF((float)guide.Position, (float)guide.Start - 4)
                    : new PointF((float)guide.Start - 4, (float)guide.Position);
                var end = guide.IsVertical ? new PointF((float)guide.Position, (float)guide.End + 4)
                    : new PointF((float)guide.End + 4, (float)guide.Position);
                graphics.DrawLine(shadow, start, end);
                graphics.DrawLine(line, start, end);
            }
        }
        finally { graphics.Restore(state); }
    }
}
