namespace CrabDesk.Core;

public enum FileShelfDockEdge { None, Left, Right, Top, Bottom }

public sealed class FileShelfFloatingSettings
{
    public bool Enabled { get; set; }
    public bool AutoHide { get; set; } = true;
    public string MonitorId { get; set; } = string.Empty;
    // Expanded bounds in DIPs, relative to the monitor work area. A collapsed tab is never saved as the full window.
    public LayoutRect? Bounds { get; set; }
    public FileShelfDockEdge DockEdge { get; set; }

    public void Normalize()
    {
        MonitorId ??= string.Empty;
        if (!Enum.IsDefined(DockEdge)) DockEdge = FileShelfDockEdge.None;
        if (Bounds is { } b && (!double.IsFinite(b.X) || !double.IsFinite(b.Y) ||
            !double.IsFinite(b.Width) || !double.IsFinite(b.Height) || b.Width < 240 || b.Height < 180)) Bounds = null;
    }
}

public static class FileShelfPlacement
{
    public static LayoutRect Clamp(LayoutRect bounds, LayoutRect area) => bounds.Clamp(area, 1, 1);

    public static (LayoutRect Bounds, FileShelfDockEdge Edge) Snap(LayoutRect bounds, LayoutRect area, double threshold)
    {
        bounds = Clamp(bounds, area);
        var distances = new[] { (Math.Abs(bounds.X - area.X), FileShelfDockEdge.Left),
            (Math.Abs(bounds.X + bounds.Width - area.X - area.Width), FileShelfDockEdge.Right),
            (Math.Abs(bounds.Y - area.Y), FileShelfDockEdge.Top),
            (Math.Abs(bounds.Y + bounds.Height - area.Y - area.Height), FileShelfDockEdge.Bottom) };
        var nearest = distances.MinBy(entry => entry.Item1);
        return nearest.Item1 <= threshold ? (Dock(bounds, area, nearest.Item2), nearest.Item2) : (bounds, FileShelfDockEdge.None);
    }

    public static LayoutRect Dock(LayoutRect bounds, LayoutRect area, FileShelfDockEdge edge)
    {
        bounds = Clamp(bounds, area);
        return edge switch
        {
            FileShelfDockEdge.Left => bounds with { X = area.X },
            FileShelfDockEdge.Right => bounds with { X = area.X + area.Width - bounds.Width },
            FileShelfDockEdge.Top => bounds with { Y = area.Y },
            FileShelfDockEdge.Bottom => bounds with { Y = area.Y + area.Height - bounds.Height },
            _ => bounds
        };
    }

    public static LayoutRect Collapsed(LayoutRect expanded, LayoutRect area, FileShelfDockEdge edge, double scale)
    {
        var size = 40 * scale;
        var tab = new LayoutRect(expanded.X + (expanded.Width - size) / 2,
            expanded.Y + (expanded.Height - size) / 2, size, size);
        return Dock(tab, area, edge);
    }

    public static LayoutRect Retracted(LayoutRect tab, FileShelfDockEdge edge, double progress)
    {
        var fraction = .4 * Math.Clamp(progress, 0, 1);
        return edge switch
        {
            FileShelfDockEdge.Left => tab with { X = tab.X - tab.Width * fraction },
            FileShelfDockEdge.Right => tab with { X = tab.X + tab.Width * fraction },
            FileShelfDockEdge.Top => tab with { Y = tab.Y - tab.Height * fraction },
            FileShelfDockEdge.Bottom => tab with { Y = tab.Y + tab.Height * fraction },
            _ => tab
        };
    }

    public static LayoutRect Visible(LayoutRect bounds, LayoutRect area)
    {
        var left = Math.Max(bounds.X, area.X); var top = Math.Max(bounds.Y, area.Y);
        return new(left, top,
            Math.Max(0, Math.Min(bounds.X + bounds.Width, area.X + area.Width) - left),
            Math.Max(0, Math.Min(bounds.Y + bounds.Height, area.Y + area.Height) - top));
    }
}
