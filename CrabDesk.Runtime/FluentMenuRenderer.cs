using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CrabDesk.Runtime;

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
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = CreateRoundedPath(bounds, 8);
        using var pen = new Pen(_border);
        eventArgs.Graphics.DrawPath(pen, path);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs eventArgs)
    {
        if (!eventArgs.Item.Selected && !eventArgs.Item.Pressed) return;
        var bounds = new Rectangle(
            4,
            1,
            Math.Max(
                eventArgs.Item.Width,
                eventArgs.ToolStrip?.DisplayRectangle.Width ?? eventArgs.Item.Width) - 8,
            eventArgs.Item.Height - 2);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = CreateRoundedPath(bounds, 5);
        using var brush = new SolidBrush(_selected);
        eventArgs.Graphics.FillPath(brush, path);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs eventArgs)
    {
        var textBounds = new Rectangle(
            eventArgs.TextRectangle.X,
            0,
            eventArgs.TextRectangle.Width,
            eventArgs.Item.Height);
        TextRenderer.DrawText(
            eventArgs.Graphics,
            eventArgs.Text,
            eventArgs.TextFont,
            textBounds,
            eventArgs.TextColor,
            TextFormatFlags.Left |
            TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine |
            TextFormatFlags.EndEllipsis |
            TextFormatFlags.NoPadding);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs eventArgs)
    {
        var checkArea = eventArgs.ImageRectangle;
        var centerX = checkArea.IsEmpty ? 14 : checkArea.Left + checkArea.Width / 2f;
        // ToolStrip keeps the image rectangle at its preferred-size position
        // after our menu metrics raise the row to 32 px. Center against the
        // actual row so the checkmark shares the same baseline as the text.
        var centerY = eventArgs.Item.Height / 2f;
        var color = eventArgs.Item.Enabled
            ? (_isDark ? Color.FromArgb(96, 205, 255) : Color.FromArgb(0, 95, 184))
            : (_isDark ? Color.FromArgb(125, 130, 138) : Color.FromArgb(145, 150, 158));
        using var pen = new Pen(color, 1.8f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        eventArgs.Graphics.DrawLines(pen,
        [
            new PointF(centerX - 4, centerY),
            new PointF(centerX - 1, centerY + 3),
            new PointF(centerX + 5, centerY - 4)
        ]);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs eventArgs)
    {
        var item = eventArgs.Item;
        var centerX = (item?.Width ?? eventArgs.ArrowRectangle.Right) - 10f;
        var centerY = (item?.Height ?? eventArgs.ArrowRectangle.Height) / 2f;
        var color = item?.Enabled != false
            ? (_isDark ? Color.FromArgb(224, 227, 232) : Color.FromArgb(61, 66, 73))
            : (_isDark ? Color.FromArgb(125, 130, 138) : Color.FromArgb(145, 150, 158));
        using var pen = new Pen(color, 1.5f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        eventArgs.Graphics.DrawLines(pen,
        [
            new PointF(centerX - 1.5f, centerY - 3),
            new PointF(centerX + 1.5f, centerY),
            new PointF(centerX - 1.5f, centerY + 3)
        ]);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs eventArgs)
    {
        var y = eventArgs.Item.Height / 2;
        using var pen = new Pen(_isDark
            ? Color.FromArgb(67, 72, 81)
            : Color.FromArgb(224, 227, 231));
        eventArgs.Graphics.DrawLine(pen, 12, y, eventArgs.Item.Width - 12, y);
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

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr window,
        int attribute,
        ref int value,
        int valueSize);
}
