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

    private void OnMouseDown(object? sender, Forms.MouseEventArgs eventArgs)
    {
        // A click on the box surface while an inline rename is open commits
        // the edit (this window never activates, so Deactivate does not fire).
        _renameEditor?.CommitExternally();
        if (_editingBox is not null)
        {
            FinishTitleEdit(true);
        }
        RebuildGeometry();
        var point = ToDip(eventArgs.Location);
        var box = _boxes.LastOrDefault(candidate => candidate.Bounds.Contains(point));
        var item = GetItemAtPoint(box, point);
        if (item is not null)
        {
            _runtime.ActivateDesktopKeyboardInput();
            TryBeginSlowDoubleClickRename(item);
        }
        DiagnosticLog.Info(
            $"Surface mouse down monitor={_monitor.Id} button={eventArgs.Button} x={point.X:0} y={point.Y:0} box={box?.Box.Id} itemKind={item?.Item.Key.Kind}");
        if (eventArgs.Button == Forms.MouseButtons.Right)
        {
            if (item is not null)
            {
                var itemKey = item.Item.Key.ToString();
                if (!_selection.Contains(itemKey))
                {
                    _selection.Clear();
                    _selection.Add(itemKey);
                }
                ShowItemContextMenu(item.Box, item.Item, eventArgs.Location);
            }
            else if (box is not null)
            {
                BuildBoxMenu(box.Box).Show(this, eventArgs.Location);
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
            if ((Forms.Control.ModifierKeys & Forms.Keys.Control) != 0 && _selection.Contains(key))
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
            if ((Forms.Control.ModifierKeys & Forms.Keys.Control) == 0 && !_selection.Contains(key))
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
            BuildBoxMenu(box.Box).Show(this, eventArgs.Location);
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
            _selectionBox = box.Box;
            _selectionGeometry = box;
            _selectionStart = point;
            _selectionRectangle = RectangleF.Empty;
            _selectionBase.Clear();
            _marqueeSelectionItems.Clear();
            _marqueeSelectionKeys.Clear();
            if ((Forms.Control.ModifierKeys & Forms.Keys.Control) != 0)
            {
                _selectionBase.UnionWith(_selection);
            }
            else
            {
                _selection.Clear();
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

    private void OnMouseMove(object? sender, Forms.MouseEventArgs eventArgs)
    {
        var point = ToDip(eventArgs.Location);
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
                // Virtual box-to-desktop drops carry private metadata, for
                // which Explorer does not reliably render IDragSourceHelper's
                // image. The desktop surface owns that preview instead. Keep
                // the shell image for mapped-folder file drags only.
                using var dragImage = sourceMapped ? CreateDragImage(selected, _pressedItem) : null;
                if (dragImage is not null)
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
        CancelPendingDragRender();
        if (_dragStarted)
        {
            _dynamicVisualVersion++;
        }
        _dragStarted = false;
        _dragDropCommitted = false;
        _dragCancelled = false;
        _pressedItem = null;
        _pressedBoxId = null;
        Invalidate();
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
        if (_runtime.IsDesktopIconPointerInteractionActive ||
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
        if (ShouldSuspendHoverState(_openBoxMenuBoxId) ||
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
        var headerActionsChanged = SetHoveredBox(null);
        ClearAutoExpandHover();
        if (_hoveredItemKey is not null)
        {
            var previousHoveredItem = _items.LastOrDefault(candidate => string.Equals(
                candidate.Item.Key.ToString(),
                _hoveredItemKey,
                StringComparison.OrdinalIgnoreCase));
            _hoveredItemKey = null;
            HideItemHoverOverlay();
            InvalidateItem(previousHoveredItem);
        }
        if (headerActionsChanged)
        {
            RequestHeaderActionVisualUpdate();
        }
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
        if (_hoveredSearchBoxId != searchBoxId || _hoveredAutoExpandBoxId != autoExpandBoxId)
        {
            _hoveredSearchBoxId = searchBoxId;
            _hoveredAutoExpandBoxId = autoExpandBoxId;
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
        Cursor = resizeEdges switch
        {
            ResizeEdges.Left or ResizeEdges.Right => Forms.Cursors.SizeWE,
            ResizeEdges.Top or ResizeEdges.Bottom => Forms.Cursors.SizeNS,
            ResizeEdges.TopLeft or ResizeEdges.BottomRight => Forms.Cursors.SizeNWSE,
            ResizeEdges.TopRight or ResizeEdges.BottomLeft => Forms.Cursors.SizeNESW,
            _ => isHeaderButton || isBoxTab ? Forms.Cursors.Hand : Forms.Cursors.Default
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
            if (ShouldSuspendHoverState(_openBoxMenuBoxId))
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
            hoveredBoxId == expandedBoxId;
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
    }

    private void OnMouseUp(object? sender, Forms.MouseEventArgs eventArgs)
    {
        DiagnosticLog.Verbose(
            $"Surface mouse up monitor={_monitor.Id} button={eventArgs.Button} moving={_movingBox is not null} resizing={_resizingBox is not null} selecting={_selectionBox is not null}");
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
        CompleteBoxTransform(_movingBox, _resizingBox, grabOffsetX, grabOffsetY, false);
    }

    private void CompleteBoxTransform(
        DesktopBox? movingBox,
        DesktopBox? resizingBox,
        double grabOffsetX,
        double grabOffsetY,
        bool allowMonitorTransfer)
    {
        CancelPendingDragRender();
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
        if (movingBox is not null && allowMonitorTransfer)
        {
            var cursor = Forms.Cursor.Position;
            LayoutCoordinator.TryMoveBoxToMonitor(
                movingBox,
                _runtime.Monitors,
                cursor.X,
                cursor.Y,
                grabOffsetX,
                grabOffsetY,
                LayoutGrid.DefaultStep);
        }

        if (movingBox is not null)
        {
            SnapBoxPositionForCommit(movingBox);
        }

        UpdateWindowRegion();
        FlushTransformTrail();
        if (movingBox is not null)
        {
            _runtime.BoxChanged(movingBox, ShouldRebuildWorkspaceAfterBoxTransform());
        }
        else if (resizingBox is not null)
        {
            _runtime.BoxChanged(resizingBox, ShouldRebuildWorkspaceAfterBoxTransform());
        }
    }

    private void UpdateMovingBox(DesktopBox box, PointF point)
    {
        var nextBounds = new LayoutRect(
            SnapDipToPixel(_startBounds.X + point.X - _pressPoint.X),
            SnapDipToPixel(_startBounds.Y + point.Y - _pressPoint.Y),
            _startBounds.Width,
            _startBounds.Height).Clamp(
                new LayoutRect(0, 0, _monitor.WorkArea.Width, _monitor.WorkArea.Height),
                GetMinimumBoxWidth(box));
        ApplyBoxTransform(box, nextBounds);
    }

    private void SnapBoxPositionForCommit(DesktopBox box)
    {
        var monitor = _runtime.Monitors.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, box.MonitorId, StringComparison.OrdinalIgnoreCase))
            ?? _monitor;
        var scale = Math.Max(monitor.DpiScale, 0.01d);
        var snappedBounds = new LayoutRect(
            SnapDipToMonitorPixel(LayoutGrid.Snap(box.Bounds.X), scale),
            SnapDipToMonitorPixel(LayoutGrid.Snap(box.Bounds.Y), scale),
            box.Bounds.Width,
            box.Bounds.Height).Clamp(
            new LayoutRect(0, 0, monitor.WorkArea.Width, monitor.WorkArea.Height),
            GetMinimumBoxWidth(box));
        box.Bounds = snappedBounds;
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
        if ((Forms.Control.ModifierKeys & Forms.Keys.Control) != 0)
        {
            return;
        }
        var point = ToDip(eventArgs.Location);
        var box = _boxes.LastOrDefault(candidate => candidate.Bounds.Contains(point));
        if (box is null)
        {
            return;
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
            return;
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
            Math.Abs(eventArgs.Delta));
        var target = Math.Clamp(current - Math.Sign(eventArgs.Delta) * step, 0, extent);
        if (Math.Abs(target - current) < 0.5)
        {
            return;
        }

        StartScrollAnimation(scrollKey, current, target);
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
            (DateTime.UtcNow - _scrollAnimationStartedUtc).TotalMilliseconds /
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
        _scrollAnimationStartedUtc = DateTime.UtcNow;
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
            (DateTime.UtcNow - _scrollAnimationStartedUtc).TotalMilliseconds /
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
        _items.Clear();
        foreach (var box in _boxes.Where(box => !box.IsCollapsed))
        {
            BuildItemGeometry(box);
        }
        if (requestRender)
        {
            // Scrolling never changes the box input region. In desktop
            // composition mode, bypass the full-monitor hit-mask upload and
            // let the icon layer update only the scrolling box rectangle.
            RequestVisualLayerRender();
        }
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

