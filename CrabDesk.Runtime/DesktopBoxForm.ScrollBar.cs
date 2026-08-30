using System.Drawing;
using CrabDesk.Core;
using Forms = System.Windows.Forms;

namespace CrabDesk.Runtime;

internal sealed partial class DesktopBoxForm
{
    private ScrollBarDragState? _scrollBarDrag;

    internal static LayoutRect CalculateScrollBarViewport(
        RectangleF boxBounds,
        RectangleF bodyBounds) =>
        new(
            bodyBounds.X,
            bodyBounds.Y,
            Math.Max(0, boxBounds.Right - bodyBounds.X),
            Math.Max(0, bodyBounds.Height));

    private VerticalScrollBarLayout? GetScrollBarLayout(BoxGeometry geometry)
    {
        if (geometry.IsCollapsed ||
            _runtime.AreDesktopItemsHidden ||
            !_runtime.State.Settings.Appearance.ShowBoxScrollBar)
        {
            return null;
        }

        var appearance = _runtime.State.Settings.Appearance;
        var body = new LayoutRect(
            geometry.Body.X,
            geometry.Body.Y,
            geometry.Body.Width,
            geometry.Body.Height);
        // Keep icon layout based on the actual body, but let the scrollbar use
        // the box's right-side padding so it sits beside the icons instead of
        // overlapping their last column.
        var scrollbarViewport = CalculateScrollBarViewport(
            geometry.Bounds,
            geometry.Body);
        var maxScroll = DesktopItemLayoutEngine.GetScrollExtent(
            geometry.Box.ViewMode,
            body,
            GetVisibleItemsForBox(geometry).Count,
            geometry.Box.Appearance.IconSize,
            DesktopItemLayoutEngine.ScaleIconSpacing(
                appearance.IconHorizontalSpacing,
                geometry.Box.Appearance.IconSize),
            DesktopItemLayoutEngine.ScaleIconSpacing(
                appearance.IconVerticalSpacing,
                geometry.Box.Appearance.IconSize));
        return DesktopScrollBarLayoutEngine.CalculateVertical(
            scrollbarViewport,
            _scrollOffsets.GetValueOrDefault(GetItemViewKey(geometry)),
            maxScroll);
    }

    private bool TryBeginScrollBarDrag(BoxGeometry geometry, PointF point)
    {
        var layout = GetScrollBarLayout(geometry);
        if (layout is null || !layout.Track.Contains(point.X, point.Y))
        {
            return false;
        }

        CancelScrollAnimationForDirectManipulation();
        ClearItemHover();
        var thumbGrabOffset = layout.Thumb.Contains(point.X, point.Y)
            ? point.Y - layout.Thumb.Y
            : layout.Thumb.Height / 2;
        _scrollBarDrag = new ScrollBarDragState(
            geometry.Box.Id,
            GetItemViewKey(geometry),
            thumbGrabOffset);
        ApplyScrollOffset(
            _scrollBarDrag.ViewKey,
            DesktopScrollBarLayoutEngine.GetScrollOffsetForThumbTop(
                layout,
                point.Y - thumbGrabOffset));
        Capture = true;
        return true;
    }

    private void UpdateScrollBarDrag(PointF point)
    {
        if (_scrollBarDrag is not { } drag)
        {
            return;
        }

        var geometry = _boxes.LastOrDefault(candidate => candidate.Box.Id == drag.BoxId);
        var layout = geometry is null ||
                     GetItemViewKey(geometry) != drag.ViewKey
            ? null
            : GetScrollBarLayout(geometry);
        if (layout is null)
        {
            FinishScrollBarDrag();
            return;
        }

        ApplyScrollOffset(
            drag.ViewKey,
            DesktopScrollBarLayoutEngine.GetScrollOffsetForThumbTop(
                layout,
                point.Y - drag.ThumbGrabOffset));
    }

    private void FinishScrollBarDrag()
    {
        if (_scrollBarDrag is null)
        {
            return;
        }

        _scrollBarDrag = null;
        if (Capture)
        {
            Capture = false;
        }
        QueueHoverReconcile();
    }

    private void CancelScrollAnimationForDirectManipulation()
    {
        if (_scrollAnimationKey is null)
        {
            return;
        }

        _scrollAnimationKey = null;
        _scrollHoverResumeTimer.Stop();
        _dynamicVisualVersion++;
        _animationFrameClock.StopWhenIdle(_heightAnimations.Count > 0);
    }

    private void DrawVerticalScrollBar(
        Graphics graphics,
        BoxGeometry geometry,
        Color accent,
        Color textColor)
    {
        var layout = GetScrollBarLayout(geometry);
        if (layout is null)
        {
            return;
        }

        var trackBounds = ToRectangleF(layout.Track);
        var thumbBounds = ToRectangleF(layout.Thumb);
        var isDragging = _scrollBarDrag?.BoxId == geometry.Box.Id;
        using var trackFill = new SolidBrush(Color.FromArgb(48, textColor));
        using var thumbFill = new SolidBrush(Color.FromArgb(
            isDragging ? 220 : 160,
            isDragging ? accent : textColor));
        using var trackPath = RoundedRectangle(trackBounds, trackBounds.Width / 2);
        using var thumbPath = RoundedRectangle(thumbBounds, thumbBounds.Width / 2);
        graphics.FillPath(trackFill, trackPath);
        graphics.FillPath(thumbFill, thumbPath);
    }

    private static RectangleF ToRectangleF(LayoutRect bounds) => new(
        (float)bounds.X,
        (float)bounds.Y,
        (float)bounds.Width,
        (float)bounds.Height);

    private sealed record ScrollBarDragState(
        Guid BoxId,
        ItemViewKey ViewKey,
        double ThumbGrabOffset);
}
