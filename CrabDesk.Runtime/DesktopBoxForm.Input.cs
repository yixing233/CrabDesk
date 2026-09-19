using System.Collections.Specialized;
using System.Diagnostics;
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

    private void OnMouseDown(object? sender, Forms.MouseEventArgs eventArgs)
    {
        // A click on the box surface while an inline rename is open commits
        // the edit (this window never activates, so Deactivate does not fire).
        _runtime.CommitActiveDesktopInlineRename();
        if (_editingBox is not null)
        {
            FinishTitleEdit(true);
        }
        RebuildGeometry();
        var point = ToDip(eventArgs.Location);
        var box = _boxes.LastOrDefault(candidate => candidate.Bounds.Contains(point));
        if (eventArgs.Button == Forms.MouseButtons.Left &&
            box is not null &&
            TryBeginScrollBarDrag(box, point))
        {
            return;
        }
        var item = GetItemAtPoint(box, point);
        if (item is not null)
        {
            // Avoid SetForegroundWindow while Explorer may still be completing a
            // shell drop; activation can block both Explorer and the desktop UI.
            TryBeginSlowDoubleClickRename(item);
        }
        DiagnosticLog.Info(
            $"Surface mouse down monitor={_monitor.Id} button={eventArgs.Button} x={point.X:0} y={point.Y:0} box={box?.Box.Id} itemKind={item?.Item.Key.Kind}");
        if (eventArgs.Button == Forms.MouseButtons.Right)
        {
            if (item is not null)
            {
                var itemKey = item.Item.Key.ToString();
                var contextTargetSelected = _selection.Contains(itemKey);
                _runtime.PrepareDesktopSelection(
                    this,
                    DesktopSelectionPolicy.PreserveExistingSelection(
                        DesktopSelectionGesture.ContextItem,
                        additive: false,
                        contextTargetSelected));
                if (!contextTargetSelected)
                {
                    _selection.Clear();
                    _selection.Add(itemKey);
                }
                ShowItemContextMenu(item.Box, item.Item, eventArgs.Location);
            }
            else if (box is not null &&
                     !box.Box.IsMappedFolder &&
                     GetManualBoxTabAtPoint(box, point) is { } manualTabHit)
            {
                var tab = manualTabHit.Id is { } tabId
                    ? box.Box.ManualTabs.FirstOrDefault(candidate => candidate.Id == tabId)
                    : null;
                BuildManualTabContextMenu(box.Box, tab).Show(this, eventArgs.Location);
            }
            else if (box is not null)
            {
                ShowBoxMenu(box.Box, eventArgs.Location);
            }
            return;
        }
        if (eventArgs.Button != Forms.MouseButtons.Left)
        {
            return;
        }
        _pressPoint = point;
        _dragStarted = false;
        _resizeEdges = ResizeEdges.None;
        if (box is not null && TrySelectBoxTab(box, point))
        {
            return;
        }
        if (item is not null)
        {
            var key = item.Item.Key.ToString();
            var controlPressed = (DesktopWindowTools.GetAsyncModifierKeys() & Forms.Keys.Control) != 0;
            var shiftPressed = (DesktopWindowTools.GetAsyncModifierKeys() & Forms.Keys.Shift) != 0;
            var targetAlreadySelected = _selection.Contains(key);
            if (shiftPressed &&
                _selectionAnchorBoxId == item.Box.Id &&
                TryApplyRangeSelection(item, key, controlPressed, targetAlreadySelected))
            {
                return;
            }

            _runtime.PrepareDesktopSelection(
                this,
                DesktopSelectionPolicy.PreserveExistingSelection(
                    DesktopSelectionGesture.PrimaryItem,
                    controlPressed,
                    targetAlreadySelected));
            // Every press that is not a range extension becomes the next anchor,
            // Ctrl+click included, matching how the shell moves focus.
            _selectionAnchorKey = key;
            _selectionAnchorBoxId = item.Box.Id;
            if (controlPressed && targetAlreadySelected)
            {
                _selection.Remove(key);
                _pressedItem = null;
                _pressedBoxId = null;
                Invalidate();
                RequestItemHoverVisualUpdate();
                return;
            }
            // Keep an existing multi-selection when pressing one of its items
            // so dragging starts from the whole selection. Only a plain press
            // on an unselected item resets the selection.
            if (!controlPressed && !targetAlreadySelected)
            {
                _selection.Clear();
            }
            _selection.Add(key);
            _pressedItem = item.Item;
            _pressedBoxId = item.Box.Id;
            Invalidate();
            RequestItemHoverVisualUpdate();
            return;
        }
        if (box is null)
        {
            return;
        }
        _startBounds = box.Box.Bounds;
        _monitorTransferSeen = false;
        _monitorTransferPreviousMonitorId = null;
        _monitorTransferLastPreviousMonitorId = null;
        if (box.Search.Contains(point))
        {
            ToggleBoxSearch(box.Box);
            return;
        }
        if (box.AutoExpand.Contains(point))
        {
            ToggleBoxDisplayMode(box.Box);
            return;
        }
        if (box.Menu.Contains(point))
        {
            ShowBoxMenu(box.Box, eventArgs.Location);
            return;
        }
        var resizeEdges = GetResizeEdges(box, point);
        if (_runtime.State.Settings.Appearance.ShowResizeGrip &&
            !box.IsCollapsed && resizeEdges != ResizeEdges.None)
        {
            PrepareBoxTransform(box.Box);
            _resizingBox = box.Box;
            _resizeEdges = resizeEdges;
        }
        else if (box.Header.Contains(point))
        {
            FinishTitleEdit(true);
            PrepareBoxTransform(box.Box);
            _movingBox = box.Box;
            PrepareMovingBoxVisualCache(box.Box);
        }
        else if (box.Body.Contains(point))
        {
            // Shift behaves like Ctrl for a rubber band: an empty-space drag
            // must not throw away the range the user just built with Shift.
            var additive = (DesktopWindowTools.GetAsyncModifierKeys() &
                (Forms.Keys.Control | Forms.Keys.Shift)) != 0;
            _runtime.PrepareDesktopSelection(
                this,
                DesktopSelectionPolicy.PreserveExistingSelection(
                    DesktopSelectionGesture.Marquee,
                    additive,
                    targetAlreadySelected: false));
            _selectionBox = box.Box;
            _selectionGeometry = box;
            _selectionStart = point;
            _selectionRectangle = RectangleF.Empty;
            _selectionBase.Clear();
            _marqueeSelectionItems.Clear();
            _marqueeSelectionKeys.Clear();
            if (additive)
            {
                _selectionBase.UnionWith(_selection);
            }
            else
            {
                _selection.Clear();
                _selectionAnchorKey = null;
                _selectionAnchorBoxId = null;
            }
            _dynamicVisualVersion++;
            // Establish the baseline before the first pointer move. The
            // dynamic overlay then owns only the marquee and newly selected
            // items instead of re-rendering the complete box.
            RequestDragRender();
        }
        if (_movingBox is not null || _resizingBox is not null)
        {
            _dynamicVisualVersion++;
            // Transfer the unchanged box to the icon surface's drag overlay
            // before the first pointer move. This prevents the first moving
            // frame from having to remove the settled box and show the overlay
            // in separate compositor updates.
            RequestDragRender();
        }
        Capture = _movingBox is not null || _resizingBox is not null || _selectionBox is not null;
    }

    /// <summary>
    /// Extends the selection from the anchor to <paramref name="item"/> inside
    /// one box. Returns false when the anchor is no longer laid out — filtered
    /// away by a search, scrolled out of the body, or on another tab — so the
    /// caller falls back to treating the press as a plain click.
    /// </summary>
    private bool TryApplyRangeSelection(
        ItemGeometry item,
        string key,
        bool controlPressed,
        bool targetAlreadySelected)
    {
        // _items already holds each box's visible items in layout order, so the
        // list order is the reading order the user sees.
        var orderedKeys = _items
            .Where(candidate => candidate.Box.Id == item.Box.Id)
            .Select(candidate => candidate.Item.Key.ToString())
            .ToArray();
        var range = DesktopSelectionPolicy.BuildRangeSelectionKeys(
            orderedKeys,
            _selectionAnchorKey,
            key);
        if (range.Count == 0)
        {
            return false;
        }

        _runtime.PrepareDesktopSelection(
            this,
            DesktopSelectionPolicy.PreserveExistingSelection(
                DesktopSelectionGesture.RangeItem,
                controlPressed,
                targetAlreadySelected));
        if (!controlPressed)
        {
            _selection.Clear();
        }
        foreach (var rangeKey in range)
        {
            _selection.Add(rangeKey);
        }
        _pressedItem = item.Item;
        _pressedBoxId = item.Box.Id;
        DiagnosticLog.Verbose(
            $"Box range selection box={item.Box.Id} span={range.Count} " +
            $"extend={controlPressed} selected={_selection.Count}");
        Invalidate();
        RequestItemHoverVisualUpdate();
        return true;
    }

    private void OnMouseMove(object? sender, Forms.MouseEventArgs eventArgs)
    {
        var point = ToDip(eventArgs.Location);
        if (_scrollBarDrag is not null)
        {
            UpdateScrollBarDrag(point);
            return;
        }
        if (_movingBox is not null)
        {
            UpdateMovingBox(_movingBox, point);
            return;
        }
        if (_resizingBox is not null)
        {
            UpdateResizingBox(_resizingBox, point);
            return;
        }
        if (_selectionBox is not null)
        {
            UpdateSelectionFromPoint(point, requestRender: true);
            return;
        }
        UpdatePointerCursor(point);
        UpdateHoverState(point);
        if (_pressedItem is null || eventArgs.Button != Forms.MouseButtons.Left || _dragStarted)
        {
            return;
        }
        if (Math.Abs(point.X - _pressPoint.X) < 4 && Math.Abs(point.Y - _pressPoint.Y) < 4)
        {
            return;
        }
        _pendingRenameItem = null;
        _pendingRenameBoxId = null;
        _dragStarted = true;
        _dynamicVisualVersion++;
        Invalidate();
        if (_pressedBoxId is not { } sourceBoxId)
        {
            return;
        }
        var selected = GetCachedItemsForBox(sourceBoxId)
            .Where(candidate => _selection.Contains(candidate.Key.ToString()))
            .ToArray();
        if (selected.Length == 0)
        {
            _dragStarted = false;
            return;
        }
        var sourceGeometry = _pressedItem is null
            ? null
            : _items.LastOrDefault(item => item.Item.Key == _pressedItem.Key);
        var sourceIconBounds = sourceGeometry is null
            ? RectangleF.Empty
            : GetItemIconBounds(sourceGeometry);
        _dragIconGrabOffset = sourceIconBounds.IsEmpty
            ? new PointF(16, 16)
            : new PointF(
                Math.Clamp((_pressPoint.X - sourceIconBounds.X) / sourceIconBounds.Width, 0f, 1f) * 32f,
                Math.Clamp((_pressPoint.Y - sourceIconBounds.Y) / sourceIconBounds.Height, 0f, 1f) * 32f);
        var data = new Forms.DataObject();
        var itemKeys = selected.Select(candidate => candidate.Key.ToString()).ToArray();
        var dragSession = new InternalDragSession();
        data.SetData(ItemKeysFormat, itemKeys);
        data.SetData(SourceBoxFormat, sourceBoxId.ToString("D"));
        data.SetData(DragSessionFormat, false, dragSession);
        var sourceBox = _runtime.State.Boxes.FirstOrDefault(box => box.Id == sourceBoxId);
        var sourceMapped = sourceBox?.IsMappedFolder == true;
        var sourceMappedReadOnly = sourceBox?.MappedFolder?.IsReadOnly == true;
        var paths = selected.Where(candidate => candidate.FileSystemPath is not null).Select(candidate => candidate.FileSystemPath!).ToArray();
        if (paths.Length > 0 &&
            BoxDragCompletionPolicy.ShouldExposeFileDrop(paths.Length == selected.Length))
        {
            var collection = new StringCollection();
            collection.AddRange(paths);
            data.SetFileDropList(collection);
            // Same default as the desktop surface: leaving a box for Explorer is
            // a move unless the mapping is read-only (which only offers Copy).
            FileClipboardCodec.WritePreferredDropEffect(data, move: !sourceMappedReadOnly);
        }
        _dragDropCommitted = false;
        _dragCancelled = false;
        _showVirtualDesktopDropCursor = !sourceMapped;
        _runtime.SetVirtualBoxDesktopDropEnabled(!sourceMapped);
        var shouldReleaseToDesktop = false;
        var dragEffect = Forms.DragDropEffects.None;
        try
        {
            try
            {
                // Own one pointer image for the entire drag, including when
                // hovering the source box, other boxes, Explorer or no target.
                using var dragImage = CreateDragImage(selected, _pressedItem);
                using var pointerPreview = dragImage is null ? null : ItemDragPointerPreview.TryCreate(
                    this, dragImage.Bitmap, dragImage.CursorOffset);
                _iconDragStateForward?.Invoke(_pressPoint, null, itemKeys, _dragIconGrabOffset);
                RequestDragRender();
                // Keep the shell fallback only if our layered preview failed.
                if (pointerPreview is null && sourceMapped && dragImage is not null)
                {
                    DesktopDragImageHelper.TryInitialize(
                        data as IDataObject,
                        dragImage.Bitmap,
                        dragImage.CursorOffset);
                }
                // Explorer selects Move by default for a same-volume FileDrop.
                // A read-only mapping must therefore only advertise Copy; otherwise
                // a drop onto the desktop silently removes the mapped source file.
                dragEffect = DoDragDrop(
                    data,
                    sourceMappedReadOnly
                        ? Forms.DragDropEffects.Copy
                        : Forms.DragDropEffects.Move | Forms.DragDropEffects.Copy);
            }
            catch (Exception exception)
            {
                _dragCancelled = true;
                DiagnosticLog.Error("Box item drag loop failed", exception);
            }
            finally
            {
                _showVirtualDesktopDropCursor = false;
                _runtime.SetVirtualBoxDesktopDropEnabled(false);
                _runtime.ClearDesktopItemDropPreviews();
                Forms.Cursor.Current = Forms.Cursors.Default;
            }

            shouldReleaseToDesktop = BoxDragCompletionPolicy.ShouldUnassign(
                _dragDropCommitted,
                _dragCancelled,
                dragSession.HandledByBox || dragSession.HandledByDesktop,
                sourceMapped,
                IsPointerOverAnyBox(Forms.Cursor.Position),
                dragEffect != Forms.DragDropEffects.None);
        }
        finally
        {
            _runtime.SetVirtualBoxDesktopDropEnabled(false);
            ResetBoxItemDragState();
        }
        if (shouldReleaseToDesktop)
        {
            _ = ReleaseBoxItemsToDesktopAsync(itemKeys, Forms.Cursor.Position);
        }
    }

    internal void UpdateDynamicSelectionAtCursor()
    {
        if (_selectionBox is null || IsDisposed || _resourcesDisposed)
        {
            return;
        }

        var clientPoint = PointToClient(Forms.Cursor.Position);
        UpdateSelectionFromPoint(ToDip(clientPoint), requestRender: false);
    }

    private void UpdateSelectionFromPoint(PointF point, bool requestRender)
    {
        if (_selectionBox is not { } selectionBox)
        {
            return;
        }

        EnsureGeometry();
        var geometry = _boxes.FirstOrDefault(candidate => candidate.Box.Id == selectionBox.Id);
        if (geometry is null)
        {
            return;
        }

        var geometryChanged = !ReferenceEquals(_selectionGeometry, geometry);
        _selectionGeometry = geometry;

        var nextRectangle = RectangleF.Intersect(
            RectangleFromPoints(_selectionStart, point),
            geometry.Body);
        if (!geometryChanged && nextRectangle.Equals(_selectionRectangle))
        {
            return;
        }

        _selectionRectangle = nextRectangle;
        _selection.Clear();
        _selection.UnionWith(_selectionBase);
        _marqueeSelectionItems.Clear();
        _marqueeSelectionKeys.Clear();
        foreach (var candidate in _items)
        {
            if (candidate.Box.Id == selectionBox.Id &&
                candidate.Bounds.IntersectsWith(_selectionRectangle))
            {
                var itemKey = candidate.Item.Key.ToString();
                _selection.Add(itemKey);
                if (!_selectionBase.Contains(itemKey))
                {
                    _marqueeSelectionItems.Add(candidate);
                    _marqueeSelectionKeys.Add(itemKey);
                }
            }
        }

        if (requestRender)
        {
            RequestDragRender();
        }
    }

    private void ResetBoxItemDragState()
    {
        RectangleF? settledBoxBounds = null;
        if (_dragStarted && _pressedBoxId is { } sourceBoxId &&
            _runtime.State.Boxes.FirstOrDefault(box => box.Id == sourceBoxId) is { } sourceBox)
        {
            settledBoxBounds = new RectangleF(
                (float)sourceBox.Bounds.X,
                (float)sourceBox.Bounds.Y,
                (float)sourceBox.Bounds.Width,
                (float)GetVisualBoxHeight(sourceBox));
            settledBoxBounds = RectangleF.Inflate(settledBoxBounds.Value, 2, 2);
        }

        CancelPendingDragRender();
        if (_dragStarted)
        {
            _dynamicVisualVersion++;
        }
        _dragStarted = false;
        _dragIconGrabOffset = PointF.Empty;
        _dragDropCommitted = false;
        _dragCancelled = false;
        _pressedItem = null;
        _pressedBoxId = null;
        Invalidate();
        if (settledBoxBounds is { } dirtyBounds &&
            _isCompositedByIconSurface &&
            _iconLayerPartialRenderRequest is not null)
        {
            _iconLayerPartialRenderRequest(dirtyBounds);
        }
    }

    // The runtime owns the release transaction: visibility, Explorer
    // confirmation, assignment removal and final placement must happen in
    // that order. Keeping this form as a single caller avoids a second,
    // slightly different drag-release path per desktop surface.
    private async Task ReleaseBoxItemsToDesktopAsync(IReadOnlyList<string> itemKeys, Point screenPoint)
    {
        try
        {
            await _runtime.ReleaseAssignedItemsToDesktopAsync(itemKeys, screenPoint);
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("Failed to place released desktop items", exception);
        }
    }

    private void OnQueryContinueDrag(object? sender, Forms.QueryContinueDragEventArgs eventArgs)
    {
        if (eventArgs.EscapePressed || eventArgs.Action == Forms.DragAction.Cancel)
        {
            _dragCancelled = true;
            _runtime.ClearDesktopItemDropPreviews();
        }
        else if (eventArgs.Action == Forms.DragAction.Drop)
        {
            _dragDropCommitted = true;
        }
    }

    private bool IsPointerOverAnyBox(Point screenPoint)
    {
        foreach (var monitor in _runtime.Monitors)
        {
            if (!monitor.PixelBounds.Contains(screenPoint.X, screenPoint.Y))
            {
                continue;
            }
            var x = (screenPoint.X - monitor.PixelBounds.X) / monitor.DpiScale;
            var y = (screenPoint.Y - monitor.PixelBounds.Y) / monitor.DpiScale;
            return _runtime.State.Boxes.Any(box =>
                string.Equals(box.MonitorId, monitor.Id, StringComparison.OrdinalIgnoreCase) &&
                new LayoutRect(
                    box.Bounds.X,
                    box.Bounds.Y,
                    box.Bounds.Width,
                    GetVisualBoxHeight(box)).Contains(x, y));
        }
        return false;
    }

    private void OnMouseLeave(object? sender, EventArgs eventArgs)
    {
        if (HasActiveInlineRename)
        {
            DiagnosticLog.Verbose("Box hover leave retained while inline rename is active.");
            return;
        }
        if (ShouldSuspendHoverState(_openBoxMenuBoxId, inlineRenameActive: false) ||
            _runtime.IsDesktopIconPointerInteractionActive ||
            _movingBox is not null || _resizingBox is not null)
        {
            return;
        }

        QueueHoverReconcile();
    }

    private void QueueHoverReconcile()
    {
        if (_hoverReconcilePending || IsDisposed || !IsHandleCreated)
        {
            return;
        }

        _hoverReconcilePending = true;
        try
        {
            BeginInvoke((Action)ReconcileHoverAtCursor);
        }
        catch (InvalidOperationException)
        {
            _hoverReconcilePending = false;
        }
    }

    private void ReconcileHoverAtCursor()
    {
        _hoverReconcilePending = false;
        if (HasActiveInlineRename)
        {
            DiagnosticLog.Verbose("Box hover reconciliation deferred while inline rename is active.");
            return;
        }
        if (ShouldSuspendHoverState(_openBoxMenuBoxId, inlineRenameActive: false) ||
            _runtime.IsDesktopIconPointerInteractionActive ||
            _movingBox is not null || _resizingBox is not null || IsDisposed)
        {
            return;
        }

        // Reconcile against the latest pointer because a layered present can
        // emit MouseLeave without the pointer leaving the current item. This
        // also transfers hover cleanly when the pointer moves to a neighbour.
        var clientPoint = PointToClient(Forms.Cursor.Position);
        if (ClientRectangle.Contains(clientPoint))
        {
            var previousHoveredBoxId = _hoveredBoxId;
            UpdateHoverState(ToDip(clientPoint));
            if (ShouldRestoreHeaderActionOverlay(
                    previousHoveredBoxId == _hoveredBoxId,
                    _hoveredBoxId is not null || _searchingBoxId is not null,
                    _headerActionOverlay.Visible))
            {
                RequestHeaderActionVisualUpdate();
            }
            return;
        }

        Cursor = Forms.Cursors.Default;

        // Keep the hover-expansion controller in sync when a layered child
        // (for example an icon) reports MouseLeave after the pointer has
        // already left the form.  The old path only cleared visual hover
        // state, so a box that had already expanded never received the
        // "pointer left" timestamp and therefore never started its collapse
        // timer.
        UpdateHoverState(ToDip(clientPoint), updateItemHover: false);
        ClearAutoExpandHover();
        ClearItemHover();
    }

    private bool IsPointerOverInteractiveBox()
    {
        var clientPoint = PointToClient(Forms.Cursor.Position);
        return clientPoint.X >= 0 && clientPoint.Y >= 0 &&
            clientPoint.X < ClientSize.Width && clientPoint.Y < ClientSize.Height &&
            IsInteractivePointSafe(ToDip(clientPoint));
    }

    private void UpdatePointerCursor(PointF point)
    {
        var searchBoxId = _boxes.LastOrDefault(box => box.Search.Contains(point))?.Box.Id;
        var autoExpandBoxId = _boxes.LastOrDefault(box => box.AutoExpand.Contains(point))?.Box.Id;
        var menuBoxId = _boxes.LastOrDefault(box => box.Menu.Contains(point))?.Box.Id;
        if (_hoveredSearchBoxId != searchBoxId ||
            _hoveredAutoExpandBoxId != autoExpandBoxId ||
            _hoveredMenuBoxId != menuBoxId)
        {
            _hoveredSearchBoxId = searchBoxId;
            _hoveredAutoExpandBoxId = autoExpandBoxId;
            _hoveredMenuBoxId = menuBoxId;
            _headerToolTip.SetToolTip(this, null);
            if (searchBoxId is not null)
            {
                _headerToolTip.SetToolTip(this, "搜索盒子内容");
            }
            else if (autoExpandBoxId is not null)
            {
                var enabled = _boxes.FirstOrDefault(box => box.Box.Id == autoExpandBoxId)?.Box.ExpandOnHover == true;
                _headerToolTip.SetToolTip(
                    this,
                    enabled ? "切换为固定展开" : "切换为悬停自动展开");
            }
            else if (menuBoxId is not null)
            {
                _headerToolTip.SetToolTip(this, "盒子菜单");
            }
            RequestHeaderActionVisualUpdate();
        }
        var resizeEdges = ResizeEdges.None;
        if (_runtime.State.Settings.Appearance.ShowResizeGrip &&
            _boxes.LastOrDefault(box => !box.IsCollapsed && GetResizeEdges(box, point) != ResizeEdges.None) is { } resizeBox)
        {
            resizeEdges = GetResizeEdges(resizeBox, point);
        }
        var isHeaderButton = _boxes.LastOrDefault(box =>
            box.Search.Contains(point) ||
            box.AutoExpand.Contains(point) ||
            box.Menu.Contains(point)) is not null;
        var isBoxTab = _boxes.LastOrDefault(box =>
            GetMappedFolderTabAtPoint(box, point) is not null ||
            GetManualBoxTabAtPoint(box, point) is not null) is not null;
        var scrollBox = _boxes.LastOrDefault(box => box.Body.Contains(point));
        var isScrollBar = scrollBox is not null &&
                          GetScrollBarLayout(scrollBox)?.Track.Contains(point.X, point.Y) == true;
        Cursor = resizeEdges switch
        {
            ResizeEdges.Left or ResizeEdges.Right => Forms.Cursors.SizeWE,
            ResizeEdges.Top or ResizeEdges.Bottom => Forms.Cursors.SizeNS,
            ResizeEdges.TopLeft or ResizeEdges.BottomRight => Forms.Cursors.SizeNWSE,
            ResizeEdges.TopRight or ResizeEdges.BottomLeft => Forms.Cursors.SizeNESW,
            _ => isScrollBar
                ? Forms.Cursors.SizeNS
                : isHeaderButton || isBoxTab ? Forms.Cursors.Hand : Forms.Cursors.Default
        };
    }

    private void OnHoverTimer(object? sender, EventArgs eventArgs)
    {
        try
        {
            _hoverTimer.Stop();
            // A context menu is an extension of its box interaction. Freeze
            // hover expansion while the root menu or one of its submenus is
            // active; Closed queues one reconciliation against the real
            // pointer position so ordinary collapse timing resumes cleanly.
            if (HasActiveInlineRename)
            {
                DiagnosticLog.Verbose("Box hover timer retained expansion while inline rename is active.");
                return;
            }
            if (ShouldSuspendHoverState(_openBoxMenuBoxId, inlineRenameActive: false))
            {
                return;
            }
            // A desktop marquee owns the pointer capture. Do not let the
            // 25 ms box-hover poll mutate box geometry while that gesture is
            // in progress. An OLE file drag is different: its DragOver route
            // is owned by this form, so hover expansion must keep ticking
            // while the pointer is held over a collapsed box.
            if (!ShouldPollHoverDuringDesktopInteraction(
                    _runtime.IsDesktopIconPointerInteractionActive,
                    _runtime.IsDesktopIconDragActive))
            {
                return;
            }
            if (_movingBox is not null || _resizingBox is not null)
            {
                return;
            }
            if (_searchingBoxId is not null && _searchWindow.Visible)
            {
                return;
            }
            var trackItemHover = _runtime.State.Settings.Appearance.HoverFeedback;
            var trackExpansion = DesktopBoxes.Any(box => box.ExpandOnHover) || _hoverExpandedBoxes.Count > 0;
            if (!trackItemHover && !trackExpansion)
            {
                return;
            }
            var clientPoint = PointToClient(Forms.Cursor.Position);
            if (clientPoint.X < 0 || clientPoint.Y < 0 ||
                clientPoint.X >= ClientSize.Width || clientPoint.Y >= ClientSize.Height)
            {
                ClearHoverState();
                return;
            }
            // MouseMove is the sole owner of item hover. Keeping the timer
            // for expand-on-hover avoids a transient MouseLeave clearing and
            // restoring the same icon every 25 ms around a layered present.
            UpdateHoverState(ToDip(clientPoint), updateItemHover: false);
        }
        catch
        {
            ClearHoverState();
        }
    }

    private bool IsInteractivePoint(PointF point)
    {
        return DesktopBoxes.Any(box => new LayoutRect(
            box.Bounds.X,
            box.Bounds.Y,
            box.Bounds.Width,
            GetVisualBoxHeight(box)).Contains(point.X, point.Y));
    }

    private bool IsInteractivePointSafe(PointF point)
    {
        try
        {
            return IsInteractivePoint(point);
        }
        catch
        {
            return false;
        }
    }

    // Clears only the item highlight, leaving hover-expanded boxes intact.
    // Scrolling moves the content under a stationary pointer, so the item
    // hover must be reconciled right away: MouseMove alone would leave the
    // highlight stuck on an item that already scrolled away.
    private void ClearItemHover()
    {
        if (_hoveredItemKey is null)
        {
            HideItemHoverOverlay();
            return;
        }

        var previousHoveredItem = _items.LastOrDefault(candidate => string.Equals(
            candidate.Item.Key.ToString(),
            _hoveredItemKey,
            StringComparison.OrdinalIgnoreCase));
        _hoveredItemKey = null;
        HideItemHoverOverlay();
        InvalidateItem(previousHoveredItem);
    }

    private void ClearHoverState()
    {
        _hoverTimer.Stop();
        HideItemHoverOverlay();
        HideHeaderActionOverlay();
        var previousHoveredItem = _hoveredItemKey is null
            ? null
            : _items.LastOrDefault(candidate => string.Equals(
                candidate.Item.Key.ToString(),
                _hoveredItemKey,
                StringComparison.OrdinalIgnoreCase));
        var expandedBoxIds = _hoverExpandedBoxes.ToArray();
        _hoveredItemKey = null;
        var headerActionsChanged = SetHoveredBox(null);
        ClearAutoExpandHover();
        var expandedBoxId = _hoverExpansion.Reset();
        if (expandedBoxId is { } id)
        {
            CollapseHoverExpandedBox(id, updateRegion: false);
        }
        else
        {
            _hoverExpandedBoxes.Clear();
            _geometryDirty = true;
        }
        if (expandedBoxIds.Length > 0)
        {
            UpdateWindowRegion();
            RequestLayerRender();
            return;
        }
        if (headerActionsChanged)
        {
            RequestHeaderActionVisualUpdate();
        }
        InvalidateItem(previousHoveredItem);
    }

    private void UpdateHoverState(PointF point, bool updateItemHover = true)
    {
        EnsureGeometry();
        var hoveredBoxId = _boxes.LastOrDefault(candidate =>
            IsPointerInsideVisualBox(candidate.Bounds, point))?.Box.Id;
        var headerActionsChanged = SetHoveredBox(hoveredBoxId);
        var focusDirtyBounds = FocusBoxOnHover(hoveredBoxId);
        var hoverChanged = false;
        ItemGeometry? previousHoveredItem = null;
        ItemGeometry? hoveredItem = null;
        if (updateItemHover)
        {
            previousHoveredItem = _hoveredItemKey is null
                ? null
                : _items.LastOrDefault(candidate => string.Equals(
                    candidate.Item.Key.ToString(),
                    _hoveredItemKey,
                    StringComparison.OrdinalIgnoreCase));
            if (ShouldTrackItemHoverDuringScroll(
                    _scrollAnimationKey is not null,
                    _scrollHoverResumeTimer.Enabled))
            {
                var hoveredBox = _boxes.LastOrDefault(candidate => candidate.Bounds.Contains(point));
                hoveredItem = GetItemAtPoint(hoveredBox, point);
            }
            var itemKey = hoveredItem?.Item.Key.ToString();
            hoverChanged = !string.Equals(_hoveredItemKey, itemKey, StringComparison.OrdinalIgnoreCase);
            _hoveredItemKey = itemKey;
        }

        var structureChanged = false;
        var collapsedHeaderBoxId = _boxes.LastOrDefault(box =>
            IsCollapsedHeaderHoverTarget(
                box.Box.Id,
                hoveredBoxId,
                box.Box.ExpandOnHover,
                box.IsCollapsed,
                box.Header.Contains(point),
                box.Search.Contains(point) ||
                box.AutoExpand.Contains(point) ||
                box.Menu.Contains(point)))?.Box.Id;
        var pointerInsideExpandedBox = _hoverExpansion.ExpandedBoxId is { } expandedBoxId &&
            DesktopBoxes.FirstOrDefault(box => box.Id == expandedBoxId) is { } expandedBox &&
            IsPointerInsideExpandedBox(
                expandedBox.Bounds,
                GetInteractionBoxHeight(expandedBox),
                point);
        var autoExpandEnabled = _hoverExpansion.ExpandedBoxId is not null ||
            collapsedHeaderBoxId is not null;
        var now = DateTimeOffset.UtcNow;
        var transition = autoExpandEnabled &&
            _movingBox is null && _resizingBox is null
            ? _hoverExpansion.Update(collapsedHeaderBoxId, pointerInsideExpandedBox, now)
            : new HoverExpansionTransition(null, _hoverExpansion.Reset());
        if (transition.CollapsedBoxId is { } collapsedBoxId)
        {
            CollapseHoverExpandedBox(collapsedBoxId, updateRegion: false);
            structureChanged = true;
        }
        if (transition.ExpandedBoxId is { } boxId)
        {
            ExpandHoveredBox(boxId, updateRegion: false);
            structureChanged = true;
        }
        if (structureChanged)
        {
            UpdateWindowRegion();
            HideItemHoverOverlay();
            RequestLayerRender();
        }
        else
        {
            if (focusDirtyBounds is { } dirtyBounds)
            {
                RequestFocusVisualUpdate(dirtyBounds);
            }
            if (headerActionsChanged)
            {
                RequestHeaderActionVisualUpdate();
            }
            if (hoverChanged)
            {
                // The shared icon layer contains only settled box pixels. Keep
                // pointer feedback in a small child layer so crossing items does
                // not upload the entire monitor-sized bitmap.
                RequestItemHoverVisualUpdate();
            }
        }
        if (!structureChanged)
        {
            QueueHeightAnimationCachePrewarm(collapsedHeaderBoxId);
        }
        ScheduleHoverDeadline(now);
    }

    private void ScheduleHoverDeadline(DateTimeOffset now)
    {
        _hoverTimer.Stop();
        if (_hoverExpansion.NextTransitionAt is not { } due)
        {
            // An acrylic child spans the monitor, with input clipped to its
            // boxes. USER32 can keep TME_LEAVE armed when the pointer moves
            // into a transparent part of that client rectangle. Reconcile
            // the physical pointer while a standalone box is expanded so
            // its collapse deadline does not depend on WM_MOUSELEAVE.
            if (!_isCompositedByIconSurface && _hoverExpansion.ExpandedBoxId is not null)
            {
                _hoverTimer.Interval = 50;
                _hoverTimer.Start();
            }
            return;
        }

        _hoverTimer.Interval = Math.Clamp(
            (int)Math.Ceiling((due - now).TotalMilliseconds),
            1,
            1000);
        _hoverTimer.Start();
    }

    private void QueueHeightAnimationCachePrewarm(Guid? boxId)
    {
        if (boxId is not { } candidateBoxId ||
            _pendingHeightAnimationCachePrewarmBoxId == candidateBoxId ||
            _heightAnimationVisualCaches.ContainsKey(candidateBoxId))
        {
            return;
        }

        _pendingHeightAnimationCachePrewarmBoxId = candidateBoxId;
        try
        {
            BeginInvoke((Action)(() => PrewarmHeightAnimationCache(candidateBoxId)));
        }
        catch (InvalidOperationException)
        {
            _pendingHeightAnimationCachePrewarmBoxId = null;
        }
    }

    private void PrewarmHeightAnimationCache(Guid boxId)
    {
        if (_pendingHeightAnimationCachePrewarmBoxId == boxId)
        {
            _pendingHeightAnimationCachePrewarmBoxId = null;
        }
        if (_resourcesDisposed || IsDisposed)
        {
            return;
        }

        EnsureGeometry();
        var geometry = _boxes.LastOrDefault(box => box.Box.Id == boxId);
        if (geometry is null)
        {
            return;
        }
        var pointer = ToDip(PointToClient(Forms.Cursor.Position));
        var pointerStillOnHeader = geometry.Header.Contains(pointer) &&
            !geometry.Search.Contains(pointer) &&
            !geometry.AutoExpand.Contains(pointer) &&
            !geometry.Menu.Contains(pointer);
        if (!pointerStillOnHeader ||
            !ShouldPrewarmHeightAnimationCache(
                _isCompositedByIconSurface,
                _runtime.State.Settings.Appearance.AnimationEnabled,
                geometry.Box.ExpandOnHover,
                IsEffectivelyCollapsed(geometry.Box),
                _heightAnimationVisualCaches.ContainsKey(boxId)))
        {
            return;
        }

        if (_prewarmedHeightAnimationCacheBoxId is { } previousBoxId &&
            previousBoxId != boxId &&
            !_heightAnimations.ContainsKey(previousBoxId))
        {
            ReleaseHeightAnimationVisualCache(previousBoxId);
        }
        EnsureHeightAnimationVisualCache(geometry.Box);
        if (_heightAnimationVisualCaches.ContainsKey(boxId))
        {
            _prewarmedHeightAnimationCacheBoxId = boxId;
        }
    }

    private RectangleF? FocusBoxOnHover(Guid? boxId)
    {
        if (boxId is not { } focusedBoxId || _focusedBoxId == focusedBoxId)
        {
            return null;
        }

        var previousBoxId = _focusedBoxId;
        var previousBounds = previousBoxId is { } previousId
            ? _boxes.FirstOrDefault(box => box.Box.Id == previousId)?.Bounds
            : null;
        _focusedBoxId = focusedBoxId;
        var focusedGeometryIndex = _boxes.FindIndex(box => box.Box.Id == focusedBoxId);
        if (focusedGeometryIndex >= 0)
        {
            var focusedGeometry = _boxes[focusedGeometryIndex];
            _boxes.RemoveAt(focusedGeometryIndex);
            _boxes.Add(focusedGeometry);
        }
        var currentBounds = _boxes.First(box => box.Box.Id == focusedBoxId).Bounds;
        DiagnosticLog.Verbose(
            $"Box hover focus monitor={_monitor.Id} {previousBoxId?.ToString("N") ?? "<none>"} -> {focusedBoxId:N}");
        return CalculateFocusDirtyBounds(previousBounds, currentBounds);
    }

    private void RequestFocusVisualUpdate(RectangleF dirtyBounds)
    {
        if (_isCompositedByIconSurface && _iconLayerPartialRenderRequest is not null)
        {
            _iconLayerPartialRenderRequest(dirtyBounds);
            return;
        }

        RequestVisualLayerRender();
    }

    private bool SetHoveredBox(Guid? boxId)
    {
        if (_hoveredBoxId == boxId)
        {
            return false;
        }

        _hoveredBoxId = boxId;
        return true;
    }

    private void FinishSelectionGesture()
    {
        if (_selectionBox is null)
        {
            return;
        }

        _selectionBox = null;
        _selectionGeometry = null;
        _selectionBase.Clear();
        _marqueeSelectionItems.Clear();
        _marqueeSelectionKeys.Clear();
        _selectionRectangle = RectangleF.Empty;
        _dynamicVisualVersion++;
        if (Capture)
        {
            Capture = false;
        }
        // Rebuild the settled layer once with the final selection. The
        // composited path queues this through the icon surface; the fallback
        // path presents the ordinary box layer directly.
        RequestVisualLayerRender();
        if (_isCompositedByIconSurface && !_itemHoverOverlayUnavailable)
        {
            PresentItemHoverOverlay();
        }
        // The marquee frame temporarily hides the header-action overlay.
        // Restore it after the settled selection frame when the pointer is
        // still inside the same box, just like box-transform completion.
        QueueHoverReconcile();
    }

    private void OnMouseUp(object? sender, Forms.MouseEventArgs eventArgs)
    {
        DiagnosticLog.Verbose(
            $"Surface mouse up monitor={_monitor.Id} button={eventArgs.Button} moving={_movingBox is not null} resizing={_resizingBox is not null} selecting={_selectionBox is not null}");
        if (eventArgs.Button == Forms.MouseButtons.Left && _scrollBarDrag is not null)
        {
            FinishScrollBarDrag();
            return;
        }
        if (eventArgs.Button == Forms.MouseButtons.Left)
        {
            CommitPendingSlowDoubleClickRename();
        }
        if (_selectionBox is not null)
        {
            FinishSelectionGesture();
            return;
        }
        var movingBox = _movingBox;
        var resizingBox = _resizingBox;
        var releasePoint = ToDip(eventArgs.Location);
        if (movingBox is not null)
        {
            UpdateMovingBox(movingBox, releasePoint);
        }
        else if (resizingBox is not null)
        {
            UpdateResizingBox(resizingBox, releasePoint);
        }
        var grabOffsetX = _pressPoint.X - _startBounds.X;
        var grabOffsetY = _pressPoint.Y - _startBounds.Y;
        CompleteBoxTransform(movingBox, resizingBox, grabOffsetX, grabOffsetY, true);
    }

    // Match Explorer's slow double-click behavior used by desktop icons.
    // A drag clears the pending state so dragging always wins.
    private void TryBeginSlowDoubleClickRename(ItemGeometry item)
    {
        var now = DateTime.UtcNow;
        var key = item.Item.Key.ToString();
        var isSlowDoubleClick = SlowDoubleClickRenamePolicy.IsSlowDoubleClick(
            _lastRenameClickKey,
            _lastRenameClickUtc,
            key,
            now,
            Forms.SystemInformation.DoubleClickTime);
        _lastRenameClickKey = key;
        _lastRenameClickUtc = now;
        if (isSlowDoubleClick &&
            item.Item.FileSystemPath is not null &&
            item.Box.MappedFolder?.IsReadOnly != true)
        {
            _pendingRenameItem = item.Item;
            _pendingRenameBoxId = item.Box.Id;
            _pendingRenamePressUtc = now;
        }
    }

    private void CommitPendingSlowDoubleClickRename()
    {
        var item = _pendingRenameItem;
        var boxId = _pendingRenameBoxId;
        _pendingRenameItem = null;
        _pendingRenameBoxId = null;
        if (item is null ||
            boxId is not { } targetBoxId ||
            _dragStarted ||
            _selectionBox is not null ||
            _movingBox is not null ||
            _resizingBox is not null ||
            IsDisposed)
        {
            return;
        }

        var elapsed = (DateTime.UtcNow - _pendingRenamePressUtc).TotalMilliseconds;
        if (elapsed > SlowDoubleClickRenamePolicy.RenameLimitMilliseconds)
        {
            return;
        }

        var box = DesktopBoxes.FirstOrDefault(candidate => candidate.Id == targetBoxId);
        if (box is null)
        {
            return;
        }

        _lastRenameClickKey = null;
        _ = RenameItemAsync(box, item);
    }

    protected override void OnMouseCaptureChanged(EventArgs eventArgs)
    {
        base.OnMouseCaptureChanged(eventArgs);
        if (Capture)
        {
            return;
        }
        if (_scrollBarDrag is not null)
        {
            _scrollBarDrag = null;
            QueueHoverReconcile();
            return;
        }
        if (_selectionBox is not null)
        {
            FinishSelectionGesture();
        }
        if (_movingBox is null && _resizingBox is null)
        {
            return;
        }

        // Capture can be stolen by Explorer, Alt+Tab, or a shell popup before
        // MouseUp arrives. Always commit the last rendered bounds and flush the
        // swept area so a half-finished drag cannot leave pixels behind.
        var grabOffsetX = _pressPoint.X - _startBounds.X;
        var grabOffsetY = _pressPoint.Y - _startBounds.Y;
        CompleteBoxTransform(_movingBox, _resizingBox, grabOffsetX, grabOffsetY, true);
    }

    private void CompleteBoxTransform(
        DesktopBox? movingBox,
        DesktopBox? resizingBox,
        double grabOffsetX,
        double grabOffsetY,
        bool allowMonitorTransfer)
    {
        CancelPendingDragRender();
        ClearBoxAlignmentGuides();
        if (_movingBox is not null || _resizingBox is not null)
        {
            _dynamicVisualVersion++;
        }
        _movingBox = null;
        _resizingBox = null;
        ReleaseMovingBoxVisualCache();
        _geometryDirty = true;
        _resizeEdges = ResizeEdges.None;
        _pressedItem = null;
        _pressedBoxId = null;
        if (Capture)
        {
            Capture = false;
        }
        var monitorChanged = _monitorTransferSeen;
        var previousMonitorId = _monitorTransferLastPreviousMonitorId;
        if (movingBox is not null && allowMonitorTransfer)
        {
            var cursor = Forms.Cursor.Position;
            var beforeMonitorId = movingBox.MonitorId;
            var targetMonitor = _runtime.Monitors.FirstOrDefault(candidate =>
                candidate.PixelBounds.Contains(cursor.X, cursor.Y));
            var targetScale = Math.Max(targetMonitor?.DpiScale ?? _scale, 0.01d);
            var moved = LayoutCoordinator.TryMoveBoxToMonitor(
                movingBox,
                _runtime.Monitors,
                cursor.X,
                cursor.Y,
                grabOffsetX * _scale / targetScale,
                grabOffsetY * _scale / targetScale,
                gridStep: 0);
            monitorChanged |= moved;
            if (moved)
            {
                previousMonitorId = beforeMonitorId;
            }
        }

        if (movingBox is not null)
        {
            ClampBoxPositionForCommit(movingBox);
        }

        UpdateWindowRegion();
        FlushTransformTrail();
        if (movingBox is not null)
        {
            _runtime.BoxChanged(
                movingBox,
                ShouldRebuildWorkspaceAfterBoxTransform(monitorChanged),
                monitorChanged ? previousMonitorId : null);
        }
        else if (resizingBox is not null)
        {
            _runtime.BoxChanged(
                resizingBox,
                ShouldRebuildWorkspaceAfterBoxTransform(monitorChanged: false));
        }
        if (movingBox is not null || resizingBox is not null)
        {
            // The transform cache owns the header actions while the box is
            // moving. Once the settled box is committed, restore the small
            // hover overlay even when the logical hover target did not change.
            QueueHoverReconcile();
        }
    }

    private void UpdateMovingBox(DesktopBox box, PointF point)
    {
        var cursor = Forms.Cursor.Position;
        var target = _runtime.Monitors.FirstOrDefault(monitor =>
            monitor.PixelBounds.Contains(cursor.X, cursor.Y));
        // Once the live box has transferred away from this source surface,
        // continue tracking it in global screen coordinates. Falling back to
        // the source form's client point would clamp the box back into the
        // source monitor on the very next mouse move.
        if (!string.Equals(box.MonitorId, _monitor.Id, StringComparison.OrdinalIgnoreCase))
        {
            if (target is null)
            {
                if (SetBoxAlignmentGuides([], null)) RequestDragRender();
                _iconLayerRenderRequest?.Invoke();
                return;
            }

            var targetScale = Math.Max(target.DpiScale, 0.01d);
            var grabOffsetX = (_pressPoint.X - _startBounds.X) * _scale / targetScale;
            var grabOffsetY = (_pressPoint.Y - _startBounds.Y) * _scale / targetScale;
            if (!string.Equals(target.Id, box.MonitorId, StringComparison.OrdinalIgnoreCase))
            {
                var previousMonitorId = box.MonitorId;
                var moved = LayoutCoordinator.TryMoveBoxToMonitor(
                    box,
                    _runtime.Monitors,
                    cursor.X,
                    cursor.Y,
                    grabOffsetX,
                    grabOffsetY,
                    gridStep: 0);
                _monitorTransferSeen |= moved;
                if (moved)
                {
                    ApplyBoxDragPosition(box, box.Bounds, target);
                    MarkMonitorTransferRefreshPending(previousMonitorId);
                }
                _iconLayerRenderRequest?.Invoke();
                return;
            }

            var localCursorX = (cursor.X - target.PixelBounds.X) / targetScale;
            var localCursorY = (cursor.Y - target.PixelBounds.Y) / targetScale;
            var transferredBounds = new LayoutRect(
                SnapDipToMonitorPixel(localCursorX - grabOffsetX, targetScale),
                SnapDipToMonitorPixel(localCursorY - grabOffsetY, targetScale),
                _startBounds.Width,
                _startBounds.Height).Clamp(
                    new LayoutRect(0, 0, target.WorkArea.Width, target.WorkArea.Height),
                    GetMinimumBoxWidth(box));
            ApplyBoxDragPosition(box, transferredBounds, target);
            _iconLayerRenderRequest?.Invoke();
            return;
        }

        if (target is not null && !string.Equals(target.Id, box.MonitorId, StringComparison.OrdinalIgnoreCase))
        {
            var previousMonitorId = box.MonitorId;
            var moved = LayoutCoordinator.TryMoveBoxToMonitor(
                box,
                _runtime.Monitors,
                cursor.X,
                cursor.Y,
                (_pressPoint.X - _startBounds.X) * _scale / Math.Max(target.DpiScale, 0.01),
                (_pressPoint.Y - _startBounds.Y) * _scale / Math.Max(target.DpiScale, 0.01),
                gridStep: 0);
            _monitorTransferSeen |= moved;
            if (moved)
            {
                ApplyBoxDragPosition(box, box.Bounds, target);
                MarkMonitorTransferRefreshPending(previousMonitorId);
            }
            _iconLayerRenderRequest?.Invoke();
            return;
        }
        var nextBounds = new LayoutRect(
            SnapDipToPixel(_startBounds.X + point.X - _pressPoint.X),
            SnapDipToPixel(_startBounds.Y + point.Y - _pressPoint.Y),
            _startBounds.Width,
            _startBounds.Height).Clamp(
                new LayoutRect(0, 0, _monitor.WorkArea.Width, _monitor.WorkArea.Height),
                GetMinimumBoxWidth(box));
        ApplyBoxDragPosition(box, nextBounds, _monitor);
    }

    private void ClampBoxPositionForCommit(DesktopBox box)
    {
        var monitor = _runtime.Monitors.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, box.MonitorId, StringComparison.OrdinalIgnoreCase))
            ?? _monitor;
        // Commit exactly the last free/aligned position. Never introduce a
        // grid step or search for a new snap target when the pointer is released.
        box.Bounds = box.Bounds.Clamp(
            new LayoutRect(0, 0, monitor.WorkArea.Width, monitor.WorkArea.Height),
            GetMinimumBoxWidth(box));
    }

    private static double SnapDipToMonitorPixel(double value, double scale) =>
        Math.Round(value * scale, MidpointRounding.AwayFromZero) / scale;

    private void UpdateResizingBox(DesktopBox box, PointF point)
    {
        var deltaX = point.X - _pressPoint.X;
        var deltaY = point.Y - _pressPoint.Y;
        var startRight = _startBounds.X + _startBounds.Width;
        var startBottom = _startBounds.Y + _startBounds.Height;
        var left = _startBounds.X;
        var top = _startBounds.Y;
        var right = startRight;
        var bottom = startBottom;
        if (_resizeEdges.HasFlag(ResizeEdges.Left))
        {
            left += deltaX;
        }
        if (_resizeEdges.HasFlag(ResizeEdges.Right))
        {
            right += deltaX;
        }
        if (_resizeEdges.HasFlag(ResizeEdges.Top))
        {
            top += deltaY;
        }
        if (_resizeEdges.HasFlag(ResizeEdges.Bottom))
        {
            bottom += deltaY;
        }

        var workArea = new LayoutRect(0, 0, _monitor.WorkArea.Width, _monitor.WorkArea.Height);
        var minWidth = LayoutGrid.SnapUp(GetMinimumBoxWidth(box));
        var tabBarHeight = _boxes.FirstOrDefault(candidate => candidate.Box.Id == box.Id)?.TabBar.Height ?? 0;
        var minHeight = LayoutGrid.SnapUp(DesktopItemLayoutEngine.GetMinimumBoxHeight(
            box.ViewMode,
            box.Appearance.TitleBarHeight,
            box.Appearance.IconSize,
            DesktopItemLayoutEngine.ScaleIconSpacing(_runtime.State.Settings.Appearance.IconVerticalSpacing, box.Appearance.IconSize),
            tabBarHeight));
        if (_resizeEdges.HasFlag(ResizeEdges.Left))
        {
            left = Math.Clamp(left, workArea.X, startRight - minWidth);
        }
        else
        {
            right = Math.Clamp(right, _startBounds.X + minWidth, workArea.X + workArea.Width);
        }
        if (_resizeEdges.HasFlag(ResizeEdges.Top))
        {
            top = Math.Clamp(top, workArea.Y, startBottom - minHeight);
        }
        else
        {
            bottom = Math.Clamp(bottom, _startBounds.Y + minHeight, workArea.Y + workArea.Height);
        }

        var requestedWidth = right - left;
        var requestedHeight = bottom - top;
        var widthSlot = DesktopItemLayoutEngine.SnapBoxWidth(
            box.ViewMode,
            requestedWidth,
            box.Appearance.IconSize,
            DesktopItemLayoutEngine.ScaleIconSpacing(_runtime.State.Settings.Appearance.IconHorizontalSpacing, box.Appearance.IconSize));
        var heightSlot = DesktopItemLayoutEngine.SnapBoxHeight(
            box.ViewMode,
            requestedHeight,
            box.Appearance.TitleBarHeight,
            box.Appearance.IconSize,
            DesktopItemLayoutEngine.ScaleIconSpacing(_runtime.State.Settings.Appearance.IconVerticalSpacing, box.Appearance.IconSize),
            tabBarHeight);
        const double snapThreshold = DesktopItemLayoutEngine.SnapThreshold;
        if (Math.Abs(requestedWidth - widthSlot) <= snapThreshold)
        {
            if (_resizeEdges.HasFlag(ResizeEdges.Left))
            {
                left = startRight - widthSlot;
            }
            else
            {
                right = _startBounds.X + widthSlot;
            }
        }
        if (Math.Abs(requestedHeight - heightSlot) <= snapThreshold)
        {
            if (_resizeEdges.HasFlag(ResizeEdges.Top))
            {
                top = startBottom - heightSlot;
            }
            else
            {
                bottom = _startBounds.Y + heightSlot;
            }
        }
        var nextBounds = new LayoutRect(left, top, right - left, bottom - top).Clamp(
            workArea,
            minWidth,
            minHeight);
        ApplyBoxTransform(box, nextBounds);
    }

    private static ResizeEdges GetResizeEdges(BoxGeometry geometry, PointF point)
    {
        const float tolerance = 9;
        var nearLeft = Math.Abs(point.X - geometry.Bounds.Left) <= tolerance;
        var nearRight = Math.Abs(point.X - geometry.Bounds.Right) <= tolerance;
        var nearTop = Math.Abs(point.Y - geometry.Bounds.Top) <= tolerance;
        var nearBottom = Math.Abs(point.Y - geometry.Bounds.Bottom) <= tolerance;
        var horizontal = point.Y >= geometry.Bounds.Top - tolerance &&
            point.Y <= geometry.Bounds.Bottom + tolerance;
        var vertical = point.X >= geometry.Bounds.Left - tolerance &&
            point.X <= geometry.Bounds.Right + tolerance;
        var edges = ResizeEdges.None;
        if (horizontal && nearLeft) edges |= ResizeEdges.Left;
        if (horizontal && nearRight) edges |= ResizeEdges.Right;
        if (vertical && nearTop) edges |= ResizeEdges.Top;
        if (vertical && nearBottom) edges |= ResizeEdges.Bottom;
        return edges;
    }

    private void ApplyBoxTransform(DesktopBox box, LayoutRect nextBounds)
    {
        if (box.Bounds == nextBounds)
        {
            return;
        }
        AccumulateTransformDirtyBounds(ToVisualBounds(box, box.Bounds));
        box.Bounds = nextBounds;
        AccumulateTransformDirtyBounds(ToVisualBounds(box, nextBounds));
        // A move keeps the active box's size and item layout intact. Its
        // dynamic icon-layer pass translates the cached geometry, leaving the
        // complete box/item rebuild for the final committed frame. Resizes do
        // need a fresh layout because their content bounds change.
        _geometryDirty = _resizingBox is not null;
        RequestDragRender();
    }

    private LayoutRect ToVisualBounds(DesktopBox box, LayoutRect bounds) => new(
        bounds.X,
        bounds.Y,
        bounds.Width,
        IsEffectivelyCollapsed(box) ? box.Appearance.TitleBarHeight : bounds.Height);

    private void AccumulateTransformDirtyBounds(LayoutRect bounds)
    {
        if (_transformDirtyBounds is not { } dirty)
        {
            _transformDirtyBounds = bounds;
            return;
        }
        var left = Math.Min(dirty.X, bounds.X);
        var top = Math.Min(dirty.Y, bounds.Y);
        var right = Math.Max(dirty.X + dirty.Width, bounds.X + bounds.Width);
        var bottom = Math.Max(dirty.Y + dirty.Height, bounds.Y + bounds.Height);
        _transformDirtyBounds = new LayoutRect(left, top, right - left, bottom - top);
    }

    private void FlushTransformTrail()
    {
        if (_transformDirtyBounds is null || !IsHandleCreated)
        {
            _transformDirtyBounds = null;
            return;
        }
        var dirtyBounds = _transformDirtyBounds.Value;
        _transformDirtyBounds = null;
        if (ShouldUsePartialTransformCommit(
                _isCompositedByIconSurface,
                _iconLayerPartialRenderRequest is not null))
        {
            _iconLayerPartialRenderRequest!(new RectangleF(
                (float)dirtyBounds.X,
                (float)dirtyBounds.Y,
                (float)dirtyBounds.Width,
                (float)dirtyBounds.Height));
            return;
        }
        PresentLayer();
    }

    private void OnMouseDoubleClick(object? sender, Forms.MouseEventArgs eventArgs)
    {
        if (eventArgs.Button != Forms.MouseButtons.Left)
        {
            return;
        }
        var point = ToDip(eventArgs.Location);
        var box = _boxes.LastOrDefault(candidate => candidate.Bounds.Contains(point));
        var item = GetItemAtPoint(box, point);
        DiagnosticLog.Info(
            $"Surface double click monitor={_monitor.Id} x={point.X:0} y={point.Y:0} box={box?.Box.Id} itemKind={item?.Item.Key.Kind}");
        if (box is not null &&
            (GetMappedFolderTabAtPoint(box, point) is not null ||
             GetManualBoxTabAtPoint(box, point) is not null))
        {
            return;
        }
        if (item is not null)
        {
            TryAction(() => _runtime.FileOperations.Open(item.Item));
            return;
        }
        if (box is not null &&
            box.Header.Contains(point) &&
            !box.Search.Contains(point) &&
            !box.Menu.Contains(point) &&
            !box.AutoExpand.Contains(point))
        {
            BeginTitleEdit(box.Box);
        }
    }

    private void OnMouseWheel(object? sender, Forms.MouseEventArgs eventArgs)
    {
        // WinForms otherwise forwards WM_MOUSEWHEEL synchronously to this
        // child window's Explorer parent after raising MouseWheel. Explorer
        // can take seconds to answer while publishing a pasted file. Mark the
        // original handled event instead of replaying the native message.
        if (eventArgs is Forms.HandledMouseEventArgs handledEventArgs)
        {
            handledEventArgs.Handled = true;
        }
        if ((DesktopWindowTools.GetAsyncModifierKeys() & Forms.Keys.Control) != 0)
        {
            return;
        }

        EnsureGeometry();
        TryScrollBox(ToDip(eventArgs.Location), eventArgs.Delta);
    }

    internal bool TryScrollBoxAt(Point screenPoint, int delta)
    {
        if (delta == 0 || IsDisposed || _resourcesDisposed)
        {
            return false;
        }

        EnsureGeometry();
        return TryScrollBox(ToDip(PointToClient(screenPoint)), delta);
    }

    private bool TryScrollBox(PointF point, int delta)
    {
        var box = _boxes.LastOrDefault(candidate => candidate.Bounds.Contains(point));
        if (box is null)
        {
            return false;
        }
        var scrollKey = GetItemViewKey(box);
        var itemCount = GetVisibleItemsForBox(box).Count;
        var extent = DesktopItemLayoutEngine.GetScrollExtent(
            box.Box.ViewMode,
            new LayoutRect(box.Body.X, box.Body.Y, box.Body.Width, box.Body.Height),
            itemCount,
            box.Box.Appearance.IconSize,
            DesktopItemLayoutEngine.ScaleIconSpacing(
                _runtime.State.Settings.Appearance.IconHorizontalSpacing,
                box.Box.Appearance.IconSize),
            DesktopItemLayoutEngine.ScaleIconSpacing(
                _runtime.State.Settings.Appearance.IconVerticalSpacing,
                box.Box.Appearance.IconSize));
        if (extent <= 0)
        {
            return false;
        }

        // Continue from the offset that is currently on screen, so rapid
        // wheel input glides through every notch instead of skipping to the
        // latest target.
        var current = _scrollAnimationKey == scrollKey && IsScrollAnimationActive
            ? GetAnimatedScrollOffset()
            : _scrollOffsets.GetValueOrDefault(scrollKey);
        // A standard notch is 120 units. Map the configured wheel lines onto
        // thirds of an item cell, then apply a small reduction so the default
        // three-line setting advances about 0.75 cell instead of jumping three
        // complete rows. High-resolution touchpads keep their proportional
        // delta and therefore remain finer than a mouse wheel.
        var step = CalculateSmoothScrollStep(
            GetScrollUnit(box),
            Forms.SystemInformation.MouseWheelScrollLines,
            Math.Abs(delta));
        var target = Math.Clamp(current - Math.Sign(delta) * step, 0, extent);
        if (Math.Abs(target - current) < 0.5)
        {
            return false;
        }

        StartScrollAnimation(scrollKey, current, target);
        return true;
    }

    private double GetScrollUnit(BoxGeometry box)
    {
        return box.Box.ViewMode == BoxViewMode.List
            ? Math.Max(48, box.Box.Appearance.IconSize + 12)
            : DesktopItemLayoutEngine.GetGridCellHeight(
                box.Box.Appearance.IconSize,
                DesktopItemLayoutEngine.ScaleIconSpacing(
                    _runtime.State.Settings.Appearance.IconVerticalSpacing,
                    box.Box.Appearance.IconSize));
    }

    internal static double CalculateSmoothScrollStep(
        double itemUnit,
        int configuredScrollLines,
        int wheelDelta)
    {
        if (itemUnit <= 0 || wheelDelta == 0)
        {
            return 0;
        }

        var lines = configuredScrollLines > 0 ? configuredScrollLines : 3;
        var deltaScale = Math.Abs(wheelDelta) / 120d;
        return Math.Max(
            2d,
            itemUnit * (lines / 3d) * ScrollWheelStepFraction * deltaScale);
    }

    private double GetAnimatedScrollOffset()
    {
        var progress = Math.Min(
            1,
            Stopwatch.GetElapsedTime(_scrollAnimationStartedTimestamp).TotalMilliseconds /
            ScrollAnimationDurationMilliseconds);
        var eased = 1 - Math.Pow(1 - progress, ScrollEaseExponent);
        return _scrollAnimationFrom + (_scrollAnimationTo - _scrollAnimationFrom) * eased;
    }

    private void StartScrollAnimation(ItemViewKey key, double from, double to)
    {
        // Keep the independent hover overlay hidden for the complete scroll
        // animation. Re-enabling it from MouseMove between animation frames
        // leaves the highlight at an obsolete item position.
        _scrollHoverResumeTimer.Stop();
        ClearItemHover();
        var startsNewDynamicPass = !IsScrollAnimationActive || _scrollAnimationKey != key;
        _scrollAnimationKey = key;
        _scrollAnimationFrom = from;
        _scrollAnimationTo = to;
        _scrollAnimationStartedTimestamp = Stopwatch.GetTimestamp();
        _animationFrameClock.RequestFrames();
        if (startsNewDynamicPass)
        {
            _dynamicVisualVersion++;
        }
        ApplyScrollOffset(key, from, requestRender: false);
    }

    private bool AdvanceScrollAnimation(bool requestRender)
    {
        if (_scrollAnimationKey is not { } key)
        {
            return false;
        }

        var progress = Math.Min(
            1,
            Stopwatch.GetElapsedTime(_scrollAnimationStartedTimestamp).TotalMilliseconds /
            ScrollAnimationDurationMilliseconds);
        var eased = 1 - Math.Pow(1 - progress, ScrollEaseExponent);
        var offset = _scrollAnimationFrom + (_scrollAnimationTo - _scrollAnimationFrom) * eased;
        var completed = progress >= 1;
        if (completed)
        {
            offset = _scrollAnimationTo;
        }
        ApplyScrollOffset(key, offset, requestRender: requestRender && !completed);
        if (completed)
        {
            _scrollAnimationKey = null;
            _dynamicVisualVersion++;
            _scrollHoverResumeTimer.Stop();
            _scrollHoverResumeTimer.Start();
        }
        return completed;
    }

    private void OnScrollHoverResumeTimerTick(object? sender, EventArgs eventArgs)
    {
        _scrollHoverResumeTimer.Stop();
        QueueHoverReconcile();
    }

    private void ApplyScrollOffset(ItemViewKey key, double offset, bool requestRender = true)
    {
        _scrollOffsets[key] = offset;
        ClearExpandedItemHitBounds(key.BoxId);
        // Only the item rectangles depend on the scroll offset; the box
        // chrome (header, tabs, body) stays untouched. Skipping the full
        // geometry rebuild keeps each animation frame cheap enough to render
        // without dropping the input pipeline.
        if (_geometryDirty)
        {
            // A full rebuild is already queued; it picks up the offset.
            if (requestRender)
            {
                RequestVisualLayerRender();
            }
            return;
        }
        RebuildScrolledBoxItemGeometry(key.BoxId);
        if (requestRender)
        {
            // Scrolling never changes the box input region. In desktop
            // composition mode, bypass the full-monitor hit-mask upload and
            // let the icon layer update only the scrolling box rectangle.
            RequestVisualLayerRender();
        }
    }

    private void RebuildScrolledBoxItemGeometry(Guid boxId)
    {
        var geometry = _boxes.FirstOrDefault(box => box.Box.Id == boxId);
        if (geometry is null || geometry.IsCollapsed)
        {
            return;
        }

        // Scrolling changes only this box's item rectangles. Keep the other
        // boxes' geometry and ordering intact so every frame avoids a full
        // layout pass and repeated filtering of unrelated items.
        var replacement = new List<ItemGeometry>();
        BuildItemGeometry(geometry, replacement);
        var firstIndex = _items.FindIndex(item => item.Box.Id == boxId);
        for (var index = _items.Count - 1; index >= 0; index--)
        {
            if (_items[index].Box.Id == boxId)
            {
                _items.RemoveAt(index);
            }
        }

        if (replacement.Count == 0)
        {
            return;
        }

        if (firstIndex >= 0)
        {
            _items.InsertRange(Math.Min(firstIndex, _items.Count), replacement);
            return;
        }

        // A box can have no visible items at one offset and gain them at the
        // next one. Insert the replacement after the preceding box groups to
        // preserve hit-test/topmost ordering.
        var insertIndex = 0;
        foreach (var previousBox in _boxes)
        {
            if (previousBox.Box.Id == boxId)
            {
                break;
            }

            var previousIndex = _items.FindLastIndex(item => item.Box.Id == previousBox.Box.Id);
            if (previousIndex >= 0)
            {
                insertIndex = Math.Max(insertIndex, previousIndex + 1);
            }
        }
        _items.InsertRange(Math.Min(insertIndex, _items.Count), replacement);
    }

    private void ClearExpandedItemHitBounds(Guid boxId)
    {
        foreach (var key in _expandedItemHitBounds.Keys
                     .Where(candidate => candidate.BoxId == boxId)
                     .ToArray())
        {
            _expandedItemHitBounds.Remove(key);
        }
    }

}

