namespace CrabDesk.Core;

public sealed record WidgetCenterLayoutResult(Dictionary<Guid, LayoutRect> Bounds, double Height);

public static class WidgetCenterLayout
{
    // Center bounds are transient and never replace a widget's saved desktop bounds.
    public static WidgetCenterLayoutResult Arrange(IReadOnlyList<DesktopWidget> widgets, double availableWidth, double gap = 16,
        double gripHeight = 0, IReadOnlyList<Guid>? order = null)
    {
        var bounds = new Dictionary<Guid, LayoutRect>();
        if (!double.IsFinite(availableWidth) || availableWidth <= 0) return new(bounds, 0);
        gap = double.IsFinite(gap) ? Math.Max(0, gap) : 16;
        gripHeight = double.IsFinite(gripHeight) ? Math.Max(0, gripHeight) : 0;
        var orderedWidgets = widgets.Where(widget => widget.Placement == WidgetPlacement.Center)
            .OrderBy(widget => widget.CenterOrder).ThenBy(widget => widget.Id);
        if (order is not null)
        {
            var indices = new Dictionary<Guid, int>();
            for (var index = 0; index < order.Count; index++) indices.TryAdd(order[index], index);
            orderedWidgets = orderedWidgets.OrderBy(widget => indices.GetValueOrDefault(widget.Id, int.MaxValue))
                .ThenBy(widget => widget.CenterOrder).ThenBy(widget => widget.Id);
        }
        double x = 0, y = 0, rowHeight = 0;
        foreach (var widget in orderedWidgets)
        {
            var (width, height) = DesktopWidgets.Dimensions(widget, widget.Size);
            width = Math.Min(width, availableWidth);
            if (x > 0 && x + width > availableWidth)
            {
                x = 0;
                y += rowHeight + gap;
                rowHeight = 0;
            }
            bounds[widget.Id] = new(x, y + gripHeight, width, height);
            x += width + gap;
            rowHeight = Math.Max(rowHeight, height + gripHeight);
        }
        return new(bounds, y + rowHeight);
    }
}
