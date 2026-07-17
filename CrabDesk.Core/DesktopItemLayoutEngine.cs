namespace CrabDesk.Core;

public sealed record DesktopItemLayoutResult(
    IReadOnlyList<LayoutRect> Items,
    double ScrollOffset,
    double MaxScroll);

public static class DesktopItemLayoutEngine
{
    public static double GetGridCellWidth(double iconSize, double horizontalSpacing)
    {
        iconSize = Math.Clamp(iconSize, 24, 96);
        return Math.Max(Math.Clamp(horizontalSpacing, 56, 160), iconSize + 34);
    }

    public static double GetMinimumBoxWidth(
        BoxViewMode viewMode,
        double iconSize,
        double horizontalSpacing)
    {
        const double titleBarMinimumWidth = 180;
        const double bodyHorizontalPadding = 16;
        return viewMode == BoxViewMode.Grid
            ? Math.Max(titleBarMinimumWidth, bodyHorizontalPadding + GetGridCellWidth(iconSize, horizontalSpacing) * 2)
            : titleBarMinimumWidth;
    }

    public static DesktopItemLayoutResult Calculate(
        BoxViewMode viewMode,
        LayoutRect body,
        int itemCount,
        double iconSize,
        double horizontalSpacing,
        double verticalSpacing,
        double requestedScroll)
    {
        itemCount = Math.Max(0, itemCount);
        iconSize = Math.Clamp(iconSize, 24, 96);
        if (viewMode == BoxViewMode.List)
        {
            var rowHeight = Math.Max(48, iconSize + 12);
            var maxScroll = Math.Max(0, itemCount * rowHeight - body.Height);
            var scroll = Math.Clamp(requestedScroll, 0, maxScroll);
            var items = Enumerable.Range(0, itemCount)
                .Select(index => new LayoutRect(
                    body.X,
                    body.Y + index * rowHeight - scroll,
                    body.Width,
                    rowHeight))
                .ToArray();
            return new DesktopItemLayoutResult(items, scroll, maxScroll);
        }

        var cellWidth = GetGridCellWidth(iconSize, horizontalSpacing);
        var cellHeight = Math.Max(Math.Clamp(verticalSpacing, 56, 180), iconSize + 46);
        var columns = Math.Max(1, (int)(body.Width / cellWidth));
        var rows = (int)Math.Ceiling(itemCount / (double)columns);
        var gridMaxScroll = Math.Max(0, rows * cellHeight - body.Height);
        var gridScroll = Math.Clamp(requestedScroll, 0, gridMaxScroll);
        var gridItems = Enumerable.Range(0, itemCount)
            .Select(index => new LayoutRect(
                body.X + index % columns * cellWidth,
                body.Y + index / columns * cellHeight - gridScroll,
                cellWidth,
                cellHeight))
            .ToArray();
        return new DesktopItemLayoutResult(gridItems, gridScroll, gridMaxScroll);
    }
}
