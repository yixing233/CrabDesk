namespace CrabDesk.Core;

public sealed record DesktopItemLayoutResult(
    IReadOnlyList<LayoutRect> Items,
    double ScrollOffset,
    double MaxScroll);

public readonly record struct DesktopItemLayoutEntry(int Index, LayoutRect Bounds);

public sealed record VisibleDesktopItemLayoutResult(
    IReadOnlyList<DesktopItemLayoutEntry> Items,
    double ScrollOffset,
    double MaxScroll);

public static class DesktopItemLayoutEngine
{
    public static double GetGridCellWidth(double iconSize, double horizontalSpacing)
    {
        iconSize = Math.Clamp(iconSize, 24, 96);
        return Math.Max(Math.Clamp(horizontalSpacing, 56, 160), iconSize + 34);
    }

    public static double GetGridCellHeight(double iconSize, double verticalSpacing)
    {
        iconSize = Math.Clamp(iconSize, 24, 96);
        return Math.Max(Math.Clamp(verticalSpacing, 56, 180), iconSize + 38);
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

    public static double SnapBoxWidth(
        BoxViewMode viewMode,
        double requestedWidth,
        double iconSize,
        double horizontalSpacing)
    {
        var minimum = GetMinimumBoxWidth(viewMode, iconSize, horizontalSpacing);
        if (viewMode == BoxViewMode.List)
        {
            return Math.Max(minimum, requestedWidth);
        }

        const double bodyHorizontalPadding = 16;
        var cellWidth = GetGridCellWidth(iconSize, horizontalSpacing);
        var columns = Math.Max(
            2,
            (int)Math.Round(
                Math.Max(0, requestedWidth - bodyHorizontalPadding) / cellWidth,
                MidpointRounding.AwayFromZero));
        return Math.Max(minimum, bodyHorizontalPadding + columns * cellWidth);
    }

    public static double SnapBoxHeight(
        BoxViewMode viewMode,
        double requestedHeight,
        double titleBarHeight,
        double iconSize,
        double verticalSpacing)
    {
        const double bodyVerticalPadding = 16;
        var cellHeight = viewMode == BoxViewMode.List
            ? Math.Max(48, Math.Clamp(iconSize, 24, 96) + 12)
            : GetGridCellHeight(iconSize, verticalSpacing);
        var availableHeight = Math.Max(0, requestedHeight - titleBarHeight - bodyVerticalPadding);
        var rows = Math.Max(
            1,
            (int)Math.Round(availableHeight / cellHeight, MidpointRounding.AwayFromZero));
        return titleBarHeight + bodyVerticalPadding + rows * cellHeight;
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
        var cellHeight = GetGridCellHeight(iconSize, verticalSpacing);
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

    public static VisibleDesktopItemLayoutResult CalculateVisible(
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
        if (itemCount == 0 || body.Width <= 0 || body.Height <= 0)
        {
            return new VisibleDesktopItemLayoutResult([], 0, 0);
        }

        if (viewMode == BoxViewMode.List)
        {
            var rowHeight = Math.Max(48, iconSize + 12);
            var maxScroll = Math.Max(0, itemCount * rowHeight - body.Height);
            var scroll = Math.Clamp(requestedScroll, 0, maxScroll);
            var firstIndex = Math.Clamp((int)Math.Floor(scroll / rowHeight), 0, itemCount - 1);
            var lastIndex = Math.Clamp(
                (int)Math.Ceiling((scroll + body.Height) / rowHeight),
                firstIndex,
                itemCount - 1);
            var items = Enumerable.Range(firstIndex, lastIndex - firstIndex + 1)
                .Select(index => new DesktopItemLayoutEntry(index, new LayoutRect(
                    body.X,
                    body.Y + index * rowHeight - scroll,
                    body.Width,
                    rowHeight)))
                .ToArray();
            return new VisibleDesktopItemLayoutResult(items, scroll, maxScroll);
        }

        var cellWidth = GetGridCellWidth(iconSize, horizontalSpacing);
        var cellHeight = GetGridCellHeight(iconSize, verticalSpacing);
        var columns = Math.Max(1, (int)(body.Width / cellWidth));
        var rows = (int)Math.Ceiling(itemCount / (double)columns);
        var maxGridScroll = Math.Max(0, rows * cellHeight - body.Height);
        var gridScroll = Math.Clamp(requestedScroll, 0, maxGridScroll);
        var firstRow = Math.Clamp((int)Math.Floor(gridScroll / cellHeight), 0, Math.Max(0, rows - 1));
        var lastRow = Math.Clamp(
            (int)Math.Ceiling((gridScroll + body.Height) / cellHeight),
            firstRow,
            Math.Max(firstRow, rows - 1));
        var firstGridIndex = firstRow * columns;
        var lastGridIndex = Math.Min(itemCount - 1, (lastRow + 1) * columns - 1);
        var gridItems = Enumerable.Range(firstGridIndex, lastGridIndex - firstGridIndex + 1)
            .Select(index => new DesktopItemLayoutEntry(index, new LayoutRect(
                body.X + index % columns * cellWidth,
                body.Y + index / columns * cellHeight - gridScroll,
                cellWidth,
                cellHeight)))
            .ToArray();
        return new VisibleDesktopItemLayoutResult(gridItems, gridScroll, maxGridScroll);
    }
}
