using System.Drawing;
using CrabDesk.Core;

namespace CrabDesk.Runtime;

// Desktop lifecycle and composition hooks stay on the Explorer surface. All
// real component drawing/input is inherited from WidgetSurfaceController.
internal sealed partial class DesktopBoxForm
{
    protected override bool IsWidgetOccluded(PointF point)
    {
        EnsureGeometry();
        return _boxes.Any(box => box.Bounds.Contains(point));
    }
    protected override bool WidgetUsesAcrylic(DesktopWidget widget) => _usesAcrylicBackground && UsesWidgetAcrylic(widget);
    protected override Bitmap? GetWidgetFileIcon(DesktopItemRef item, float iconSize) => GetIconBitmap(item, iconSize);

    private RectangleF? GetFileShelfIconDirtyBounds(IconBitmapKey key)
    {
        RectangleF? dirty = null;
        foreach (var widget in DesktopWidgetItems.Where(widget => widget.Kind == DesktopWidgetKind.FileShelf))
        {
            var layout = GetFileShelfLayout(widget, WidgetCornerRadius);
            var start = FileShelfStart(widget);
            for (var row = 0; row < layout.Rows && row + start < widget.FileShelfItems.Count; row++)
            {
                var item = GetFileShelfIconItem(widget, widget.FileShelfItems[row + start]);
                if (item is null || CreateIconBitmapKey(item, FileShelfIconSize) != key) continue;
                var bounds = new RectangleF(layout.List.X, layout.List.Y + row * layout.RowHeight, layout.List.Width, layout.RowHeight);
                dirty = dirty is { } existing ? RectangleF.Union(existing, bounds) : bounds;
            }
        }
        return dirty;
    }

    // Keep the existing reflection-based static test APIs, which do not request
    // FlattenHierarchy. These delegate rather than duplicate implementations.
    private static new RectangleF NoteFieldRectangle(DesktopWidget widget) => WidgetSurfaceController.NoteFieldRectangle(widget);
    private static new RectangleF WidgetBodyRectangle(DesktopWidget widget) => WidgetSurfaceController.WidgetBodyRectangle(widget);
    private static new float DrawTranslationField(Graphics graphics, string value, string placeholder, RectangleF field,
        Color text, Color muted, float offset, DesktopWidgetTextVisual visual) =>
        WidgetSurfaceController.DrawTranslationField(graphics, value, placeholder, field, text, muted, offset, visual);
    private static new void WidgetText(Graphics graphics, string value, RectangleF bounds, Color color, float size = 12,
        bool bold = false, bool centered = false, bool multiline = false, bool strike = false, bool rightAligned = false) =>
        WidgetSurfaceController.WidgetText(graphics, value, bounds, color, size, bold, centered, multiline, strike, rightAligned);
}
