using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CrabDesk.Runtime;

internal readonly record struct FluentMenuItemLayout(
    RectangleF IconBounds,
    Rectangle TextBounds,
    RectangleF ArrowBounds);

internal sealed class FluentMenuRenderer : ToolStripProfessionalRenderer
{
    private readonly bool _isDark;
    private readonly Color _background;
    private readonly Color _border;
    private readonly Color _selected;

    internal FluentMenuRenderer(bool isDark)
        : base(new ThemedTrayColorTable(isDark))
    {
        _isDark = isDark;
        _background = isDark ? Color.FromArgb(37, 40, 45) : Color.FromArgb(252, 252, 252);
        _border = isDark ? Color.FromArgb(72, 77, 86) : Color.FromArgb(210, 214, 220);
        _selected = isDark ? Color.FromArgb(55, 60, 68) : Color.FromArgb(232, 235, 239);
        RoundedEdges = false;
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs eventArgs)
    {
        eventArgs.Graphics.Clear(_background);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs eventArgs)
    {
        var bounds = new Rectangle(0, 0, eventArgs.ToolStrip.Width - 1, eventArgs.ToolStrip.Height - 1);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        var dpiScale = GetDpiScale(eventArgs.Graphics);
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = CreateRoundedPath(bounds, (int)Math.Round(8 * dpiScale));
        using var pen = new Pen(_border);
        eventArgs.Graphics.DrawPath(pen, path);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs eventArgs)
    {
        if (!eventArgs.Item.Selected && !eventArgs.Item.Pressed) return;
        var dpiScale = GetDpiScale(eventArgs.Graphics);
        var horizontalInset = (int)Math.Round(4 * dpiScale);
        var verticalInset = Math.Max(1, (int)Math.Round(dpiScale));
        var bounds = new Rectangle(
            horizontalInset,
            verticalInset,
            Math.Max(
                eventArgs.Item.Width,
                eventArgs.ToolStrip?.DisplayRectangle.Width ?? eventArgs.Item.Width) - horizontalInset * 2,
            eventArgs.Item.Height - verticalInset * 2);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = CreateRoundedPath(bounds, (int)Math.Round(5 * dpiScale));
        using var brush = new SolidBrush(_selected);
        eventArgs.Graphics.FillPath(brush, path);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs eventArgs)
    {
        var dpiScale = GetDpiScale(eventArgs.Graphics);
        var hasArrow = eventArgs.Item is ToolStripMenuItem { HasDropDownItems: true };
        var layout = CalculateItemLayout(eventArgs.Item.Size, dpiScale, hasArrow);
        if (LucideRuntimeIcons.TryGetMenuIcon(eventArgs.Item, out var icon))
        {
            LucideRuntimeIcons.Draw(
                eventArgs.Graphics,
                icon,
                layout.IconBounds,
                eventArgs.TextColor,
                layout.IconBounds.Width);
        }
        else if (eventArgs.Item is ToolStripMenuItem { Checked: true })
        {
            LucideRuntimeIcons.Draw(
                eventArgs.Graphics,
                LucideRuntimeIcon.Check,
                layout.IconBounds,
                GetCheckColor(eventArgs.Item.Enabled),
                layout.IconBounds.Width);
        }

        TextRenderer.DrawText(
            eventArgs.Graphics,
            eventArgs.Text,
            eventArgs.TextFont,
            layout.TextBounds,
            eventArgs.TextColor,
            TextFormatFlags.Left |
            TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine |
            TextFormatFlags.EndEllipsis |
            TextFormatFlags.NoPadding);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs eventArgs)
    {
        // Checks share the same explicit leading slot as action icons and are
        // rendered from OnRenderItemText. Suppress WinForms' independent
        // check/image columns so high-DPI layouts cannot drift apart.
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs eventArgs)
    {
        var item = eventArgs.Item;
        var dpiScale = GetDpiScale(eventArgs.Graphics);
        var itemSize = item?.Size ?? eventArgs.ArrowRectangle.Size;
        var layout = CalculateItemLayout(itemSize, dpiScale, hasArrow: true);
        var color = item?.Enabled != false
            ? (_isDark ? Color.FromArgb(224, 227, 232) : Color.FromArgb(61, 66, 73))
            : (_isDark ? Color.FromArgb(125, 130, 138) : Color.FromArgb(145, 150, 158));
        LucideRuntimeIcons.Draw(
            eventArgs.Graphics,
            LucideRuntimeIcon.ChevronRight,
            layout.ArrowBounds,
            color,
            layout.ArrowBounds.Width);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs eventArgs)
    {
        var dpiScale = GetDpiScale(eventArgs.Graphics);
        var y = eventArgs.Item.Height / 2;
        using var pen = new Pen(_isDark
            ? Color.FromArgb(67, 72, 81)
            : Color.FromArgb(224, 227, 231));
        var horizontalInset = 10f * dpiScale;
        eventArgs.Graphics.DrawLine(
            pen,
            horizontalInset,
            y,
            eventArgs.Item.Width - horizontalInset,
            y);
    }

    internal static float CalculateIconSize(float itemHeight, float dpiScale) =>
        Math.Min(16f * Math.Max(0.75f, dpiScale), itemHeight * 0.6f);

    internal static FluentMenuItemLayout CalculateItemLayout(
        Size itemSize,
        float dpiScale,
        bool hasArrow)
    {
        dpiScale = Math.Max(0.75f, dpiScale);
        var iconSize = CalculateIconSize(itemSize.Height, dpiScale);
        var iconBounds = new RectangleF(
            8f * dpiScale,
            (itemSize.Height - iconSize) / 2f,
            iconSize,
            iconSize);
        var arrowSize = 14f * dpiScale;
        var arrowBounds = hasArrow
            ? new RectangleF(
                itemSize.Width - 11f * dpiScale - arrowSize / 2f,
                (itemSize.Height - arrowSize) / 2f,
                arrowSize,
                arrowSize)
            : RectangleF.Empty;
        var textLeft = (int)Math.Ceiling(32f * dpiScale);
        var textRight = (int)Math.Floor(hasArrow
            ? arrowBounds.Left - 6f * dpiScale
            : itemSize.Width - 10f * dpiScale);
        var textBounds = new Rectangle(
            textLeft,
            0,
            Math.Max(0, textRight - textLeft),
            itemSize.Height);
        return new FluentMenuItemLayout(iconBounds, textBounds, arrowBounds);
    }

    internal static Region CreateRoundedRegion(Size size)
    {
        using var path = CreateRoundedPath(new Rectangle(0, 0, size.Width, size.Height), 9);
        return new Region(path);
    }

    internal static void ApplyRoundedCorners(ToolStripDropDown menu)
    {
        if (menu.Width <= 0 || menu.Height <= 0)
        {
            return;
        }
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            var preference = 2;
            if (DwmSetWindowAttribute(menu.Handle, 33, ref preference, sizeof(int)) == 0)
            {
                var previousRegion = menu.Region;
                menu.Region = null;
                previousRegion?.Dispose();
                return;
            }
        }
        var previous = menu.Region;
        menu.Region = CreateRoundedRegion(menu.Size);
        previous?.Dispose();
    }

    private static GraphicsPath CreateRoundedPath(Rectangle bounds, int radius)
    {
        var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        var arc = new Rectangle(bounds.Location, new Size(diameter, diameter));
        var path = new GraphicsPath();
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static float GetDpiScale(Graphics graphics) =>
        Math.Max(0.75f, graphics.DpiX / 96f);

    private Color GetCheckColor(bool enabled) => enabled
        ? _isDark ? Color.FromArgb(96, 205, 255) : Color.FromArgb(0, 95, 184)
        : _isDark ? Color.FromArgb(125, 130, 138) : Color.FromArgb(145, 150, 158);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr window,
        int attribute,
        ref int value,
        int valueSize);
}
