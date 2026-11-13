using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using CrabDesk.Core;
using Forms = System.Windows.Forms;

namespace CrabDesk.Runtime;

internal sealed partial class WidgetCenterForm
{
    private const double ReorderGripHeight = 16;
    private readonly Forms.Timer _reorderScrollTimer = new() { Interval = 30 };
    private DesktopWidget? _reorderWidget;
    private Guid? _reorderHovered;
    private bool _reorderDragging;
    private PointF _reorderPress, _reorderPointer, _reorderGrab;
    private IReadOnlyList<Guid> _reorderOriginalOrder = Array.Empty<Guid>();
    private IReadOnlyList<Guid>? _reorderPreviewOrder;
    private Dictionary<Guid, LayoutRect> _reorderOriginalBounds = [];
    private Bitmap? _reorderGhost;

    internal static RectangleF ReorderGripBounds(LayoutRect bounds) =>
        new((float)(bounds.X + bounds.Width / 2 - 24), (float)(bounds.Y - ReorderGripHeight), 48, (float)ReorderGripHeight);

    private bool BeginWidgetReorder(Forms.MouseEventArgs args, PointF point)
    {
        if (args.Button != Forms.MouseButtons.Left || _pickerVisible || _acceptingDrop || _pressedWidget is not null) return false;
        var widget = _hostWidgets.FirstOrDefault(item => ReorderGripBounds(ViewBounds(item)).Contains(point));
        if (widget is null) return false;
        DismissWidgetInputs(); HideCalendarHover();
        _reorderWidget = widget;
        _reorderPress = _reorderPointer = point;
        var bounds = ViewBounds(widget);
        _reorderGrab = new(point.X - (float)bounds.X, point.Y - (float)bounds.Y);
        _reorderOriginalOrder = _hostWidgets.Select(item => item.Id).ToArray();
        _reorderOriginalBounds = new(_viewBounds);
        Capture = true;
        Cursor = Forms.Cursors.SizeAll;
        Invalidate();
        return true;
    }

    private bool MoveWidgetReorder(Forms.MouseEventArgs args, PointF point)
    {
        if (_reorderWidget is null)
        {
            var hovered = !_pickerVisible && args.Y >= ToPixel(HeaderHeight)
                ? _hostWidgets.FirstOrDefault(item => ReorderGripBounds(ViewBounds(item)).Contains(point))?.Id : null;
            if (hovered != _reorderHovered) { _reorderHovered = hovered; Invalidate(); }
            if (hovered is null) return false;
            Cursor = Forms.Cursors.SizeAll;
            return true;
        }
        _reorderPointer = point;
        if (!_reorderDragging && Math.Max(Math.Abs(point.X - _reorderPress.X), Math.Abs(point.Y - _reorderPress.Y)) < 4) return true;
        if (!_reorderDragging)
        {
            _reorderDragging = true;
            var bounds = ViewBounds(_reorderWidget);
            _reorderGhost = new Bitmap(Math.Max(1, ToPixel(bounds.Width)), Math.Max(1, ToPixel(bounds.Height)), PixelFormat.Format32bppPArgb);
            using var graphics = Graphics.FromImage(_reorderGhost);
            graphics.ScaleTransform((float)_scale, (float)_scale);
            graphics.TranslateTransform(-(float)bounds.X, -(float)bounds.Y);
            DrawWidget(graphics, _reorderWidget);
            _reorderScrollTimer.Tick -= ScrollWidgetReorder;
            _reorderScrollTimer.Tick += ScrollWidgetReorder;
            _reorderScrollTimer.Start();
        }
        UpdateWidgetReorderPreview();
        return true;
    }

    private void UpdateWidgetReorderPreview()
    {
        if (_reorderWidget is null) return;
        var index = WidgetCenterReordering.TargetIndex(_reorderOriginalOrder, _reorderOriginalBounds, _reorderPointer.X, _reorderPointer.Y);
        var order = WidgetCenterReordering.Move(_reorderOriginalOrder, _reorderWidget.Id, index);
        if (_reorderPreviewOrder is null || !_reorderPreviewOrder.SequenceEqual(order))
        {
            _reorderPreviewOrder = order;
            RefreshWidgets();
        }
        Invalidate();
    }

    private void ScrollWidgetReorder(object? sender, EventArgs args)
    {
        if (!_reorderDragging || _reorderWidget is null) { _reorderScrollTimer.Stop(); return; }
        var y = _reorderPointer.Y - _scrollOffset;
        var bottom = ClientSize.Height / _scale;
        var step = y < HeaderHeight + 32 ? -10 : y > bottom - 32 ? 10 : 0;
        if (step == 0) return;
        var previous = _scrollOffset;
        SetScrollOffset(_scrollOffset + step);
        _reorderPointer.Y += (float)(_scrollOffset - previous);
        UpdateWidgetReorderPreview();
    }

    private void FinishWidgetReorder(bool commit)
    {
        if (_reorderWidget is null) return;
        var order = commit && _reorderDragging ? _reorderPreviewOrder : null;
        _reorderWidget = null;
        _reorderDragging = false;
        _reorderPreviewOrder = null;
        _reorderOriginalOrder = Array.Empty<Guid>();
        _reorderOriginalBounds.Clear();
        _reorderScrollTimer.Stop();
        _reorderGhost?.Dispose(); _reorderGhost = null;
        Capture = false;
        Cursor = Forms.Cursors.Default;
        if (order is not null) _runtime.ReorderWidgetCenter(order);
        RefreshWidgets();
    }

    private void DrawWidgetReorder(Graphics graphics)
    {
        foreach (var widget in _hostWidgets)
        {
            var grip = ReorderGripBounds(ViewBounds(widget));
            var active = _reorderHovered == widget.Id || _reorderWidget == widget;
            using var dots = new SolidBrush(Color.FromArgb(active ? 170 : 64, ForeColor));
            for (var row = 0; row < 2; row++)
            for (var column = 0; column < 3; column++)
                graphics.FillEllipse(dots, grip.X + 18 + column * 5, grip.Y + 4 + row * 5, 2, 2);
        }
        if (!_reorderDragging || _reorderWidget is null || _reorderGhost is null) return;
        var bounds = ViewBounds(_reorderWidget);
        using var path = RoundedRectangle(new((float)bounds.X, (float)bounds.Y, (float)bounds.Width, (float)bounds.Height), 8);
        using var wash = new SolidBrush(Color.FromArgb(18, ForeColor));
        using var edge = new Pen(Color.FromArgb(135, ForeColor), 1.5f) { DashStyle = DashStyle.Dash };
        graphics.FillPath(wash, path); graphics.DrawPath(edge, path);
        var ghost = new RectangleF(_reorderPointer.X - _reorderGrab.X, _reorderPointer.Y - _reorderGrab.Y, (float)bounds.Width, (float)bounds.Height);
        graphics.DrawImage(_reorderGhost, ghost, new RectangleF(0, 0, _reorderGhost.Width, _reorderGhost.Height), GraphicsUnit.Pixel);
    }
}
