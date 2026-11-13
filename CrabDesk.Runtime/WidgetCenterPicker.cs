using System.Drawing;
using System.Drawing.Drawing2D;
using CrabDesk.Core;
using Forms = System.Windows.Forms;

namespace CrabDesk.Runtime;

internal enum WidgetCenterPickerStatus { NotAdded, MoveToCenter, Added }
internal readonly record struct WidgetCenterPickerItem(DesktopWidgetKind Kind, DesktopWidget? Widget, WidgetCenterPickerStatus Status)
{
    internal string ActionLabel => Status switch { WidgetCenterPickerStatus.Added => "已添加", WidgetCenterPickerStatus.MoveToCenter => "移入中心", _ => "未添加 · 添加" };
}

/// <summary>An in-window picker; it acts on the existing one-per-kind model.</summary>
internal sealed class WidgetCenterPicker : Forms.Control
{
    private readonly CrabDeskRuntime _runtime;
    private readonly WidgetCenterChrome _chrome = new();
    private double _scale;
    private int _hover = -1;
    internal event Action? Selected;
    internal WidgetCenterPicker(CrabDeskRuntime runtime, double scale)
    {
        _runtime = runtime; _scale = scale;
        DoubleBuffered = true;
        AccessibleName = "添加组件";
        TabStop = true;
    }
    internal static IReadOnlyList<WidgetCenterPickerItem> GetItems(IEnumerable<DesktopWidget> widgets)
    {
        var existing = widgets.ToArray();
        return Enum.GetValues<DesktopWidgetKind>().Select(kind =>
        {
            var widget = existing.FirstOrDefault(item => item.Kind == kind);
            return new WidgetCenterPickerItem(kind, widget, widget is null ? WidgetCenterPickerStatus.NotAdded :
                widget.Placement == WidgetPlacement.Center ? WidgetCenterPickerStatus.Added : WidgetCenterPickerStatus.MoveToCenter);
        }).ToArray();
    }
    internal void UpdateScale(double scale) { _scale = Math.Max(.1, scale); Invalidate(); }
    internal int ContentHeight => (int)Math.Ceiling(GetItems(_runtime.State.Widgets).Count * 58 * _scale + 8 * _scale);
    private int RowAt(Point point) => (int)(point.Y / (58 * _scale));
    protected override void OnMouseMove(Forms.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var row = RowAt(e.Location);
        if (_hover != row) { _hover = row; Invalidate(); }
        Cursor = row < GetItems(_runtime.State.Widgets).Count && GetItems(_runtime.State.Widgets)[row].Status != WidgetCenterPickerStatus.Added
            ? Forms.Cursors.Hand : Forms.Cursors.Default;
    }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = -1; Invalidate(); }
    protected override void OnMouseClick(Forms.MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != Forms.MouseButtons.Left) return;
        Choose(RowAt(e.Location));
    }
    protected override bool ProcessCmdKey(ref Forms.Message msg, Forms.Keys keyData)
    {
        var count = GetItems(_runtime.State.Widgets).Count;
        if (keyData is Forms.Keys.Down or Forms.Keys.Up)
        { _hover = (_hover + (keyData == Forms.Keys.Down ? 1 : count - 1) + count) % count; Invalidate(); return true; }
        if (keyData is Forms.Keys.Enter or Forms.Keys.Space) { Choose(Math.Max(0, _hover)); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
    private void Choose(int row)
    {
        var items = GetItems(_runtime.State.Widgets);
        if (row < 0 || row >= items.Count || items[row].Status == WidgetCenterPickerStatus.Added) return;
        var item = items[row];
        if (item.Widget is { } widget) _runtime.MoveWidgetToCenter(widget);
        else _runtime.AddWidgetToCenter(item.Kind);
        Selected?.Invoke(); Invalidate();
    }
    protected override void OnPaint(Forms.PaintEventArgs e) => Draw(e.Graphics);

    internal void Draw(Graphics graphics)
    {
        using var background = new SolidBrush(BackColor);
        graphics.FillRectangle(background, 0, 0, Width, Height);
        graphics.ScaleTransform((float)_scale, (float)_scale);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var width = (float)(Width / _scale);
        var items = GetItems(_runtime.State.Widgets);
        for (var row = 0; row < items.Count; row++)
        {
            var item = items[row]; var y = row * 58;
            using var shape = RoundedRow(new(0, y, width, 54));
            using var fill = new SolidBrush(Color.FromArgb(_hover == row ? 24 : 10, ForeColor));
            graphics.FillPath(fill, shape);
            WidgetColorIcons.Draw(graphics, WidgetIconCatalog.GetName(item.Kind), new(12, y + 11, 32, 32), item.Status == WidgetCenterPickerStatus.Added ? .6f : 1);
            _chrome.DrawText(graphics, DesktopWidgets.GetName(item.Kind), new(56, y + 6, width - 176, 24), ForeColor, 14, semibold: true);
            _chrome.DrawText(graphics, item.Status == WidgetCenterPickerStatus.MoveToCenter ? "保留已有内容与桌面位置" : "同一种组件只添加一个",
                new(56, y + 29, width - 176, 19), Color.FromArgb(165, ForeColor), 11);
            _chrome.DrawText(graphics, item.ActionLabel, new(width - 116, y + 9, 108, 34),
                Color.FromArgb(item.Status == WidgetCenterPickerStatus.Added ? 130 : 230, ForeColor), 12, centered: true);
        }
    }

    private static GraphicsPath RoundedRow(RectangleF bounds)
    {
        const float diameter = 16;
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _chrome.Dispose();
        base.Dispose(disposing);
    }
}
