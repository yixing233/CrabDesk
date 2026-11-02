namespace CrabDesk.Core;

[Flags]
public enum WidgetResizeEdges
{
    None = 0, Left = 1, Right = 2, Top = 4, Bottom = 8,
    TopLeft = Top | Left, TopRight = Top | Right, BottomLeft = Bottom | Left, BottomRight = Bottom | Right
}

public static class WidgetSizing
{
    public const double MinimumWidth = 180;
    public const double MinimumHeight = 120;

    public static WidgetResizeEdges HitTest(DesktopWidget widget, double x, double y)
    {
        if (!DesktopWidgets.SupportsFreeResize(widget.Kind) || widget.IsLocked || !widget.Bounds.Contains(x, y)) return WidgetResizeEdges.None;
        var bounds = widget.Bounds; var right = bounds.X + bounds.Width; var bottom = bounds.Y + bounds.Height;
        // Wider corner targets remain reachable inside the rounded card shape.
        const double corner = 16, edge = 6;
        var nearBorder = x <= bounds.X + 8 || x >= right - 8 || y <= bounds.Y + 8 || y >= bottom - 8;
        if (nearBorder && x <= bounds.X + corner && y <= bounds.Y + corner) return WidgetResizeEdges.TopLeft;
        if (nearBorder && x >= right - corner && y <= bounds.Y + corner) return WidgetResizeEdges.TopRight;
        if (nearBorder && x <= bounds.X + corner && y >= bottom - corner) return WidgetResizeEdges.BottomLeft;
        if (nearBorder && x >= right - corner && y >= bottom - corner) return WidgetResizeEdges.BottomRight;
        if (x <= bounds.X + edge) return WidgetResizeEdges.Left;
        if (x >= right - edge) return WidgetResizeEdges.Right;
        if (y <= bounds.Y + edge) return WidgetResizeEdges.Top;
        if (y >= bottom - edge) return WidgetResizeEdges.Bottom;
        return WidgetResizeEdges.None;
    }

    public static LayoutRect Resize(LayoutRect start, WidgetResizeEdges edges, double deltaX, double deltaY, LayoutRect workArea)
    {
        if (!double.IsFinite(deltaX) || !double.IsFinite(deltaY) || workArea.Width <= 0 || workArea.Height <= 0) return start;
        var minWidth = Math.Min(MinimumWidth, workArea.Width); var minHeight = Math.Min(MinimumHeight, workArea.Height);
        start = start.Clamp(workArea, minWidth, minHeight);
        var left = start.X; var top = start.Y; var right = left + start.Width; var bottom = top + start.Height;
        if (edges.HasFlag(WidgetResizeEdges.Left)) left = Math.Clamp(left + deltaX, workArea.X, right - minWidth);
        if (edges.HasFlag(WidgetResizeEdges.Right)) right = Math.Clamp(right + deltaX, left + minWidth, workArea.X + workArea.Width);
        if (edges.HasFlag(WidgetResizeEdges.Top)) top = Math.Clamp(top + deltaY, workArea.Y, bottom - minHeight);
        if (edges.HasFlag(WidgetResizeEdges.Bottom)) bottom = Math.Clamp(bottom + deltaY, top + minHeight, workArea.Y + workArea.Height);
        return new(left, top, right - left, bottom - top);
    }
}
