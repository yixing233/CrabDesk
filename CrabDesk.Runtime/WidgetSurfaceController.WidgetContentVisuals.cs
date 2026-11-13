using System.Drawing;
using CrabDesk.Core;

namespace CrabDesk.Runtime;

internal abstract partial class WidgetSurfaceController
{
    protected static Color ContentTone(string name, Color text) => ColorTranslator.FromHtml(WidgetContentVisuals.Tone(name, text.R > 128));

    protected static void DrawContentIcon(Graphics graphics, string name, RectangleF bounds, float opacity = 1)
        => WidgetColorIcons.Draw(graphics, name, bounds, opacity);

    protected static void DrawContentWash(Graphics graphics, RectangleF bounds, Color tone, int alpha = 18, float radius = 7)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        using var path = RoundedRectangle(bounds, radius);
        using var brush = new SolidBrush(Color.FromArgb(alpha, tone));
        graphics.FillPath(brush, path);
    }
}
