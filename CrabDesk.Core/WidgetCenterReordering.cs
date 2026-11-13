namespace CrabDesk.Core;

public static class WidgetCenterReordering
{
    public static IReadOnlyList<Guid> Move(IReadOnlyList<Guid> order, Guid widgetId, int targetIndex)
    {
        if (!order.Contains(widgetId)) return order.ToArray();
        var moved = order.Distinct().ToList();
        moved.Remove(widgetId);
        moved.Insert(Math.Clamp(targetIndex, 0, moved.Count), widgetId);
        return moved;
    }

    // Hit testing always uses the original grid, not the rearranged preview bounds.
    public static int TargetIndex(IReadOnlyList<Guid> order, IReadOnlyDictionary<Guid, LayoutRect> bounds, double x, double y)
    {
        if (order.Count == 0 || !double.IsFinite(x) || !double.IsFinite(y)) return -1;
        var cards = new List<(int Index, LayoutRect Bounds)>();
        for (var index = 0; index < order.Count; index++)
        {
            if (!bounds.TryGetValue(order[index], out var rect) ||
                !double.IsFinite(rect.X) || !double.IsFinite(rect.Y) ||
                !double.IsFinite(rect.Width) || !double.IsFinite(rect.Height) ||
                rect.Width <= 0 || rect.Height <= 0 ||
                !double.IsFinite(rect.X + rect.Width) || !double.IsFinite(rect.Y + rect.Height)) continue;
            cards.Add((index, rect));
        }
        if (cards.Count == 0) return -1;
        if (y < cards.Min(card => card.Bounds.Y)) return cards[0].Index;
        if (y > cards.Max(card => card.Bounds.Y + card.Bounds.Height)) return cards[^1].Index;

        var target = cards[0].Index;
        var nearest = double.PositiveInfinity;
        foreach (var card in cards)
        {
            var rect = card.Bounds;
            var dx = Math.Max(rect.X - x, Math.Max(0, x - (rect.X + rect.Width)));
            var dy = Math.Max(rect.Y - y, Math.Max(0, y - (rect.Y + rect.Height)));
            var distance = dx * dx + dy * dy;
            if (distance >= nearest) continue;
            nearest = distance;
            target = card.Index;
        }
        return target;
    }
}
