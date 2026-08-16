using System.Collections.Specialized;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices.ComTypes;
using CrabDesk.Core;
using CrabDesk.Native;
using Forms = System.Windows.Forms;
using FormsIntegration = System.Windows.Forms.Integration;
using Wpf = System.Windows;
using WpfControls = System.Windows.Controls;
using WpfInput = System.Windows.Input;
using WpfMedia = System.Windows.Media;

namespace CrabDesk.Runtime;

internal sealed partial class DesktopBoxForm : Forms.Form
{

    private void InvalidateItem(ItemGeometry? item)
    {
        if (item is not null)
        {
            RequestItemHoverVisualUpdate();
        }
    }

    private void InvalidateDip(RectangleF bounds)
    {
        // UpdateLayeredWindow replaces the complete surface bitmap. Retaining
        // the old partial Invalidate path lets the native form paint between
        // a region update and the next layered present, which visibly strips
        // the header and tabs while a box is dragged.
        RequestLayerRender();
    }

    private void RequestLayerRender()
    {
        if (_resourcesDisposed || IsDisposed || !IsHandleCreated)
        {
            return;
        }

        PresentLayer();
    }

    private void RequestVisualLayerRender()
    {
        if (_resourcesDisposed || IsDisposed || !IsHandleCreated)
        {
            return;
        }

        // In desktop composition mode the box window is only an invisible
        // hit mask. Item hover is painted by the shared icon layer, so
        // updating this window first creates a visible two-window transition.
        if (_isCompositedByIconSurface && _iconLayerRenderRequest is not null)
        {
            _iconLayerRenderRequest();
            return;
        }

        PresentLayer();
    }

    private void RequestItemHoverVisualUpdate()
    {
        if (_isCompositedByIconSurface && !_itemHoverOverlayUnavailable)
        {
            if (PresentItemHoverOverlay())
            {
                return;
            }
        }

        RequestVisualLayerRender();
    }

    private bool PresentItemHoverOverlay()
    {
        if (_resourcesDisposed ||
            !_isCompositedByIconSurface ||
            HasDynamicVisual ||
            !_runtime.State.Settings.Appearance.HoverFeedback ||
            _hoveredItemKey is null)
        {
            HideItemHoverOverlay();
            return true;
        }

        var item = FindHoveredItem();
        var geometry = item is null
            ? null
            : _boxes.LastOrDefault(box => box.Box.Id == item.Box.Id);
        if (item is null || geometry is null)
        {
            HideItemHoverOverlay();
            return true;
        }

        EnsureHitMaskBitmap();
        RectangleF currentBounds;
        using (var measureGraphics = Graphics.FromImage(_hitMaskBitmap!))
        {
            measureGraphics.ScaleTransform((float)_scale, (float)_scale);
            currentBounds = GetItemHoverVisualBounds(measureGraphics, item, geometry.Body);
            measureGraphics.ResetTransform();
        }

        var surfaceBounds = new RectangleF(
            0,
            0,
            (float)(ClientSize.Width / Math.Max(_scale, 0.01d)),
            (float)(ClientSize.Height / Math.Max(_scale, 0.01d)));
        currentBounds = RectangleF.Intersect(
            surfaceBounds,
            RectangleF.Inflate(currentBounds, 4, 4));
        if (currentBounds.Width <= 0 || currentBounds.Height <= 0)
        {
            HideItemHoverOverlay();
            return true;
        }

        var requestedBounds = _lastItemHoverOverlayBounds is { } previousBounds
            ? RectangleF.Union(previousBounds, currentBounds)
            : currentBounds;
        if (!_itemHoverOverlay.Present(
                requestedBounds,
                _scale,
                DrawItemHoverOverlay,
                out var diagnostic))
        {
            HideItemHoverOverlay();
            _itemHoverOverlayUnavailable = true;
            DiagnosticLog.Error(
                $"Desktop box item hover overlay presentation failed monitor={_monitor.Id}: {diagnostic}",
                new InvalidOperationException(diagnostic));
            return false;
        }

        _lastItemHoverOverlayBounds = currentBounds;
        return true;
    }

    private ItemGeometry? FindHoveredItem() =>
        _hoveredItemKey is null
            ? null
            : _items.LastOrDefault(item => string.Equals(
                item.Item.Key.ToString(),
                _hoveredItemKey,
                StringComparison.OrdinalIgnoreCase));

    private void HideItemHoverOverlay()
    {
        _itemHoverOverlay.HideOverlay();
        _lastItemHoverOverlayBounds = null;
    }

    private void RequestDragRender()
    {
        if (_resourcesDisposed || IsDisposed || !IsHandleCreated)
        {
            return;
        }

        HideItemHoverOverlay();

        if (_isCompositedByIconSurface && _iconLayerRenderRequest is not null)
        {
            // The shared icon layer owns the small drag overlay and already
            // coalesces frames. A second 16 ms timer here adds a full extra
            // frame of input latency to every box movement.
            _iconLayerRenderRequest();
            return;
        }

        // Mouse handlers only publish the latest pointer state. Queue the
        // expensive layered update so pointer messages remain responsive even
        // when the previous frame took longer than the coalesce interval.
        var now = DateTime.UtcNow;
        if (_dragRenderPending)
        {
            return;
        }

        _dragRenderPending = true;
        var elapsedMilliseconds = (now - _lastDragRenderUtc).TotalMilliseconds;
        if (elapsedMilliseconds >= DragRenderCoalesceMilliseconds)
        {
            QueueDragRender();
            return;
        }

        _dragRenderTimer.Interval = Math.Max(
            1,
            DragRenderCoalesceMilliseconds - (int)Math.Floor(elapsedMilliseconds));
        _dragRenderTimer.Start();
    }

    private void QueueDragRender()
    {
        try
        {
            BeginInvoke((Action)RenderQueuedDragFrame);
        }
        catch (InvalidOperationException)
        {
            _dragRenderPending = false;
        }
    }

    private void OnDragRenderTimerTick(object? sender, EventArgs eventArgs)
    {
        _dragRenderTimer.Stop();
        QueueDragRender();
    }

    private void RenderQueuedDragFrame()
    {
        if (!_dragRenderPending || _resourcesDisposed || IsDisposed)
        {
            return;
        }

        _dragRenderPending = false;
        _lastDragRenderUtc = DateTime.UtcNow;
        RenderPendingDragFrame();
    }

    private void RenderPendingDragFrame()
    {
        if (_movingBox is not null || _resizingBox is not null)
        {
            if (_isCompositedByIconSurface && _iconLayerRenderRequest is not null)
            {
                // Mouse capture keeps this surface receiving the drag even while
                // its old hit mask remains installed. Rebuild that full-monitor
                // mask and native region only once the transform is committed.
                _iconLayerRenderRequest();
                return;
            }
            UpdateWindowRegion();
            PresentLayer();
            return;
        }
        if (_isCompositedByIconSurface && _iconLayerRenderRequest is not null)
        {
            // Preview and selection frames never change box bounds, so the
            // box layer hit-mask does not need another full-screen
            // UpdateLayeredWindow pass. Redraw the shared icon layer directly.
            _iconLayerRenderRequest();
            return;
        }
        PresentLayer();
    }

    private void CancelPendingDragRender()
    {
        _dragRenderTimer.Stop();
        _dragRenderPending = false;
    }

}

