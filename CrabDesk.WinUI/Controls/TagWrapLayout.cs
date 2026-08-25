using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace CrabDesk.WinUI.Controls;

/// <summary>
/// Measures variable-width tag chips into rows while reporting the complete
/// wrapped height back to the parent ItemsRepeater.
/// </summary>
public sealed class TagWrapLayout : NonVirtualizingLayout
{
    private const double HorizontalSpacing = 8d;
    private const double VerticalSpacing = 8d;

    protected override Size MeasureOverride(NonVirtualizingLayoutContext context, Size availableSize)
    {
        var availableWidth = NormalizeWidth(availableSize.Width);
        var rowWidth = 0d;
        var rowHeight = 0d;
        var desiredWidth = 0d;
        var desiredHeight = 0d;
        var hasItemInRow = false;

        foreach (var child in context.Children)
        {
            child.Measure(new Size(availableWidth, double.PositiveInfinity));
            var childSize = child.DesiredSize;
            var spacing = hasItemInRow ? HorizontalSpacing : 0d;

            if (hasItemInRow && rowWidth + spacing + childSize.Width > availableWidth)
            {
                desiredWidth = Math.Max(desiredWidth, rowWidth);
                desiredHeight += rowHeight + VerticalSpacing;
                rowWidth = 0d;
                rowHeight = 0d;
                hasItemInRow = false;
                spacing = 0d;
            }

            rowWidth += spacing + childSize.Width;
            rowHeight = Math.Max(rowHeight, childSize.Height);
            hasItemInRow = true;
        }

        if (hasItemInRow)
        {
            desiredWidth = Math.Max(desiredWidth, rowWidth);
            desiredHeight += rowHeight;
        }

        return new Size(desiredWidth, desiredHeight);
    }

    protected override Size ArrangeOverride(NonVirtualizingLayoutContext context, Size finalSize)
    {
        var availableWidth = NormalizeWidth(finalSize.Width);
        var rowWidth = 0d;
        var rowHeight = 0d;
        var y = 0d;
        var hasItemInRow = false;

        foreach (var child in context.Children)
        {
            var childSize = child.DesiredSize;
            var spacing = hasItemInRow ? HorizontalSpacing : 0d;

            if (hasItemInRow && rowWidth + spacing + childSize.Width > availableWidth)
            {
                y += rowHeight + VerticalSpacing;
                rowWidth = 0d;
                rowHeight = 0d;
                hasItemInRow = false;
                spacing = 0d;
            }

            var x = rowWidth + spacing;
            child.Arrange(new Rect(x, y, childSize.Width, childSize.Height));
            rowWidth = x + childSize.Width;
            rowHeight = Math.Max(rowHeight, childSize.Height);
            hasItemInRow = true;
        }

        return finalSize;
    }

    private static double NormalizeWidth(double width)
    {
        return double.IsNaN(width) || width <= 0d
            ? double.PositiveInfinity
            : width;
    }
}
