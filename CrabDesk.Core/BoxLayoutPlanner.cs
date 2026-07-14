namespace CrabDesk.Core;

public static class BoxLayoutPlanner
{
    private const double Margin = 24;
    private const double Gap = 20;
    private const double SearchStep = 20;

    public static IReadOnlyList<LayoutRect> Arrange(
        LayoutRect workArea,
        IReadOnlyList<LayoutRect> requestedSizes,
        IReadOnlyList<LayoutRect>? occupied = null)
    {
        var placed = new List<LayoutRect>();
        var unavailable = new List<LayoutRect>(occupied ?? []);
        var localArea = new LayoutRect(0, 0, Math.Max(220, workArea.Width), Math.Max(120, workArea.Height));

        foreach (var requested in requestedSizes)
        {
            var width = Math.Clamp(requested.Width, 260, Math.Max(260, localArea.Width - Margin * 2));
            var height = Math.Clamp(requested.Height, 160, Math.Max(160, localArea.Height - Margin * 2));
            var candidate = FindFreePosition(localArea, width, height, unavailable);
            placed.Add(candidate);
            unavailable.Add(candidate);
        }

        return placed;
    }

    private static LayoutRect FindFreePosition(
        LayoutRect area,
        double width,
        double height,
        IReadOnlyList<LayoutRect> unavailable)
    {
        var maxX = Math.Max(Margin, area.Width - Margin - width);
        var maxY = Math.Max(Margin, area.Height - Margin - height);
        LayoutRect? best = null;
        var bestOverlap = double.MaxValue;

        for (var y = Margin; y <= maxY + 0.1; y += SearchStep)
        {
            for (var x = maxX; x >= Margin - 0.1; x -= SearchStep)
            {
                var candidate = new LayoutRect(Math.Max(Margin, x), Math.Min(y, maxY), width, height);
                var overlap = unavailable.Sum(existing => OverlapArea(Inflate(existing, Gap), candidate));
                if (overlap <= 0)
                {
                    return candidate;
                }
                if (overlap < bestOverlap)
                {
                    best = candidate;
                    bestOverlap = overlap;
                }
            }
        }

        return best ?? new LayoutRect(Margin, Margin, width, height).Clamp(area);
    }

    private static LayoutRect Inflate(LayoutRect rectangle, double amount) => new(
        rectangle.X - amount,
        rectangle.Y - amount,
        rectangle.Width + amount * 2,
        rectangle.Height + amount * 2);

    private static double OverlapArea(LayoutRect first, LayoutRect second)
    {
        var width = Math.Max(0, Math.Min(first.X + first.Width, second.X + second.Width) - Math.Max(first.X, second.X));
        var height = Math.Max(0, Math.Min(first.Y + first.Height, second.Y + second.Height) - Math.Max(first.Y, second.Y));
        return width * height;
    }
}
