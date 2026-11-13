using System.Drawing;
using Forms = System.Windows.Forms;

namespace CrabDesk.Runtime;

internal sealed partial class WidgetCenterForm
{
    private PointF ChromePoint(Point point) => new((float)(point.X / _scale), (float)(point.Y / _scale));
    private RectangleF ChromeScrollTrack => WidgetCenterChrome.ScrollTrack((float)(ClientSize.Width / _scale), (float)(ClientSize.Height / _scale));
    private double ChromeViewport => Math.Max(1, ClientSize.Height / _scale - HeaderHeight);

    private bool HandleChromeMouseDown(Forms.MouseEventArgs args)
    {
        if (args.Button != Forms.MouseButtons.Left) return false;
        var point = ChromePoint(args.Location);
        var width = (float)(ClientSize.Width / _scale);
        _addPressed = WidgetCenterChrome.AddBounds(width).Contains(point);
        if (_addPressed)
        {
            _headerFocus = 0;
            Focus(); Capture = true; Invalidate();
            return true;
        }
        if (ScrollContentHeight <= ChromeViewport || !ChromeScrollTrack.Contains(point)) return false;
        var thumb = WidgetCenterChrome.ScrollThumb(ChromeScrollTrack, ChromeViewport, ScrollContentHeight, _scrollOffset, true);
        if (!thumb.Contains(point))
            SetScrollOffset(_scrollOffset + (point.Y < thumb.Top ? -ChromeViewport : ChromeViewport));
        _scrollDragging = true;
        _scrollDragY = point.Y;
        _scrollDragStart = _scrollOffset;
        Capture = true; Invalidate();
        return true;
    }

    private bool HandleChromeMouseMove(Forms.MouseEventArgs args)
    {
        var point = ChromePoint(args.Location);
        if (_scrollDragging)
        {
            var thumb = WidgetCenterChrome.ScrollThumb(ChromeScrollTrack, ChromeViewport, ScrollContentHeight, _scrollOffset, true);
            var travel = Math.Max(1, ChromeScrollTrack.Height - thumb.Height);
            SetScrollOffset(_scrollDragStart + (point.Y - _scrollDragY) / travel * Math.Max(0, ScrollContentHeight - ChromeViewport));
            return true;
        }
        var width = (float)(ClientSize.Width / _scale);
        var add = WidgetCenterChrome.AddBounds(width).Contains(point);
        var scroll = ScrollContentHeight > ChromeViewport && ChromeScrollTrack.Contains(point);
        if (add != _addHovered || scroll != _scrollHovered)
        { _addHovered = add; _scrollHovered = scroll; Invalidate(); }
        if (!add && !scroll && !_addPressed) return false;
        Cursor = scroll ? Forms.Cursors.Default : Forms.Cursors.Hand;
        return true;
    }

    private bool HandleChromeMouseUp(Forms.MouseEventArgs args)
    {
        if (!_addPressed && !_scrollDragging) return false;
        var point = ChromePoint(args.Location);
        var width = (float)(ClientSize.Width / _scale);
        var add = _addPressed && WidgetCenterChrome.AddBounds(width).Contains(point);
        _addPressed = _scrollDragging = false;
        Capture = false;
        if (add) TogglePicker();
        Invalidate();
        return true;
    }

    private bool HandleChromeKey(Forms.Keys keys)
    {
        if (keys == Forms.Keys.Tab || keys == (Forms.Keys.Shift | Forms.Keys.Tab))
        {
            _headerFocus = 0;
            Invalidate(); return true;
        }
        if ((keys is Forms.Keys.Enter or Forms.Keys.Space) && _headerFocus >= 0)
        {
            TogglePicker();
            return true;
        }
        if (keys is Forms.Keys.PageDown or Forms.Keys.PageUp)
        { SetScrollOffset(_scrollOffset + (keys == Forms.Keys.PageDown ? ChromeViewport : -ChromeViewport)); return true; }
        return false;
    }
}
