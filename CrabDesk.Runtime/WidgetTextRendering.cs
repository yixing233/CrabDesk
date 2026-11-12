using System;
using System.Drawing;
using System.Drawing.Text;
using System.Runtime.CompilerServices;
using CrabDesk.Core;

namespace CrabDesk.Runtime;

internal static class WidgetTextRendering
{
    private sealed class Context { internal DesktopWidget? Widget; }
    private static readonly ConditionalWeakTable<Graphics, Context> Contexts = new();

    internal static DesktopWidget? Current(Graphics graphics) => Contexts.TryGetValue(graphics, out var context) ? context.Widget : null;

    internal static IDisposable Begin(Graphics graphics, DesktopWidget widget)
    {
        var context = Contexts.GetValue(graphics, _ => new Context());
        var scope = new Scope(graphics, context, context.Widget, graphics.TextRenderingHint);
        context.Widget = widget.Kind == DesktopWidgetKind.Lyrics ? null : widget;
        // ClearType on an alpha layer produces opaque, hard edges. Grayscale
        // antialiasing keeps coverage in alpha for the desktop compositor.
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        return scope;
    }

    internal static Font CreateFont(Graphics graphics, float size, bool bold = false, bool strike = false, bool scale = true) =>
        CreateFont(Current(graphics), size, bold, strike, scale);

    internal static Font CreateFont(DesktopWidget? widget, float size, bool bold = false, bool strike = false, bool scale = true)
    {
        var weight = widget?.FontWeight ?? 0;
        var style = (weight == 0 ? bold : weight == 700) ? FontStyle.Bold : FontStyle.Regular;
        if (widget?.FontItalic == true) style |= FontStyle.Italic;
        if (strike) style |= FontStyle.Strikeout;
        return new Font(WidgetTypography.NormalizeFamily(widget?.FontFamily),
            size * (scale ? (float)WidgetTypography.NormalizeScale(widget?.FontScale ?? 1) : 1), style, GraphicsUnit.Pixel);
    }

    private sealed class Scope(Graphics graphics, Context context, DesktopWidget? previous, TextRenderingHint hint) : IDisposable
    {
        public void Dispose()
        {
            context.Widget = previous;
            graphics.TextRenderingHint = hint;
        }
    }
}
