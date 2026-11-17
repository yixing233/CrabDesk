using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using CrabDesk.Core;
using Forms = System.Windows.Forms;

namespace CrabDesk.Runtime;

internal sealed partial class WidgetCenterForm
{
    private readonly Forms.Timer _reorderScrollTimer = new() { Interval = 30 };
    private DesktopWidget? _reorderWidget;
    private PointF _reorderPointer, _reorderGrab;
    private IReadOnlyList<Guid> _reorderOriginalOrder = Array.Empty<Guid>();
    private IReadOnlyList<Guid>? _reorderPreviewOrder;
    private Dictionary<Guid, LayoutRect> _reorderOriginalBounds = [];
    private Bitmap? _reorderGhost;

    protected override void UpdateMovingWidget(DesktopWidget widget, PointF point)
    {
        if (_reorderWidget is null)
        {
            DismissWidgetInputs();
            HideCalendarHover();
            _reorderWidget = widget;
            var bounds = ViewBounds(widget);
            _reorderGrab = new(_widgetPressPoint.X - (float)bounds.X, _widgetPressPoint.Y - (float)bounds.Y);
            _reorderOriginalOrder = _hostWidgets.Select(item => item.Id).ToArray();
            _reorderOriginalBounds = new(_viewBounds);
            _reorderGhost = new Bitmap(Math.Max(1, ToPixel(bounds.Width)), Math.Max(1, ToPixel(bounds.Height)), PixelFormat.Format32bppPArgb);
            using var graphics = Graphics.FromImage(_reorderGhost);
            graphics.ScaleTransform((float)_scale, (float)_scale);
            graphics.TranslateTransform(-(float)bounds.X, -(float)bounds.Y);
            DrawWidget(graphics, widget);
            _reorderScrollTimer.Start();
        }
        _reorderPointer = point;
        UpdateWidgetReorderPreview();
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
        if (_reorderWidget is null) { _reorderScrollTimer.Stop(); return; }
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
        var order = commit ? _reorderPreviewOrder : null;
        _reorderWidget = null;
        _reorderPreviewOrder = null;
        _reorderOriginalOrder = Array.Empty<Guid>();
        _reorderOriginalBounds.Clear();
        _reorderScrollTimer.Stop();
        _reorderGhost?.Dispose(); _reorderGhost = null;
        FinishWidgetPress(false);
        Cursor = Forms.Cursors.Default;
        if (order is not null) _runtime.ReorderWidgetCenter(order);
        RefreshWidgets();
    }

    private void DrawWidgetReorder(Graphics graphics)
    {
        if (_reorderWidget is null || _reorderGhost is null) return;
        var bounds = ViewBounds(_reorderWidget);
        using var path = RoundedRectangle(new((float)bounds.X, (float)bounds.Y, (float)bounds.Width, (float)bounds.Height), 8);
        using var wash = new SolidBrush(Color.FromArgb(18, ForeColor));
        using var edge = new Pen(Color.FromArgb(135, ForeColor), 1.5f) { DashStyle = DashStyle.Dash };
        graphics.FillPath(wash, path); graphics.DrawPath(edge, path);
        var ghost = new RectangleF(_reorderPointer.X - _reorderGrab.X, _reorderPointer.Y - _reorderGrab.Y, (float)bounds.Width, (float)bounds.Height);
        graphics.DrawImage(_reorderGhost, ghost, new RectangleF(0, 0, _reorderGhost.Width, _reorderGhost.Height), GraphicsUnit.Pixel);
    }
}
