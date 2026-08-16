using System.Drawing;
using System.Globalization;
using System.Collections.Specialized;
using System.Drawing.Drawing2D;
using CrabDesk.Core;
using CrabDesk.Native;
using Forms = System.Windows.Forms;

namespace CrabDesk.Runtime;

/// <summary>
/// Draws ordinary desktop items on a full-monitor, per-pixel-alpha surface.
/// Explorer's ListView can therefore be hidden as one visual layer while the
/// underlying desktop files retain their normal attributes and remain visible
/// to every common file dialog.
/// </summary>
internal sealed class DesktopIconSurface : Forms.Form
{
    // Carries CrabDesk's stable item keys alongside the standard FileDrop
    // payload used by external applications.
    internal const string DesktopIconDragSessionFormat = "CrabDesk.DesktopIconDragSession";
    // Browser uploads normally choose Copy, while Recycle Bin requires Move.
    // Advertise both so each target can negotiate its supported operation.
    internal static Forms.DragDropEffects ExternalFileDropEffects =>
        Forms.DragDropEffects.Copy | Forms.DragDropEffects.Move;
    private const int WmMouseActivate = 0x0021;
    private const int WmContextMenu = 0x007B;
    private const int WsClipSiblings = 0x04000000;
    private const int WsExLayered = 0x00080000;
    private const float DefaultIconSize = 48;
    private const float DefaultHorizontalSpacing = 88;
    private const float DefaultVerticalSpacing = 96;
    private const float DesktopGridEdgeInset = 8;
    private const int CompactLabelLineCount = 2;
    // A per-pixel-alpha layered window is click-through where alpha is zero.
    // Keep the desktop background visually transparent while leaving it
    // targetable for blank-area marquee selection.
    private const int DesktopHitTestAlpha = 1;
    // Let a layered-window MouseLeave settle before recalculating hover.
    private const int HoverReconcileDelayMilliseconds = 32;
    private const int SlowDoubleClickRenameLimitMilliseconds = 900;
    private static readonly IntPtr MaNoActivate = new(3);
    private readonly CrabDeskRuntime _runtime;
    private readonly MonitorLayout _monitor;
    private readonly IntPtr _desktopListView;
    private readonly double _scale;
    private readonly List<DesktopIconGeometry> _items = [];
    private readonly HashSet<string> _selection = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _selectionBase = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dragItemKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GridCell> _boxDropPreviewCells = new(StringComparer.OrdinalIgnoreCase);
    // A highlighted long label can extend beyond its grid cell. Retain that
    // visual footprint while the pointer crosses the expanded label.
    private readonly Dictionary<string, RectangleF> _expandedItemHitBounds = new(StringComparer.OrdinalIgnoreCase);
    // The shell provider owns and may evict its cached bitmaps. Keep copies
    // here because this full-surface renderer can reuse an icon across frames.
    private readonly Dictionary<(string ParsingName, int PixelSize), Bitmap> _desktopIconCache = [];
    private readonly HashSet<string> _boxDropItemKeys = new(StringComparer.OrdinalIgnoreCase);
    private DesktopItemRef? _pressedItem;
    private PointF _pressPoint;
    private PointF _dragPointer;
    // Keep the pointer relationship to the actual rendered icon so the
    // floating preview stays attached to the exact pixel that was grabbed.
    private PointF _dragIconGrabOffset;
    private string? _dragAnchorKey;
    private GridCell? _dragAnchorCell;
    private GridCell? _lastDragPreviewAnchorCell;
    private bool _dragPointerOverBox;
    private PointF? _boxDragPointer;
    private string? _boxDragPrimaryKey;
    private bool _persistingLayout;
    private PointF _selectionStart;
    private RectangleF _selectionRectangle;
    private bool _dragStarted;
    private bool _selecting;
    private bool _virtualBoxDropTargetEnabled;
    private bool _desktopOleDragActive;
    private string? _hoveredItemKey;
    private ShellContextMenuSession? _shellContextMenu;
    private bool _lastPresentSucceeded;
    private bool _lastRegionSucceeded;
    private float _iconSize = DefaultIconSize;
    private float _horizontalSpacing = DefaultHorizontalSpacing;
    private float _verticalSpacing = DefaultVerticalSpacing;
    private Size _lastKnownNativeSpacing = new(88, 96);
    private string _lastPresentDiagnostic = string.Empty;
    private string _lastRegionDiagnostic = string.Empty;
    private string _lastGridDiagnostic = string.Empty;
    private string _lastAppliedRegionKey = string.Empty;
    private DesktopGridTopology? _previousGridTopology;
    private Action<Graphics, RectangleF>? _boxRenderer;
    private Action<Graphics, RectangleF>? _dragBoxRenderer;
    private Func<bool>? _boxTransformActive;
    private Func<RectangleF?>? _boxDynamicBounds;
    private Func<int>? _boxDynamicVersion;
    private Action? _boxDynamicStateUpdate;
    private Func<Point, bool>? _boxPointerHitTest;
    private Func<bool>? _boxHeightAnimationOnly;
    private readonly Forms.Timer _hoverReconcileTimer;
    private readonly DesktopDragOverlay _dragOverlay;
    private readonly DesktopHoverOverlay _hoverOverlay;
    private DesktopRenameEditor? _renameEditor;
    private bool _overRecycleBin;
    // Slow double-click rename: the second click on the same icon inside the
    // window between the system double-click time and this limit enters
    // rename mode, exactly like Explorer's label edit.
    private string? _lastRenameClickKey;
    private DateTime _lastRenameClickUtc = DateTime.MinValue;
    // A slow double-click qualifies for rename on the second press, but the
    // rename is deferred until mouse up: if that press turns into a drag the
    // drag wins and no rename editor opens.
    private DesktopIconGeometry? _pendingRenameItem;
    private DateTime _pendingRenamePressUtc;
    // External file drags paint their own ghost (Explorer's drag image is not
    // shown over the replacement layer), so the dragged content stays visible
    // next to the cursor like the native desktop.
    private string[]? _externalDragPaths;
    private PointF _externalDragPointer;
    private Bitmap? _externalDragIcon;
    private string? _externalDragIconPath;
    private readonly DesktopHoverRenderState _hoverRenderState = new();
    private bool _geometryDirty = true;
    private bool _dragRenderPending;
    private bool _hoverReconcilePending;
    private bool _presentingLayer;
    private bool _presentRequested;
    private Bitmap? _layerBitmap;
    private Bitmap? _staticLayerBitmap;
    private RectangleF? _lastHoverOverlayBounds;
    private bool _hoverOverlayUnavailable;
    private bool _dragBaseReady;
    private int _lastBoxDynamicVersion = int.MinValue;

    internal DesktopIconSurface(
        CrabDeskRuntime runtime,
        MonitorLayout monitor,
        IntPtr desktopListView)
    {
        _runtime = runtime;
        _monitor = monitor;
        _desktopListView = desktopListView;
        _scale = monitor.DpiScale;
        Text = "CrabDesk Desktop Icons";
        FormBorderStyle = Forms.FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = Forms.FormStartPosition.Manual;
        AutoScaleMode = Forms.AutoScaleMode.None;
        ClientSize = new Size((int)monitor.PixelBounds.Width, (int)monitor.PixelBounds.Height);
        DoubleBuffered = true;
        SetStyle(
            Forms.ControlStyles.AllPaintingInWmPaint |
            Forms.ControlStyles.UserPaint |
            Forms.ControlStyles.OptimizedDoubleBuffer,
            true);

        MouseDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseUp += OnMouseUp;
        MouseDoubleClick += OnMouseDoubleClick;
        MouseLeave += OnMouseLeave;
        MouseCaptureChanged += OnMouseCaptureChanged;
        // The replacement layer sits above Explorer's (hidden) list view, so
        // it must stay registered as an OLE target from startup: external
        // file drops land here and are routed to the desktop folder (or the
        // Recycle Bin) by OnDragOver/OnDragDrop.
        AllowDrop = true;
        DragEnter += OnDragOver;
        DragOver += OnDragOver;
        DragLeave += OnDragLeave;
        DragDrop += OnDragDrop;
        _dragOverlay = new DesktopDragOverlay();
        Controls.Add(_dragOverlay);
        _hoverOverlay = new DesktopHoverOverlay();
        Controls.Add(_hoverOverlay);
        _hoverReconcileTimer = new Forms.Timer { Interval = HoverReconcileDelayMilliseconds };
        _hoverReconcileTimer.Tick += OnHoverReconcileTimerTick;
    }

    protected override bool ShowWithoutActivation => true;

    protected override Forms.CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.Style &= ~WsClipSiblings;
            parameters.ExStyle |= WsExLayered;
            return parameters;
        }
    }

    protected override void WndProc(ref Forms.Message message)
    {
        if (_shellContextMenu?.TryHandleMessage(
                message.Msg,
                message.WParam,
                message.LParam,
                out var shellMenuResult) == true)
        {
            message.Result = shellMenuResult;
            return;
        }
        if (message.Msg == WmMouseActivate)
        {
            message.Result = MaNoActivate;
            return;
        }
        if (message.Msg == WmContextMenu)
        {
            message.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref message);
    }

    protected override void OnPaintBackground(Forms.PaintEventArgs eventArgs)
    {
    }

    protected override void OnPaint(Forms.PaintEventArgs eventArgs)
    {
        // UpdateLayeredWindow owns the pixels. Re-presenting from every
        // WM_PAINT creates a second full-monitor commit after hover changes.
        if (!_lastPresentSucceeded)
        {
            PresentLayer();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CancelPendingDragRender();
            _hoverReconcileTimer.Stop();
            _hoverReconcileTimer.Dispose();
            ClearDesktopIconCache();
            _layerBitmap?.Dispose();
            _layerBitmap = null;
            _staticLayerBitmap?.Dispose();
            _staticLayerBitmap = null;
            _dragOverlay.Dispose();
            _hoverOverlay.Dispose();
            _renameEditor?.Dispose();
            _renameEditor = null;
            ReleaseExternalDragIcon();
            LayeredWindowPresenter.Release(Handle);
            _shellContextMenu?.Dispose();
            _shellContextMenu = null;
        }
        base.Dispose(disposing);
    }

    private void EnsureLayerBitmap()
    {
        if (_layerBitmap is null ||
            _layerBitmap.Width != ClientSize.Width ||
            _layerBitmap.Height != ClientSize.Height)
        {
            _layerBitmap?.Dispose();
            _layerBitmap = DesktopLayerBitmapFactory.Create(
                ClientSize.Width,
                ClientSize.Height);
        }
    }

    private void EnsureStaticLayerBitmap()
    {
        if (_staticLayerBitmap is null ||
            _staticLayerBitmap.Width != ClientSize.Width ||
            _staticLayerBitmap.Height != ClientSize.Height)
        {
            _staticLayerBitmap?.Dispose();
            _staticLayerBitmap = DesktopLayerBitmapFactory.Create(
                ClientSize.Width,
                ClientSize.Height);
            _dragBaseReady = false;
        }
    }

    internal bool RefreshWorkspace()
    {
        ClearDesktopIconCache();
        _geometryDirty = true;
        _dragBaseReady = false;
        return PresentLayer();
    }

    internal string MonitorId => _monitor.Id;

    internal void SetBoxRenderer(Action<Graphics, RectangleF>? renderer) =>
        _boxRenderer = renderer;

    internal void SetDragBoxRenderer(Action<Graphics, RectangleF>? renderer) =>
        _dragBoxRenderer = renderer;

    internal void SetBoxTransformActive(Func<bool>? provider) =>
        _boxTransformActive = provider;

    internal void SetBoxDynamicBounds(Func<RectangleF?>? provider) =>
        _boxDynamicBounds = provider;

    internal void SetBoxDynamicVersion(Func<int>? provider) =>
        _boxDynamicVersion = provider;

    internal void SetBoxDynamicStateUpdater(Action? updater) =>
        _boxDynamicStateUpdate = updater;

    internal void SetBoxPointerHitTest(Func<Point, bool>? hitTest) =>
        _boxPointerHitTest = hitTest;

    internal void SetBoxHeightAnimationOnly(Func<bool>? provider) =>
        _boxHeightAnimationOnly = provider;

    internal bool RequestRender() => PresentLayer();

    internal void RequestDragFrame()
    {
        if (IsDragCompositeActive)
        {
            // A box selection can receive several mouse messages before the
            // queued compositor callback runs. Let the owner reconcile its
            // state with the physical cursor before deciding what to paint.
            _boxDynamicStateUpdate?.Invoke();
        }

        // Promote a box into the small drag overlay before its settled pixels
        // are removed from the monitor layer. The first frame must be
        // synchronous: waiting for the coalescer leaves one composition frame
        // where neither layer owns the box.
        if (IsDragCompositeActive && !_dragBaseReady)
        {
            if (_selecting)
            {
                // A marquee has no floating box that must be promoted before
                // the next frame. Keep its base build on the queued path so a
                // box callback cannot block mouse input.
                RequestDragRender();
                return;
            }
            CancelPendingDragRender();
            PresentLayer();
            return;
        }

        RequestDragRender();
    }

    private void RequestDragRender()
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        // Mouse handlers only publish the latest pointer state. Posting the
        // compositor pass lets WinForms drain the input queue before the
        // potentially expensive layered-window update starts.
        if (_dragRenderPending)
        {
            return;
        }

        _dragRenderPending = true;
        // BeginInvoke gives the current mouse handler a chance to publish the
        // latest point while guaranteeing that only one render callback is
        // queued at a time.
        QueueDragRender();
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

    private void RenderQueuedDragFrame()
    {
        if (!_dragRenderPending || IsDisposed)
        {
            return;
        }

        _dragRenderPending = false;
        // Mouse messages can be coalesced behind a layered-window upload. Read
        // the physical cursor once immediately before painting so the frame
        // represents the pointer's current position instead of the last
        // message that happened to reach the handler.
        if (IsDragCompositeActive)
        {
            _boxDynamicStateUpdate?.Invoke();
        }
        if (_selecting && IsHandleCreated)
        {
            UpdateMarqueeSelection(ToDip(PointToClient(Forms.Cursor.Position)));
        }
        PresentLayer();
    }

    private void CancelPendingDragRender()
    {
        _dragRenderPending = false;
    }

    private void RequestHoverRender()
    {
        if (IsDisposed || !IsHandleCreated || IsDragCompositeActive)
        {
            _hoverOverlay.HideOverlay();
            _lastHoverOverlayBounds = null;
            return;
        }

        if (_hoverOverlayUnavailable)
        {
            RequestDragRender();
            return;
        }

        if (!_hoverRenderState.Publish(_hoveredItemKey))
        {
            return;
        }

        try
        {
            BeginInvoke((Action)RenderQueuedHoverFrame);
        }
        catch (InvalidOperationException)
        {
            _hoverRenderState.TryTake(out _);
        }
    }

    private void RenderQueuedHoverFrame()
    {
        if (!_hoverRenderState.TryTake(out _) || IsDisposed)
        {
            return;
        }

        if (_geometryDirty)
        {
            PresentLayer();
            return;
        }

        if (!PresentHoverOverlay(GetDesktopWorkAreaBounds()))
        {
            RequestDragRender();
        }
    }

    // The full-monitor layered window accepts input only in the Windows work
    // area. That keeps the taskbar outside both desktop selection and icon
    // layout while preserving the ordinary blank-area marquee gesture.
    internal bool IsLayerReady => _lastPresentSucceeded && _lastRegionSucceeded;

    internal void SetVirtualBoxDropTargetEnabled(bool enabled)
    {
        _virtualBoxDropTargetEnabled = enabled;
        UpdateDropTargetRegistration();
        if (!enabled)
        {
            ClearBoxDropPreview();
        }
    }

    // The replacement layer owns the desktop background, so the normal
    // Explorer click monitor must not clear this gesture while WinForms is
    // still delivering captured mouse events.
    internal bool IsPointerInteractionActive =>
        _selecting || _dragStarted || _pressedItem is not null;

    internal string LayerDiagnostic =>
        _lastPresentSucceeded && _lastRegionSucceeded
            ? _lastPresentDiagnostic
            : $"present={_lastPresentDiagnostic}; region={_lastRegionDiagnostic}";

    internal void ClearSelection()
    {
        if (_selection.Count == 0)
        {
            return;
        }
        _selection.Clear();
        PresentLayer();
    }

    internal bool HasSelection => _selection.Count > 0;

    internal IReadOnlyList<DesktopItemRef> GetSelectedItems() => _items
        .Where(item => _selection.Contains(item.Item.Key.ToString()))
        .Select(item => item.Item)
        .ToArray();

    internal IReadOnlyList<DesktopItemRef> GetSelectedFileSystemItems() => GetSelectedItems()
        .Where(item => item.FileSystemPath is not null)
        .ToArray();

    internal bool SelectAllItems()
    {
        if (_items.Count == 0)
        {
            return false;
        }

        _selection.Clear();
        foreach (var item in _items)
        {
            _selection.Add(item.Item.Key.ToString());
        }
        _pressedItem = null;
        PresentLayer();
        return true;
    }

    internal int RenameSelectionCount => GetSelectedFileSystemItems().Count;

    internal bool BeginRenameSelectedItem()
    {
        var selectedItems = GetSelectedFileSystemItems();
        if (selectedItems.Count != 1)
        {
            return false;
        }

        _ = RenameItemAsync(selectedItems[0]);
        return true;
    }

    internal int ClearIconCache()
    {
        return ClearDesktopIconCache();
    }

    private bool PresentLayer()
    {
        if (_presentingLayer)
        {
            _presentRequested = true;
            return _lastPresentSucceeded;
        }

        _presentingLayer = true;
        try
        {
            return PresentLayerCore();
        }
        finally
        {
            _presentingLayer = false;
            if (_presentRequested && !IsDisposed)
            {
                _presentRequested = false;
                RequestDragRender();
            }
        }
    }

    private bool PresentLayerCore()
    {
        if (IsDisposed || !IsHandleCreated || ClientSize.Width <= 0 || ClientSize.Height <= 0)
        {
            _lastPresentSucceeded = false;
            _lastRegionSucceeded = false;
            _lastPresentDiagnostic = "The desktop icon surface has no valid handle or size.";
            _lastRegionDiagnostic = _lastPresentDiagnostic;
            return false;
        }

        if (_geometryDirty)
        {
            RebuildGeometry();
            _geometryDirty = false;
        }
        var workAreaBounds = GetDesktopWorkAreaBounds();
        // The interaction region only changes when the work area or DPI scale
        // changes. Applying SetWindowRgn on every drag frame is expensive,
        // so keep the last applied region key and skip the call when it
        // is unchanged.
        var regionKey = string.Format(
            CultureInfo.InvariantCulture,
            "{0:0.####};{1:0.####};{2:0.####};{3:0.####};{4}",
            workAreaBounds.X,
            workAreaBounds.Y,
            workAreaBounds.Width,
            workAreaBounds.Height,
            _scale);
        if (!string.Equals(_lastAppliedRegionKey, regionKey, StringComparison.Ordinal))
        {
            _lastRegionSucceeded = DesktopWindowTools.ApplyRegion(
                Handle,
                [new LayoutRect(
                    workAreaBounds.X,
                    workAreaBounds.Y,
                    workAreaBounds.Width,
                    workAreaBounds.Height)],
                _scale,
                out _lastRegionDiagnostic);
            if (_lastRegionSucceeded)
            {
                _lastAppliedRegionKey = regionKey;
            }
        }

        if (!_lastRegionSucceeded)
        {
            _lastPresentSucceeded = false;
            DiagnosticLog.Error(
                $"Desktop icon interaction region failed monitor={_monitor.Id}: {_lastRegionDiagnostic}",
                new InvalidOperationException(_lastRegionDiagnostic));
            return false;
        }

        // A captured marquee freezes box interaction for the duration of the
        // gesture. Avoid asking every box to rebuild dynamic geometry while
        // the pointer is only changing the selection rectangle.
        var boxIsDynamic = _boxTransformActive?.Invoke() == true;
        var boxDynamicVersion = _selecting && !boxIsDynamic
            ? _lastBoxDynamicVersion
            : _boxDynamicVersion?.Invoke() ?? 0;
        if (_lastBoxDynamicVersion != boxDynamicVersion)
        {
            _lastBoxDynamicVersion = boxDynamicVersion;
            _dragBaseReady = false;
        }

        if (IsDragCompositeActive)
        {
            _hoverOverlay.HideOverlay();
            _lastHoverOverlayBounds = null;
            EnsureStaticLayerBitmap();
            var staticFrameChanged = !_dragBaseReady;
            if (staticFrameChanged)
            {
                using var baseGraphics = Graphics.FromImage(_staticLayerBitmap!);
                DrawSettledLayer(
                    baseGraphics,
                    workAreaBounds,
                    includeBoxDropPreview: false,
                    selectedItemKeys: _selecting ? _selectionBase : null,
                    includeSelectionRectangle: !_selecting);
                _dragBaseReady = true;
            }

            // A pure hover-expand height animation stays inside the parent
            // bitmap so the box never crosses between the settled layer and
            // the drag overlay. That handoff is what flashes for one
            // compositor frame at the start and end of the animation.
            if (_boxHeightAnimationOnly?.Invoke() == true &&
                !_selecting &&
                !_dragStarted &&
                _boxDropItemKeys.Count == 0)
            {
                return PresentHeightAnimationFrame(workAreaBounds);
            }

            // The overlay is a child of this surface. Present it while the
            // previous settled frame is still visible, then replace that
            // frame with the version that excludes the dynamic box. Reversing
            // the order briefly leaves no window drawing the box.
            if (!PresentDragOverlay(workAreaBounds))
            {
                return false;
            }

            if (staticFrameChanged)
            {
                _lastPresentSucceeded = LayeredWindowPresenter.TryPresent(
                    Handle,
                    _staticLayerBitmap!,
                    PointToScreen(Point.Empty),
                    out _lastPresentDiagnostic);
                if (!_lastPresentSucceeded)
                {
                    DiagnosticLog.Error(
                        $"Desktop icon static drag presentation failed monitor={_monitor.Id}: {_lastPresentDiagnostic}",
                        new InvalidOperationException(_lastPresentDiagnostic));
                    return false;
                }
            }

            return _lastPresentSucceeded;
        }

        EnsureLayerBitmap();
        using (var graphics = Graphics.FromImage(_layerBitmap!))
        {
            DrawSettledLayer(
                graphics,
                workAreaBounds,
                includeHoverFeedback: _hoverOverlayUnavailable);
        }
        EnsureStaticLayerBitmap();
        using (var baseGraphics = Graphics.FromImage(_staticLayerBitmap!))
        {
            baseGraphics.CompositingMode = CompositingMode.SourceCopy;
            baseGraphics.DrawImageUnscaled(_layerBitmap!, 0, 0);
        }
        _dragBaseReady = false;

        _lastPresentSucceeded = LayeredWindowPresenter.TryPresent(
            Handle,
            _layerBitmap!,
            PointToScreen(Point.Empty),
            out _lastPresentDiagnostic);
        if (!_lastPresentSucceeded)
        {
            DiagnosticLog.Error(
                $"Desktop icon layered presentation failed monitor={_monitor.Id}: {_lastPresentDiagnostic}",
                new InvalidOperationException(_lastPresentDiagnostic));
        }
        else
        {
            // Keep the dynamic child visible until the parent already contains
            // the final settled box. Hiding it first produces the end-of-drag
            // flash users can see on a compositor frame boundary.
            _dragOverlay.HideOverlay();
        }
        if (!PresentHoverOverlay(workAreaBounds))
        {
            RequestDragRender();
        }
        return _lastPresentSucceeded;
    }

    // Icon drags keep the grabbed icons attached to the pointer through the
    // small drag overlay (no squeeze markers, no grid reflow preview). The
    // composite path is shared with marquee selection, dynamic box visuals,
    // and external file drops (whose ghost card is drawn by the overlay).
    private bool IsDragCompositeActive =>
        _selecting ||
        _dragStarted ||
        _externalDragPaths is { Length: > 0 } ||
        _boxTransformActive?.Invoke() == true;

    private bool PresentDragOverlay(RectangleF workAreaBounds)
    {
        var overlayBounds = GetDragOverlayBounds(workAreaBounds);
        if (overlayBounds is not { } bounds)
        {
            _dragOverlay.HideOverlay();
            return _lastPresentSucceeded;
        }

        if (_dragOverlay.Present(
                bounds,
                _scale,
                (graphics, alignedBounds) => DrawDragOverlay(graphics, alignedBounds),
                out var overlayDiagnostic))
        {
            return true;
        }

        // A layered child overlay is supported on the target Windows versions,
        // but preserve rendering if a host or shell variant rejects it.
        _dragOverlay.HideOverlay();
        EnsureLayerBitmap();
        using (var graphics = Graphics.FromImage(_layerBitmap!))
        {
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.DrawImageUnscaled(_staticLayerBitmap!, 0, 0);
            graphics.CompositingMode = CompositingMode.SourceOver;
            ConfigureLayerGraphics(graphics, workAreaBounds, fastRender: true);
            DrawDynamicDragVisuals(graphics, workAreaBounds);
            graphics.ResetTransform();
        }
        _lastPresentSucceeded = LayeredWindowPresenter.TryPresent(
            Handle,
            _layerBitmap!,
            PointToScreen(Point.Empty),
            out _lastPresentDiagnostic);
        if (!_lastPresentSucceeded)
        {
            _lastPresentDiagnostic = $"overlay={overlayDiagnostic}; fallback={_lastPresentDiagnostic}";
            DiagnosticLog.Error(
                $"Desktop icon drag presentation failed monitor={_monitor.Id}: {_lastPresentDiagnostic}",
                new InvalidOperationException(_lastPresentDiagnostic));
        }
        return _lastPresentSucceeded;
    }

    /// <summary>
    /// Presents a hover-expand height animation entirely through the parent
    /// layered window. The static base (desktop icons and settled boxes) is
    /// copied and the animated box is drawn on top, so one atomic
    /// UpdateLayeredWindow per tick carries the whole frame. Unlike the drag
    /// overlay channel this never hands the box between two windows, which
    /// avoids the single-frame flash at the start and end of the animation.
    /// </summary>
    private bool PresentHeightAnimationFrame(RectangleF workAreaBounds)
    {
        _dragOverlay.HideOverlay();
        EnsureLayerBitmap();
        using (var graphics = Graphics.FromImage(_layerBitmap!))
        {
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.DrawImageUnscaled(_staticLayerBitmap!, 0, 0);
            graphics.CompositingMode = CompositingMode.SourceOver;
            ConfigureLayerGraphics(graphics, workAreaBounds, fastRender: false);
            DrawDynamicDragVisuals(graphics, workAreaBounds);
            graphics.ResetTransform();
        }

        _lastPresentSucceeded = LayeredWindowPresenter.TryPresent(
            Handle,
            _layerBitmap!,
            PointToScreen(Point.Empty),
            out _lastPresentDiagnostic);
        if (!_lastPresentSucceeded)
        {
            DiagnosticLog.Error(
                $"Desktop icon height animation presentation failed monitor={_monitor.Id}: {_lastPresentDiagnostic}",
                new InvalidOperationException(_lastPresentDiagnostic));
        }
        return _lastPresentSucceeded;
    }

    private bool PresentHoverOverlay(RectangleF workAreaBounds)
    {
        if (_hoverOverlayUnavailable)
        {
            _hoverOverlay.HideOverlay();
            _lastHoverOverlayBounds = null;
            return true;
        }

        if (_selecting || _dragStarted || _boxTransformActive?.Invoke() == true ||
            !DesktopIconHoverPolicy.CanHoverDesktopIcon(IsPointerOverBox(Forms.Cursor.Position)) ||
            !_runtime.State.Settings.Appearance.HoverFeedback || _hoveredItemKey is null)
        {
            if (IsPointerOverBox(Forms.Cursor.Position))
            {
                SetHoveredItem(null);
            }
            _hoverOverlay.HideOverlay();
            _lastHoverOverlayBounds = null;
            return true;
        }

        var item = FindHoveredItem();
        if (item is null)
        {
            _hoverOverlay.HideOverlay();
            _lastHoverOverlayBounds = null;
            return true;
        }

        EnsureLayerBitmap();
        RectangleF currentBounds;
        using (var measureGraphics = Graphics.FromImage(_layerBitmap!))
        {
            ConfigureLayerGraphics(measureGraphics, workAreaBounds, fastRender: false);
            currentBounds = GetHoverVisualBounds(measureGraphics, item);
            measureGraphics.ResetTransform();
        }

        var surfaceBounds = new RectangleF(
            0,
            0,
            (float)(ClientSize.Width / Math.Max(_scale, 0.01d)),
            (float)(ClientSize.Height / Math.Max(_scale, 0.01d)));
        currentBounds = RectangleF.Intersect(surfaceBounds, currentBounds);
        if (currentBounds.Width <= 0 || currentBounds.Height <= 0)
        {
            _hoverOverlay.HideOverlay();
            _lastHoverOverlayBounds = null;
            return true;
        }

        var requestedBounds = _lastHoverOverlayBounds is { } previousBounds
            ? RectangleF.Union(previousBounds, currentBounds)
            : currentBounds;
        if (!_hoverOverlay.Present(
                requestedBounds,
                _scale,
                (graphics, alignedBounds) => DrawHoverOverlay(graphics, alignedBounds),
                out var diagnostic))
        {
            _hoverOverlay.HideOverlay();
            _lastHoverOverlayBounds = null;
            _hoverOverlayUnavailable = true;
            DiagnosticLog.Error(
                $"Desktop icon hover overlay presentation failed monitor={_monitor.Id}: {diagnostic}",
                new InvalidOperationException(diagnostic));
            return false;
        }

        var itemKey = item.Item.Key.ToString();
        if (_selection.Contains(itemKey))
        {
            _expandedItemHitBounds[itemKey] = currentBounds;
        }
        else
        {
            _expandedItemHitBounds.Remove(itemKey);
        }
        _lastHoverOverlayBounds = currentBounds;
        return true;
    }

    private DesktopIconGeometry? FindHoveredItem() =>
        _items.FirstOrDefault(item => string.Equals(
            item.Item.Key.ToString(),
            _hoveredItemKey,
            StringComparison.OrdinalIgnoreCase));

    private RectangleF GetHoverVisualBounds(Graphics graphics, DesktopIconGeometry item)
    {
        var iconBounds = GetIconBounds(item.Bounds);
        var selected = _selection.Contains(item.Item.Key.ToString());
        using var font = ResolveIconLabelFont();
        var textBounds = GetItemTextBounds(
            graphics,
            item.Item.DisplayName,
            item.Bounds,
            iconBounds,
            font,
            selected: DesktopIconLabelDisplayPolicy.ShowsFullLabel(selected, isHovered: true));
        var textHitBounds = GetTextHitBounds(graphics, item.Item.DisplayName, textBounds, font);
        return GetItemVisualBounds(iconBounds, textHitBounds);
    }

    private void DrawHoverOverlay(Graphics graphics, RectangleF overlayBounds)
    {
        var item = FindHoveredItem();
        if (item is null)
        {
            return;
        }

        graphics.CompositingQuality = CompositingQuality.HighSpeed;
        graphics.SmoothingMode = SmoothingMode.HighSpeed;
        graphics.InterpolationMode = InterpolationMode.Low;
        graphics.PixelOffsetMode = PixelOffsetMode.HighSpeed;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        graphics.TextContrast = 4;
        graphics.Transform = new Matrix(
            (float)_scale,
            0,
            0,
            (float)_scale,
            -(float)(overlayBounds.X * _scale),
            -(float)(overlayBounds.Y * _scale));
        graphics.SetClip(overlayBounds, CombineMode.Replace);

        var selectionColor = ParseColor(
            _runtime.State.Settings.Appearance.SelectionColor,
            Color.FromArgb(74, 91, 177));
        var hoverColor = DesktopItemVisualStyle.Brighten(selectionColor);
        var iconBounds = GetIconBounds(item.Bounds);
        var selected = _selection.Contains(item.Item.Key.ToString());
        using var font = ResolveIconLabelFont();
        using var textFormat = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Near,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.LineLimit
        };
        var textBounds = GetItemTextBounds(
            graphics,
            item.Item.DisplayName,
            item.Bounds,
            iconBounds,
            font,
            selected: DesktopIconLabelDisplayPolicy.ShowsFullLabel(selected, isHovered: true));
        var textHitBounds = GetTextHitBounds(graphics, item.Item.DisplayName, textBounds, font);
        var visualBounds = GetItemVisualBounds(iconBounds, textHitBounds);
        using var fill = new SolidBrush(Color.FromArgb(DesktopItemVisualStyle.HoverFillAlpha, hoverColor));
        using var border = new Pen(Color.FromArgb(DesktopItemVisualStyle.HoverBorderAlpha, hoverColor), 1);
        using var path = RoundedRectangle(visualBounds, DesktopItemVisualStyle.SelectionCornerRadius(_iconSize));
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);

        var bitmap = GetDesktopIconBitmap(
            item.Item,
            Math.Clamp((int)Math.Round(_iconSize * _scale), 16, 256))
            ?? ShellIconProvider.GetGenericFileIcon();
        if (bitmap is not null)
        {
            DrawImageWithAlpha(graphics, bitmap, iconBounds, 1f);
        }

        using var textBrush = new SolidBrush(Color.FromArgb(248, Color.White));
        using var shadowBrush = new SolidBrush(Color.FromArgb(190, Color.Black));
        var shadowBounds = textBounds;
        shadowBounds.Offset(1, 1);
        graphics.DrawString(item.Item.DisplayName, font, shadowBrush, shadowBounds, textFormat);
        graphics.DrawString(item.Item.DisplayName, font, textBrush, textBounds, textFormat);
        graphics.ResetTransform();
    }

    private void DrawDragOverlay(Graphics graphics, RectangleF overlayBounds)
    {
        graphics.CompositingQuality = CompositingQuality.HighSpeed;
        graphics.SmoothingMode = SmoothingMode.HighSpeed;
        graphics.InterpolationMode = InterpolationMode.Low;
        graphics.PixelOffsetMode = PixelOffsetMode.HighSpeed;
        graphics.Transform = new Matrix(
            (float)_scale,
            0,
            0,
            (float)_scale,
            -(float)(overlayBounds.X * _scale),
            -(float)(overlayBounds.Y * _scale));
        graphics.SetClip(overlayBounds, CombineMode.Replace);
        DrawDynamicDragVisuals(graphics, overlayBounds);
        graphics.ResetTransform();
    }

    private void DrawDynamicDragVisuals(Graphics graphics, RectangleF clipBounds)
    {
        if (_selecting)
        {
            DrawMarqueeOverlay(graphics);
        }
        if (_overRecycleBin)
        {
            DrawRecycleBinHighlight(graphics);
        }
        DrawBoxItemDropPreview(graphics);
        if (_dragStarted || _boxDropItemKeys.Count > 0 ||
            _boxTransformActive?.Invoke() == true)
        {
            _dragBoxRenderer?.Invoke(graphics, clipBounds);
        }
        // Ghost cards float above the box visuals so the dragged item stays
        // visible while the pointer is over a box (the box forwards its drag
        // state to this surface, which owns all dynamic rendering).
        if (_dragStarted)
        {
            DrawFloatingDragPreview(graphics);
        }
        if (_externalDragPaths is { Length: > 0 })
        {
            DrawExternalDragPreview(graphics);
        }
    }

    private RectangleF? GetDragOverlayBounds(RectangleF workAreaBounds)
    {
        RectangleF? bounds = null;
        if (_selecting)
        {
            // Every item selected by a marquee is inside this rectangle. Keep
            // the mutable surface limited to the rectangle itself instead of
            // scanning and unioning every selected label on every mouse move.
            // The small inset also leaves room for the selection border.
            if (!_selectionRectangle.IsEmpty)
            {
                bounds = UnionVisualBounds(
                    bounds,
                    RectangleF.Inflate(_selectionRectangle, 8, 8));
            }
        }

        if (_overRecycleBin &&
            _items.FirstOrDefault(item => IsRecycleBinItem(item.Item)) is { } recycleBin)
        {
            bounds = UnionVisualBounds(
                bounds,
                RectangleF.Inflate(GetIconBounds(recycleBin.Bounds), 12, 12));
        }
        if (_externalDragPaths is { Length: > 0 })
        {
            bounds = UnionVisualBounds(
                bounds,
                new RectangleF(_externalDragPointer.X - 34, _externalDragPointer.Y + 2, 150, 64));
        }

        if (_dragStarted)
        {
            var anchor = _items.FirstOrDefault(item =>
                string.Equals(item.Item.Key.ToString(), _dragAnchorKey, StringComparison.OrdinalIgnoreCase));
            if (anchor is not null)
            {
                var anchorIconBounds = GetIconBounds(anchor.Bounds);
                var anchorIconOffset = new PointF(
                    anchorIconBounds.X - anchor.Bounds.X,
                    anchorIconBounds.Y - anchor.Bounds.Y);
                var floatingAnchorCellTopLeft = new PointF(
                    _dragPointer.X - _dragIconGrabOffset.X - anchorIconOffset.X,
                    _dragPointer.Y - _dragIconGrabOffset.Y - anchorIconOffset.Y);
                var labelAllowance = Math.Max(48f, _verticalSpacing * 3f);
                foreach (var entry in _items.Where(item => _dragItemKeys.Contains(item.Item.Key.ToString())))
                {
                    var floatingBounds = new RectangleF(
                        floatingAnchorCellTopLeft.X + entry.Bounds.X - anchor.Bounds.X,
                        floatingAnchorCellTopLeft.Y + entry.Bounds.Y - anchor.Bounds.Y,
                        entry.Bounds.Width,
                        entry.Bounds.Height + labelAllowance);
                    bounds = UnionVisualBounds(bounds, RectangleF.Inflate(floatingBounds, 12, 12));
                }
            }
        }

        if (_boxDropItemKeys.Count > 0)
        {
            var grid = CreateCurrentGrid();
            foreach (var cell in _boxDropPreviewCells.Values)
            {
                bounds = UnionVisualBounds(
                    bounds,
                    RectangleF.Inflate(GetIconBounds(GetCellBounds(grid, cell)), 10, 10));
            }
            if (_boxDragPointer is { } pointer)
            {
                bounds = UnionVisualBounds(bounds, new RectangleF(pointer.X - 40, pointer.Y - 40, 112, 112));
            }
        }

        if ((!_selecting || _dragStarted || _boxDropItemKeys.Count > 0 ||
             _boxTransformActive?.Invoke() == true) &&
            _boxDynamicBounds?.Invoke() is { } boxBounds)
        {
            bounds = UnionVisualBounds(bounds, RectangleF.Inflate(boxBounds, 10, 10));
        }

        if (bounds is not { } visualBounds)
        {
            return null;
        }

        var surfaceBounds = new RectangleF(
            0,
            0,
            (float)(ClientSize.Width / Math.Max(_scale, 0.01d)),
            (float)(ClientSize.Height / Math.Max(_scale, 0.01d)));
        var clippedBounds = RectangleF.Intersect(surfaceBounds, visualBounds);
        return clippedBounds.Width > 0 && clippedBounds.Height > 0
            ? clippedBounds
            : RectangleF.Intersect(workAreaBounds, visualBounds);
    }

    private static RectangleF? UnionVisualBounds(RectangleF? current, RectangleF candidate)
    {
        if (candidate.Width <= 0 || candidate.Height <= 0)
        {
            return current;
        }
        return current is { } existing ? RectangleF.Union(existing, candidate) : candidate;
    }

    private void DrawSettledLayer(
        Graphics graphics,
        RectangleF workAreaBounds,
        bool includeBoxDropPreview = true,
        IReadOnlySet<string>? selectedItemKeys = null,
        bool includeSelectionRectangle = true,
        bool includeHoverFeedback = false)
    {
        graphics.Clear(Color.Transparent);
        graphics.CompositingMode = CompositingMode.SourceOver;
        ConfigureLayerGraphics(graphics, workAreaBounds, fastRender: false);
        using var hitTestBackground = new SolidBrush(Color.FromArgb(DesktopHitTestAlpha, Color.Black));
        graphics.FillRectangle(hitTestBackground, workAreaBounds);
        DrawDesktopItems(
            graphics,
            selectedItemKeys,
            includeSelectionRectangle,
            includeHoverFeedback);
        if (includeBoxDropPreview)
        {
            DrawBoxItemDropPreview(graphics);
        }
        _boxRenderer?.Invoke(graphics, workAreaBounds);
        graphics.ResetTransform();
    }

    private void ConfigureLayerGraphics(
        Graphics graphics,
        RectangleF workAreaBounds,
        bool fastRender)
    {
        graphics.SmoothingMode = fastRender ? SmoothingMode.HighSpeed : SmoothingMode.AntiAlias;
        graphics.InterpolationMode = fastRender ? InterpolationMode.Low : InterpolationMode.HighQualityBicubic;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        graphics.ScaleTransform((float)_scale, (float)_scale);
        graphics.SetClip(workAreaBounds, CombineMode.Replace);
    }

    private void RebuildGeometry()
    {
        _items.Clear();
        _expandedItemHitBounds.Clear();
        var desktopViewState = DesktopIconPositionService.GetDesktopViewState();
        SynchronizeNativeMetrics(desktopViewState);
        if (!_monitor.IsPrimary)
        {
            return;
        }

        var desktopItems = _runtime.GetUnassignedDesktopItems().ToArray();
        var grid = CreateCurrentGrid();
        var gridTopology = new DesktopGridTopology(grid.ColumnCount, grid.RowCount);
        var occupiedCells = new HashSet<GridCell>();
        var placedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var useStoredLayout = !desktopViewState.AutoArrange &&
            !_runtime.IsDesktopSortCommandPending;
        var storedLayout = useStoredLayout
            ? _runtime.State.DesktopIconLayout
            : new Dictionary<string, DesktopIconLayoutSnapshot>(StringComparer.OrdinalIgnoreCase);
        var storedEntries = desktopItems
            .Select(item => (Item: item, Placement: GetStoredLayoutPlacement(item, storedLayout)))
            .Where(entry => entry.Placement is not null)
            .ToArray();
        var previousGridTopology = _previousGridTopology;
        var gridCapacityChanged = useStoredLayout &&
            previousGridTopology is { } previousTopology &&
            previousTopology != gridTopology;
        var reflowedCells = gridCapacityChanged
            ? DesktopIconGridLayout.Reflow(
                storedEntries.Select(entry => new DesktopIconGridItem(
                    entry.Item.Key.ToString(),
                    new DesktopIconGridCell(entry.Placement!.Column, entry.Placement.Row))),
                grid.ColumnCount,
                grid.RowCount)
            : null;
        if (reflowedCells is not null)
        {
            DiagnosticLog.Info(
                $"Desktop icon grid reflow monitor={_monitor.Id} " +
                $"from={previousGridTopology!.Value.ColumnCount}x{previousGridTopology.Value.RowCount} " +
                $"to={gridTopology.ColumnCount}x{gridTopology.RowCount} " +
                $"items={storedEntries.Length} order=column-major");
        }

        // A capacity change caused by icon zoom preserves the manual reading
        // sequence while refilling the new grid. Ordinary refreshes retain
        // each exact manual cell.
        foreach (var entry in storedEntries
                     .OrderBy(entry => entry.Placement!.Column)
                     .ThenBy(entry => entry.Placement!.Row)
                     .ThenBy(entry => entry.Item.Key.ToString(), StringComparer.OrdinalIgnoreCase))
        {
            var placement = entry.Placement!;
            GridCell? cell;
            if (reflowedCells is not null)
            {
                cell = reflowedCells.TryGetValue(entry.Item.Key.ToString(), out var reflowedCell)
                    ? new GridCell(reflowedCell.Column, reflowedCell.Row)
                    : null;
            }
            else
            {
                cell = FindNearestFreeCell(
                    new GridCell(placement.Column, placement.Row),
                    grid,
                    occupiedCells);
            }
            if (cell is not { } storedCell)
            {
                continue;
            }

            occupiedCells.Add(storedCell);
            placedKeys.Add(entry.Item.Key.ToString());
            _items.Add(new DesktopIconGeometry(
                entry.Item,
                GetCellBounds(grid, storedCell),
                storedCell));
        }

        // Newly created desktop entries fill the next vacant grid cell. They
        // are then captured in the layout below, so later refreshes do not
        // reorder items simply because the active sort property still exists.
        foreach (var item in OrderDesktopItems(
                     desktopItems.Where(item => !placedKeys.Contains(item.Key.ToString())).ToArray(),
                     desktopViewState.Sort))
        {
            var cell = FindFirstFreeCell(grid, occupiedCells);
            if (cell is not { } automaticCell)
            {
                break;
            }
            occupiedCells.Add(automaticCell);
            _items.Add(new DesktopIconGeometry(
                item,
                GetCellBounds(grid, automaticCell),
                automaticCell));
        }

        if (useStoredLayout)
        {
            PersistCurrentLayoutIfNeeded(storedLayout);
        }

        _previousGridTopology = gridTopology;

        // Hit rectangles are stable for the lifetime of this geometry. Cache
        // them once so marquee movement does not repeatedly recompute the
        // monitor work area and icon/text rectangles for every item.
        var workAreaBounds = GetDesktopWorkAreaBounds();
        // Measure on the same logical (96-DPI) canvas the layer renderer
        // uses. A bare Bitmap inherits the monitor DPI on PerMonitorV2
        // threads, which would inflate every label metric by the scale factor.
        using var measureBitmap = DesktopLayerBitmapFactory.Create(1, 1);
        using (var measureGraphics = Graphics.FromImage(measureBitmap))
        using (var measureFont = ResolveIconLabelFont())
        {
            foreach (var entry in _items)
            {
                entry.HitBounds = CalculateItemHitBounds(
                    entry.Bounds,
                    workAreaBounds,
                    measureGraphics,
                    measureFont,
                    entry.Item.DisplayName);
            }
        }
        DiagnosticLog.Info(
            $"Icon geometry iconSize={_iconSize:0.#} spacing={_horizontalSpacing:0.#}x{_verticalSpacing:0.#} " +
            $"workArea={workAreaBounds.Width:0.#}x{workAreaBounds.Height:0.#}");
        foreach (var entry in _items.Take(30))
        {
            DiagnosticLog.Info(
                $"Icon geometry name={entry.Item.DisplayName} cell={entry.Cell.Column},{entry.Cell.Row} " +
                $"hit={entry.HitBounds.X:0.#},{entry.HitBounds.Y:0.#},{entry.HitBounds.Width:0.#}x{entry.HitBounds.Height:0.#}");
        }

        var visibleKeys = _items.Select(item => item.Item.Key.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _selection.RemoveWhere(key => !visibleKeys.Contains(key));
        _selectionBase.RemoveWhere(key => !visibleKeys.Contains(key));
        if (_hoveredItemKey is not null && !visibleKeys.Contains(_hoveredItemKey))
        {
            _hoveredItemKey = null;
        }
    }

    private DesktopIconLayoutSnapshot? GetStoredLayoutPlacement(
        DesktopItemRef item,
        IReadOnlyDictionary<string, DesktopIconLayoutSnapshot> layout)
    {
        if (!layout.TryGetValue(item.Key.ToString(), out var placement) ||
            placement is null ||
            (!string.IsNullOrWhiteSpace(placement.MonitorId) &&
             !string.Equals(placement.MonitorId, _monitor.Id, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        return placement;
    }

    private void PersistCurrentLayoutIfNeeded(
        IReadOnlyDictionary<string, DesktopIconLayoutSnapshot> storedLayout)
    {
        if (_persistingLayout)
        {
            return;
        }

        var layout = _items.ToDictionary(
            item => item.Item.Key.ToString(),
            item => new DesktopIconLayoutSnapshot
            {
                MonitorId = _monitor.Id,
                Column = item.Cell.Column,
                Row = item.Cell.Row
            },
            StringComparer.OrdinalIgnoreCase);
        var needsSnapshot = layout.Count != storedLayout.Count ||
            layout.Any(entry =>
                !storedLayout.TryGetValue(entry.Key, out var stored) ||
                !string.Equals(stored.MonitorId, entry.Value.MonitorId, StringComparison.OrdinalIgnoreCase) ||
                stored.Column != entry.Value.Column ||
                stored.Row != entry.Value.Row);
        if (!needsSnapshot)
        {
            return;
        }

        // Rebuilding geometry must never start another surface refresh while
        // it is already rendering. Saving this initial snapshot is enough;
        // the geometry on screen already reflects the same cells.
        _persistingLayout = true;
        try
        {
            _runtime.SetDesktopIconLayout(layout, refreshWorkspace: false);
        }
        finally
        {
            _persistingLayout = false;
        }
    }

    private DesktopGrid CreateDesktopGrid(RectangleF desktopBounds)
    {
        var metrics = DesktopIconGridLayout.CalculateSurfaceMetrics(
            desktopBounds.Width,
            desktopBounds.Height,
            _horizontalSpacing,
            _verticalSpacing);
        var diagnostic =
            $"Desktop icon grid monitor={_monitor.Id} bounds={desktopBounds.Width:0.###}x{desktopBounds.Height:0.###} " +
            $"native={_horizontalSpacing:0.###}x{_verticalSpacing:0.###} " +
            $"grid={metrics.ColumnCount}x{metrics.RowCount} spacing={metrics.HorizontalSpacing:0.###}x{metrics.VerticalSpacing:0.###}";
        if (!string.Equals(diagnostic, _lastGridDiagnostic, StringComparison.Ordinal))
        {
            _lastGridDiagnostic = diagnostic;
            DiagnosticLog.Info(diagnostic);
        }
        return new DesktopGrid(
            desktopBounds,
            (float)metrics.HorizontalSpacing,
            (float)metrics.VerticalSpacing,
            metrics.ColumnCount,
            metrics.RowCount);
    }

    private static RectangleF GetCellBounds(DesktopGrid grid, GridCell cell) => new(
        grid.Bounds.Left + cell.Column * grid.HorizontalSpacing,
        grid.Bounds.Top + cell.Row * grid.VerticalSpacing,
        grid.HorizontalSpacing,
        grid.VerticalSpacing);

    private static GridCell? FindFirstFreeCell(
        DesktopGrid grid,
        IReadOnlySet<GridCell> occupied)
    {
        for (var column = 0; column < grid.ColumnCount; column++)
        {
            for (var row = 0; row < grid.RowCount; row++)
            {
                var cell = new GridCell(column, row);
                if (!occupied.Contains(cell))
                {
                    return cell;
                }
            }
        }
        return null;
    }

    private static GridCell? FindNearestFreeCell(
        GridCell requested,
        DesktopGrid grid,
        IReadOnlySet<GridCell> occupied)
    {
        if (grid.ColumnCount == 0 || grid.RowCount == 0)
        {
            return null;
        }

        var column = Math.Clamp(requested.Column, 0, grid.ColumnCount - 1);
        var row = Math.Clamp(requested.Row, 0, grid.RowCount - 1);
        var candidates = Enumerable
            .Range(0, grid.ColumnCount)
            .SelectMany(candidateColumn => Enumerable.Range(0, grid.RowCount)
                .Select(candidateRow => new GridCell(candidateColumn, candidateRow)))
            .OrderBy(candidate => Math.Abs(candidate.Column - column) + Math.Abs(candidate.Row - row))
            .ThenBy(candidate => candidate.Row)
            .ThenBy(candidate => candidate.Column);
        foreach (var candidate in candidates)
        {
            if (!occupied.Contains(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    private void DrawDesktopItems(
        Graphics graphics,
        IReadOnlySet<string>? selectedItemKeys = null,
        bool includeSelectionRectangle = true,
        bool includeHoverFeedback = false)
    {
        selectedItemKeys ??= _selection;
        using var font = ResolveIconLabelFont();
        using var textFormat = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Near,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.LineLimit
        };
        using var textBrush = new SolidBrush(Color.FromArgb(248, Color.White));
        using var shadowBrush = new SolidBrush(Color.FromArgb(190, Color.Black));
        var selectionColor = ParseColor(_runtime.State.Settings.Appearance.SelectionColor, Color.FromArgb(74, 91, 177));
        var previewGrid = _boxDropPreviewCells.Count > 0
            ? CreateCurrentGrid()
            : (DesktopGrid?)null;
        // Icon backgrounds and glyphs are painted first, then every label is
        // painted afterwards so an expanded two-line or full name is never
        // covered by the icon pixels of the row below.
        var labelBoundsByKey = new Dictionary<string, RectangleF>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _items.OrderBy(item => IsRaisedVisual(item.Item.Key.ToString(), selectedItemKeys)))
        {
            var itemKey = entry.Item.Key.ToString();
            var selected = selectedItemKeys.Contains(itemKey);
            var dragging = _dragStarted && _dragItemKeys.Contains(itemKey);
            if (dragging || _boxDropItemKeys.Contains(itemKey))
            {
                // The selected group is rendered once as a floating preview
                // below. Keeping the source pixels here would look like a
                // copy and was the source of the old duplicate-icon effect.
                _expandedItemHitBounds.Remove(itemKey);
                continue;
            }

            var drawBounds = entry.Bounds;
            if (previewGrid is { } grid &&
                _boxDropPreviewCells.TryGetValue(itemKey, out var boxPreviewCell))
            {
                drawBounds = GetCellBounds(grid, boxPreviewCell);
            }
            var hoverTarget =
                _runtime.State.Settings.Appearance.HoverFeedback &&
                string.Equals(_hoveredItemKey, itemKey, StringComparison.OrdinalIgnoreCase);
            var hovered = includeHoverFeedback && hoverTarget;
            var iconBounds = GetIconBounds(drawBounds);
            var textBounds = GetItemTextBounds(
                graphics,
                entry.Item.DisplayName,
                drawBounds,
                iconBounds,
                font,
                DesktopIconLabelDisplayPolicy.ShowsFullLabel(selected, hovered));
            labelBoundsByKey[itemKey] = textBounds;
            var textHitBounds = GetTextHitBounds(
                graphics,
                entry.Item.DisplayName,
                textBounds,
                font);
            var visualBounds = GetItemVisualBounds(iconBounds, textHitBounds);
            if (selected)
            {
                _expandedItemHitBounds[itemKey] = visualBounds;
            }
            else
            {
                _expandedItemHitBounds.Remove(itemKey);
            }
            if (hovered)
            {
                // Hover is the active pointer feedback, including for an
                // already-selected item. It intentionally takes precedence
                // so the hover treatment remains brighter than selection.
                var hoverColor = DesktopItemVisualStyle.Brighten(selectionColor);
                using var fill = new SolidBrush(Color.FromArgb(DesktopItemVisualStyle.HoverFillAlpha, hoverColor));
                using var border = new Pen(Color.FromArgb(DesktopItemVisualStyle.HoverBorderAlpha, hoverColor), 1);
                using var path = RoundedRectangle(visualBounds, DesktopItemVisualStyle.SelectionCornerRadius(_iconSize));
                graphics.FillPath(fill, path);
                graphics.DrawPath(border, path);
            }
            else if (selected)
            {
                using var fill = new SolidBrush(Color.FromArgb(DesktopItemVisualStyle.SelectedFillAlpha, selectionColor));
                using var path = RoundedRectangle(visualBounds, DesktopItemVisualStyle.SelectionCornerRadius(_iconSize));
                graphics.FillPath(fill, path);
            }

            var bitmap = GetDesktopIconBitmap(
                entry.Item,
                Math.Clamp((int)Math.Round(_iconSize * _scale), 16, 256))
                ?? ShellIconProvider.GetGenericFileIcon();
            if (bitmap is not null)
            {
                DrawImageWithAlpha(graphics, bitmap, iconBounds, 1f);
            }
        }

        foreach (var entry in _items.OrderBy(item => IsRaisedVisual(item.Item.Key.ToString(), selectedItemKeys)))
        {
            var itemKey = entry.Item.Key.ToString();
            if ((_dragStarted && _dragItemKeys.Contains(itemKey)) || _boxDropItemKeys.Contains(itemKey))
            {
                continue;
            }
            if (!labelBoundsByKey.TryGetValue(itemKey, out var textBounds))
            {
                continue;
            }
            var shadowBounds = textBounds;
            shadowBounds.Offset(1, 1);
            graphics.DrawString(entry.Item.DisplayName, font, shadowBrush, shadowBounds, textFormat);
            graphics.DrawString(entry.Item.DisplayName, font, textBrush, textBounds, textFormat);
        }

        if (includeSelectionRectangle && _selecting && !_selectionRectangle.IsEmpty)
        {
            using var fill = new SolidBrush(Color.FromArgb(42, selectionColor));
            using var border = new Pen(Color.FromArgb(190, selectionColor), 1);
            graphics.FillRectangle(fill, _selectionRectangle);
            graphics.DrawRectangle(border, _selectionRectangle.X, _selectionRectangle.Y,
                Math.Max(1, _selectionRectangle.Width), Math.Max(1, _selectionRectangle.Height));
        }
    }

    private bool IsDynamicMarqueeSelection(DesktopIconGeometry entry)
    {
        return _selecting &&
            _selection.Contains(entry.Key) &&
            !_selectionBase.Contains(entry.Key);
    }

    private void DrawMarqueeOverlay(Graphics graphics)
    {
        var selectionColor = ParseColor(
            _runtime.State.Settings.Appearance.SelectionColor,
            Color.FromArgb(74, 91, 177));
        // The settled layer already contains every icon and label. During a
        // marquee only the translucent selection treatment changes, so redraw
        // the highlight rectangles rather than fetching and painting all
        // selected bitmaps/text again on every pointer update.
        using var highlightFill = new SolidBrush(Color.FromArgb(DesktopItemVisualStyle.SelectedFillAlpha, selectionColor));
        using var highlightBorder = new Pen(Color.FromArgb(190, selectionColor), 1f);
        foreach (var entry in _items)
        {
            if (!IsDynamicMarqueeSelection(entry))
            {
                continue;
            }

            var padding = DesktopItemVisualStyle.SelectionPadding(_iconSize);
            var visualBounds = RectangleF.Inflate(
                entry.HitBounds.IsEmpty ? GetItemHitBounds(entry) : entry.HitBounds,
                padding,
                padding);
            using var path = RoundedRectangle(visualBounds, DesktopItemVisualStyle.SelectionCornerRadius(_iconSize));
            graphics.FillPath(highlightFill, path);
            graphics.DrawPath(highlightBorder, path);
        }

        if (!_selectionRectangle.IsEmpty)
        {
            using var fill = new SolidBrush(Color.FromArgb(42, selectionColor));
            using var border = new Pen(Color.FromArgb(190, selectionColor), 1);
            graphics.FillRectangle(fill, _selectionRectangle);
            graphics.DrawRectangle(
                border,
                _selectionRectangle.X,
                _selectionRectangle.Y,
                Math.Max(1, _selectionRectangle.Width),
                Math.Max(1, _selectionRectangle.Height));
        }
    }

    private Font ResolveIconLabelFont()
    {
        var appearance = _runtime.State.Settings.Appearance;
        var family = appearance.IconLabelFontFamily;
        var size = appearance.IconLabelFontSize;
        if (!string.IsNullOrWhiteSpace(family) && size > 0)
        {
            try
            {
                return new Font(family, (float)size, FontStyle.Regular, GraphicsUnit.Point);
            }
            catch
            {
                // Fall through to the system icon-title font.
            }
        }

        var systemIconFont = SystemFonts.IconTitleFont;
        return systemIconFont is null
            ? new Font("Segoe UI", 9, FontStyle.Regular, GraphicsUnit.Point)
            : new Font(
                systemIconFont.FontFamily,
                systemIconFont.Size,
                FontStyle.Regular,
                GraphicsUnit.Point);
    }

    private Bitmap? GetDesktopIconBitmap(DesktopItemRef item, int pixelSize)
    {
        var key = (item.ParsingName, pixelSize);
        if (_desktopIconCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var source = _runtime.IconProvider.GetIcon(item.ParsingName, pixelSize);
        if (source is null)
        {
            // Shell image retrieval can temporarily fail while Explorer
            // rebuilds its image list, so leave misses uncached for retry.
            return null;
        }

        try
        {
            cached = new Bitmap(source);
            _desktopIconCache[key] = cached;
            return cached;
        }
        catch
        {
            return null;
        }
    }

    private int ClearDesktopIconCache()
    {
        var count = _desktopIconCache.Count;
        foreach (var bitmap in _desktopIconCache.Values)
        {
            bitmap.Dispose();
        }
        _desktopIconCache.Clear();
        return count;
    }

    // The grabbed icons stay attached to the pointer, like the native
    // desktop ghost. Only the icons and labels follow the cursor - no
    // insertion markers and no grid reflow preview.
    private void DrawFloatingDragPreview(Graphics graphics)
    {
        var anchor = _items.FirstOrDefault(item =>
            string.Equals(item.Item.Key.ToString(), _dragAnchorKey, StringComparison.OrdinalIgnoreCase));
        if (anchor is null)
        {
            return;
        }

        using var font = ResolveIconLabelFont();
        using var textFormat = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Near,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.LineLimit
        };
        using var floatingText = new SolidBrush(Color.FromArgb(238, Color.White));
        using var floatingShadow = new SolidBrush(Color.FromArgb(180, Color.Black));
        var anchorIconBounds = GetIconBounds(anchor.Bounds);
        var anchorIconOffset = new PointF(
            anchorIconBounds.X - anchor.Bounds.X,
            anchorIconBounds.Y - anchor.Bounds.Y);
        var floatingAnchorCellTopLeft = new PointF(
            _dragPointer.X - _dragIconGrabOffset.X - anchorIconOffset.X,
            _dragPointer.Y - _dragIconGrabOffset.Y - anchorIconOffset.Y);
        foreach (var entry in _items.Where(item => _dragItemKeys.Contains(item.Item.Key.ToString())))
        {
            var floatingBounds = new RectangleF(
                floatingAnchorCellTopLeft.X + entry.Bounds.X - anchor.Bounds.X,
                floatingAnchorCellTopLeft.Y + entry.Bounds.Y - anchor.Bounds.Y,
                entry.Bounds.Width,
                entry.Bounds.Height);
            var iconBounds = GetIconBounds(floatingBounds);
            var textBounds = GetItemTextBounds(
                graphics,
                entry.Item.DisplayName,
                floatingBounds,
                iconBounds,
                font,
                selected: true);
            var bitmap = GetDesktopIconBitmap(
                entry.Item,
                Math.Clamp((int)Math.Round(_iconSize * _scale), 16, 256))
                ?? ShellIconProvider.GetGenericFileIcon();
            if (bitmap is not null)
            {
                DrawImageWithAlpha(graphics, bitmap, iconBounds, 0.86f);
            }
            var shadowBounds = textBounds;
            shadowBounds.Offset(1, 1);
            graphics.DrawString(entry.Item.DisplayName, font, floatingShadow, shadowBounds, textFormat);
            graphics.DrawString(entry.Item.DisplayName, font, floatingText, textBounds, textFormat);
        }
    }

    private void DrawBoxItemDropPreview(Graphics graphics)
    {
        if (_boxDropItemKeys.Count == 0 || _boxDragPointer is not { } pointer)
        {
            return;
        }

        var grid = CreateCurrentGrid();
        var accent = ParseColor(_runtime.State.Settings.Appearance.SelectionColor, Color.FromArgb(74, 91, 177));
        foreach (var key in _boxDropItemKeys)
        {
            if (!_boxDropPreviewCells.TryGetValue(key, out var cell))
            {
                continue;
            }
            var previewBounds = RectangleF.Inflate(GetIconBounds(GetCellBounds(grid, cell)), 4, 4);
            using var fill = new SolidBrush(Color.FromArgb(78, accent));
            using var border = new Pen(Color.FromArgb(238, accent), 2f);
            using var path = RoundedRectangle(previewBounds, 5);
            graphics.FillPath(fill, path);
            graphics.DrawPath(border, path);
        }

        DrawBoxDragFloatingPreview(graphics, pointer);
    }

    private void DrawBoxDragFloatingPreview(Graphics graphics, PointF pointer)
    {
        var primaryItem = _boxDragPrimaryKey is null
            ? null
            : _runtime.FindItemByKey(_boxDragPrimaryKey);
        var icon = primaryItem is null
            ? ShellIconProvider.GetGenericFileIcon()
            : GetDesktopIconBitmap(
                    primaryItem,
                    Math.Clamp((int)Math.Round(_iconSize * _scale), 16, 256))
                ?? ShellIconProvider.GetGenericFileIcon();
        using var font = ResolveIconLabelFont();
        DragGhostRenderer.Draw(
            graphics,
            pointer,
            icon,
            primaryItem?.DisplayName ?? _boxDropItemKeys.FirstOrDefault() ?? string.Empty,
            _boxDropItemKeys.Count,
            font);
    }

    private DesktopGrid CreateCurrentGrid()
    {
        return CreateDesktopGrid(GetDesktopGridBounds());
    }

    private RectangleF GetDesktopGridBounds()
    {
        var workArea = GetDesktopWorkAreaBounds();
        var horizontalInset = Math.Min(DesktopGridEdgeInset, workArea.Width / 2);
        var verticalInset = Math.Min(DesktopGridEdgeInset, workArea.Height / 2);
        return new RectangleF(
            workArea.X + horizontalInset,
            workArea.Y + verticalInset,
            Math.Max(0, workArea.Width - horizontalInset * 2),
            Math.Max(0, workArea.Height - verticalInset * 2));
    }

    private RectangleF GetDesktopWorkAreaBounds()
    {
        var scale = Math.Max(_scale, 0.01d);
        var surfaceBounds = new RectangleF(
            0,
            0,
            ClientSize.Width / (float)scale,
            ClientSize.Height / (float)scale);
        var workArea = MonitorCoordinateConverter.GetMonitorRelativeWorkArea(_monitor);
        var localWorkArea = new RectangleF(
            (float)workArea.X,
            (float)workArea.Y,
            (float)workArea.Width,
            (float)workArea.Height);
        var clippedWorkArea = RectangleF.Intersect(surfaceBounds, localWorkArea);
        return clippedWorkArea.Width > 0 && clippedWorkArea.Height > 0
            ? clippedWorkArea
            : surfaceBounds;
    }

    private static void DrawImageWithAlpha(Graphics graphics, Image image, RectangleF bounds, float alpha)
    {
        alpha = Math.Clamp(alpha, 0, 1);
        if (alpha >= 0.999f)
        {
            graphics.DrawImage(image, bounds);
            return;
        }

        using var attributes = new System.Drawing.Imaging.ImageAttributes();
        var matrix = new System.Drawing.Imaging.ColorMatrix { Matrix33 = alpha };
        attributes.SetColorMatrix(matrix, System.Drawing.Imaging.ColorMatrixFlag.Default,
            System.Drawing.Imaging.ColorAdjustType.Bitmap);
        graphics.DrawImage(image, Rectangle.Round(bounds), 0, 0, image.Width, image.Height,
            GraphicsUnit.Pixel, attributes);
    }

    private RectangleF GetIconBounds(RectangleF cellBounds) => new(
        cellBounds.X + (cellBounds.Width - _iconSize) / 2,
        cellBounds.Y + 3,
        _iconSize,
        _iconSize);

    private RectangleF GetItemTextBounds(
        Graphics graphics,
        string displayName,
        RectangleF itemBounds,
        RectangleF iconBounds,
        Font font,
        bool selected)
    {
        var textTop = iconBounds.Bottom + 3;
        var textWidth = Math.Max(0, itemBounds.Width - 4);
        // Labels may extend below the owning grid cell (Explorer-style):
        // unselected names use up to two lines, and a selected name is shown
        // in full instead of being truncated by the cell height.
        var maxTextBottom = GetDesktopWorkAreaBounds().Bottom - 3;
        var compactHeight = Math.Max(0, font.GetHeight(graphics) * CompactLabelLineCount + 2);
        var textHeight = selected
            ? Math.Min(
                MeasureFullLabelHeight(graphics, displayName, font, textWidth),
                Math.Max(0, maxTextBottom - textTop))
            : Math.Min(
                compactHeight,
                Math.Max(0, maxTextBottom - textTop));
        return new RectangleF(itemBounds.X + 2, textTop, textWidth, textHeight);
    }

    private static RectangleF GetTextHitBounds(
        Graphics graphics,
        string displayName,
        RectangleF textBounds,
        Font font)
    {
        if (textBounds.Width <= 0 || textBounds.Height <= 0 || string.IsNullOrWhiteSpace(displayName))
        {
            return RectangleF.Empty;
        }

        // Measure the label with the exact format used for drawing, so the
        // hit footprint matches the rendered text: real line count, widest
        // line, and ellipsis instead of the whole layout rectangle. The
        // layout rectangle is intentionally wide enough for wrapping, but it
        // must not make the surrounding blank desktop area behave like a
        // click target.
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Near,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.LineLimit
        };
        var measured = graphics.MeasureString(displayName, font, textBounds.Size, format);
        var width = Math.Min(textBounds.Width, Math.Max(font.Size, measured.Width));
        var height = Math.Min(textBounds.Height, Math.Max(0, measured.Height));
        return new RectangleF(
            textBounds.X + (textBounds.Width - width) / 2,
            textBounds.Y,
            width,
            height);
    }

    private static float MeasureFullLabelHeight(
        Graphics graphics,
        string displayName,
        Font font,
        float width)
    {
        if (width <= 0)
        {
            return 0;
        }

        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Near,
            Trimming = StringTrimming.None
        };
        return graphics.MeasureString(displayName, font, new SizeF(width, 100_000), format).Height + 2;
    }

    private void OnMouseDown(object? sender, Forms.MouseEventArgs eventArgs)
    {
        // A click on the desktop while an inline rename is open commits the
        // edit (the surface never activates, so Deactivate does not fire).
        _renameEditor?.CommitExternally();
        var point = ToDip(eventArgs.Location);
        var item = GetItemAt(point);
        if (item is not null)
        {
            _runtime.ActivateDesktopKeyboardInput();
            TryBeginSlowDoubleClickRename(item);
        }
        DiagnosticLog.Info(
            $"Icon surface mouse down monitor={_monitor.Id} button={eventArgs.Button} " +
            $"x={point.X:0} y={point.Y:0} item={item?.Item.DisplayName ?? "<desktop>"}");
        var hoverChanged = SetHoveredItem(item);
        if (eventArgs.Button == Forms.MouseButtons.Right)
        {
            if (item is null)
            {
                if (_selection.Count > 0)
                {
                    ClearSelection();
                }
                else if (hoverChanged)
                {
                    RequestHoverRender();
                }

                var screenPoint = PointToScreen(eventArgs.Location);
                if (DesktopWindowTools.ShowDesktopContextMenu(
                        _desktopListView,
                        screenPoint.X,
                        screenPoint.Y))
                {
                    _runtime.NotifyDesktopContextMenuOpened();
                }
                return;
            }

            var key = item.Item.Key.ToString();
            var selectionChanged = false;
            if (!_selection.Contains(key))
            {
                _selection.Clear();
                _selection.Add(key);
                selectionChanged = true;
            }
            if (selectionChanged)
            {
                PresentLayer();
            }
            else if (hoverChanged)
            {
                RequestHoverRender();
            }
            ShowItemContextMenu(item.Item, eventArgs.Location);
            return;
        }

        if (eventArgs.Button != Forms.MouseButtons.Left)
        {
            return;
        }

        // A click or marquee gesture on the ordinary desktop layer clears a
        // box's selection, while retaining the icon layer's own selection
        // state for Ctrl and drag operations.
        _runtime.ClearDesktopBoxSelection();

        if (item is null)
        {
            var additive = (Forms.Control.ModifierKeys & Forms.Keys.Control) != 0;
            _selectionBase.Clear();
            if (additive)
            {
                _selectionBase.UnionWith(_selection);
            }
            else
            {
                _selection.Clear();
            }
            _pressedItem = null;
            _dragStarted = false;
            _selecting = true;
            _selectionStart = point;
            _selectionRectangle = RectangleF.Empty;
            _dragBaseReady = false;
            Capture = true;
            // Do not block the mouse-down message with a monitor-sized base
            // render. The queued frame will build the base while the pointer
            // continues publishing the latest marquee rectangle.
            RequestDragRender();
            return;
        }

        var itemKey = item.Item.Key.ToString();
        var controlPressed = (Forms.Control.ModifierKeys & Forms.Keys.Control) != 0;
        if (controlPressed && _selection.Contains(itemKey))
        {
            _selection.Remove(itemKey);
            _pressedItem = null;
            _selecting = false;
            Capture = false;
            PresentLayer();
            return;
        }

        if (!controlPressed && !_selection.Contains(itemKey))
        {
            _selection.Clear();
        }
        _selection.Add(itemKey);
        _selectionBase.Clear();
        _pressedItem = item.Item;
        _pressPoint = point;
        _dragStarted = false;
        _selecting = false;
        Capture = true;
        PresentLayer();
    }

    private void OnMouseMove(object? sender, Forms.MouseEventArgs eventArgs)
    {
        var point = ToDip(eventArgs.Location);
        if (_selecting)
        {
            if (UpdateMarqueeSelection(point))
            {
                RequestDragRender();
            }
            return;
        }

        if (_pressedItem is null && !_dragStarted)
        {
            // Use the real screen cursor rather than the coordinates carried
            // by a possibly stale mouse message. Layered-window presents can
            // replay a move after the pointer has already advanced.
            var cursorClientPoint = PointToClient(Forms.Cursor.Position);
            if (ClientRectangle.Contains(cursorClientPoint))
            {
                _hoverReconcileTimer.Stop();
                _hoverReconcilePending = false;
                var currentItem = DesktopIconHoverPolicy.CanHoverDesktopIcon(
                    IsPointerOverBox(Forms.Cursor.Position))
                    ? GetHoverItemAt(ToDip(cursorClientPoint))
                    : null;
                if (SetHoveredItem(currentItem))
                {
                    RequestHoverRender();
                }
            }
            else
            {
                QueueHoverReconcile();
            }
        }
        if (_pressedItem is null || eventArgs.Button != Forms.MouseButtons.Left)
        {
            return;
        }

        if (!_dragStarted &&
            Math.Abs(point.X - _pressPoint.X) < 4 &&
            Math.Abs(point.Y - _pressPoint.Y) < 4)
        {
            return;
        }

        if (!_dragStarted)
        {
            BeginDesktopDrag(_pressedItem.Key.ToString());
            if (_dragStarted && TryStartDesktopOleDrag())
            {
                return;
            }
        }
        if (!_dragStarted)
        {
            return;
        }

        _dragPointer = point;
        _runtime.UpdateDesktopItemDropPreview(
            PointToScreen(eventArgs.Location),
            _dragItemKeys.ToArray(),
            out _dragPointerOverBox);
        // The grabbed icons follow the pointer through the small drag
        // overlay; only that overlay repaints per move.
        UpdateDesktopDragPreview(point);
        RequestDragRender();
    }

    private void OnMouseUp(object? sender, Forms.MouseEventArgs eventArgs)
    {
        var point = ToDip(eventArgs.Location);
        if (eventArgs.Button == Forms.MouseButtons.Left)
        {
            CommitPendingSlowDoubleClickRename();
        }
        if (_selecting && eventArgs.Button == Forms.MouseButtons.Left)
        {
            CancelPendingDragRender();
            _selecting = false;
            _selectionRectangle = RectangleF.Empty;
            _selectionBase.Clear();
            PresentLayer();
        }

        if (_dragStarted && eventArgs.Button == Forms.MouseButtons.Left)
        {
            var screenPoint = PointToScreen(eventArgs.Location);
            var itemKeys = _dragItemKeys.ToArray();
            _dragPointer = point;
            _runtime.UpdateDesktopItemDropPreview(screenPoint, itemKeys, out _dragPointerOverBox);
            UpdateDesktopDragPreview(point);
            CancelPendingDragRender();
            var droppedIntoBox = _runtime.TryDropDesktopItemsIntoBox(screenPoint, itemKeys);
            _runtime.ClearDesktopItemDropPreviews();
            if (!droppedIntoBox)
            {
                CommitDesktopDrop();
            }
            EndDesktopDrag();
            PresentLayer();
        }

        _pressedItem = null;
        Capture = false;
        DiagnosticLog.Info(
            $"Icon surface mouse up monitor={_monitor.Id} button={eventArgs.Button} " +
            $"x={point.X:0} y={point.Y:0} selected={_selection.Count}");
    }

    private void OnMouseLeave(object? sender, EventArgs eventArgs)
    {
        if (_pressedItem is not null || _selecting || _dragStarted)
        {
            return;
        }

        if (!DesktopIconHoverPolicy.CanHoverDesktopIcon(IsPointerOverBox(Forms.Cursor.Position)))
        {
            _hoverReconcileTimer.Stop();
            _hoverReconcilePending = false;
            if (SetHoveredItem(null))
            {
                RequestHoverRender();
            }
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
        _hoverReconcileTimer.Stop();
        _hoverReconcileTimer.Start();
    }

    private void OnHoverReconcileTimerTick(object? sender, EventArgs eventArgs)
    {
        _hoverReconcileTimer.Stop();
        if (!_hoverReconcilePending)
        {
            return;
        }

        ReconcileHoverAtCursor();
    }

    private void ReconcileHoverAtCursor()
    {
        _hoverReconcilePending = false;
        if (_pressedItem is not null || _selecting || _dragStarted || IsDisposed)
        {
            return;
        }

        var clientPoint = PointToClient(Forms.Cursor.Position);
        var currentItem = ClientRectangle.Contains(clientPoint) &&
            DesktopIconHoverPolicy.CanHoverDesktopIcon(IsPointerOverBox(Forms.Cursor.Position))
            ? GetHoverItemAt(ToDip(clientPoint))
            : null;
        if (SetHoveredItem(currentItem))
        {
            RequestHoverRender();
        }
    }

    private void OnMouseCaptureChanged(object? sender, EventArgs eventArgs)
    {
        if (!Capture && _dragStarted && !_desktopOleDragActive)
        {
            EndDesktopDrag();
            CancelPendingDragRender();
            PresentLayer();
        }
        else if (!Capture && !_selecting)
        {
            _pressedItem = null;
        }
    }

    private void OnDragOver(object? sender, Forms.DragEventArgs eventArgs)
    {
        if (TryGetDesktopIconDrag(eventArgs, out var desktopDrag))
        {
            if (!ReferenceEquals(desktopDrag.Source, this))
            {
                eventArgs.Effect = Forms.DragDropEffects.None;
                return;
            }

            var dropPoint = ToDip(PointToClient(new Point(eventArgs.X, eventArgs.Y)));
            var overRecycleBin = IsOverRecycleBin(dropPoint) && DraggedKeysAreFileSystemItems(desktopDrag);
            if (overRecycleBin != _overRecycleBin)
            {
                _overRecycleBin = overRecycleBin;
                if (!overRecycleBin)
                {
                    ClearBoxDropPreview();
                }
                RequestDragRender();
            }
            UpdateDesktopOleDropPreview(new Point(eventArgs.X, eventArgs.Y));
            eventArgs.Effect = _overRecycleBin
                ? (eventArgs.AllowedEffect & Forms.DragDropEffects.Move) != 0
                    ? Forms.DragDropEffects.Move
                    : Forms.DragDropEffects.None
                : (eventArgs.AllowedEffect & Forms.DragDropEffects.Copy) != 0
                    ? Forms.DragDropEffects.Copy
                    : Forms.DragDropEffects.None;
            return;
        }

        if (!TryGetVirtualBoxDrag(eventArgs, out var itemKeys, out _))
        {
            if (TryGetExternalFileDrop(eventArgs, out var externalPaths))
            {
                OnExternalFileDragOver(eventArgs, externalPaths);
                return;
            }
            ClearBoxDropPreview();
            if (_externalDragPaths is not null || _dragRenderPending)
            {
                DiagnosticLog.Info(
                    "Icon surface drag over: no payload recognized " +
                    $"external={_externalDragPaths?.Length ?? 0}");
            }
            eventArgs.Effect = Forms.DragDropEffects.None;
            return;
        }

        var point = ToDip(PointToClient(new Point(eventArgs.X, eventArgs.Y)));
        var acceptsDrop = UpdateBoxDropPreview(point, itemKeys);
        eventArgs.Effect = acceptsDrop &&
                           (eventArgs.AllowedEffect & Forms.DragDropEffects.Move) != 0
            ? Forms.DragDropEffects.Move
            : Forms.DragDropEffects.None;
        return;
    }

    private void OnExternalFileDragOver(Forms.DragEventArgs eventArgs, string[] paths)
    {
        var dropPoint = ToDip(PointToClient(new Point(eventArgs.X, eventArgs.Y)));
        var overRecycleBin = IsOverRecycleBin(dropPoint);
        var wasEmpty = _externalDragPaths is null;
        _externalDragPaths = paths;
        _externalDragPointer = dropPoint;
        if (overRecycleBin != _overRecycleBin)
        {
            _overRecycleBin = overRecycleBin;
        }
        DiagnosticLog.Info(
            $"External drag over restore={wasEmpty} count={paths.Length} " +
            $"point={dropPoint.X:0},{dropPoint.Y:0} recycle={overRecycleBin}");
        RequestDragRender();
        eventArgs.Effect = overRecycleBin
            ? (eventArgs.AllowedEffect & Forms.DragDropEffects.Move) != 0
                ? Forms.DragDropEffects.Move
                : Forms.DragDropEffects.None
            : (eventArgs.AllowedEffect & Forms.DragDropEffects.Copy) != 0
                ? Forms.DragDropEffects.Copy
                : (eventArgs.AllowedEffect & Forms.DragDropEffects.Move) != 0
                    ? Forms.DragDropEffects.Move
                    : Forms.DragDropEffects.None;
    }

    // The box window owns the OLE drag route while the pointer is over it,
    // so this surface receives no DragOver there and its ghost would freeze
    // (or be cleared by DragLeave). The box forwards its drag state here so
    // the dragged card keeps following the pointer into the box.
    internal void ForwardDragFromBox(
        PointF pointDip,
        IReadOnlyList<string>? externalPaths,
        IReadOnlyList<string>? desktopItemKeys)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        // A desktop icon drag also carries FileDrop paths in its payload, so
        // the active desktop OLE drag must win over the external-file branch:
        // otherwise the real drag ghost freezes at its last position outside
        // the box while a stray external card takes over inside it.
        if (desktopItemKeys is { Count: > 0 } && _dragStarted && _desktopOleDragActive)
        {
            _dragPointer = pointDip;
            _dragPointerOverBox = true;
            UpdateDesktopDragPreview(_dragPointer);
            RequestDragRender();
            return;
        }

        if (externalPaths is { Count: > 0 })
        {
            _externalDragPaths = externalPaths.ToArray();
            _externalDragPointer = pointDip;
            RequestDragRender();
            return;
        }

        // The drag left the box surface (or ended over it). Drop the external
        // ghost state so no stale card lingers after the OLE loop is gone.
        if (_externalDragPaths is not null)
        {
            _externalDragPaths = null;
            RequestDragRender();
        }
    }

    private void OnDragLeave(object? sender, EventArgs eventArgs)
    {
        DiagnosticLog.Info(
            $"Icon surface drag leave external={_externalDragPaths?.Length ?? 0} " +
            $"recycle={_overRecycleBin} pending={_dragRenderPending}");
        _overRecycleBin = false;
        ClearExternalDragPreview();
        ClearBoxDropPreview();
    }

    private async void OnDragDrop(object? sender, Forms.DragEventArgs eventArgs)
    {
        try
        {
            if (TryGetDesktopIconDrag(eventArgs, out var desktopDrag))
            {
                if (ReferenceEquals(desktopDrag.Source, this))
                {
                    var dropPoint = ToDip(PointToClient(new Point(eventArgs.X, eventArgs.Y)));
                    if (_overRecycleBin && IsOverRecycleBin(dropPoint))
                    {
                        CompleteRecycleBinDrop(desktopDrag);
                    }
                    else
                    {
                        CompleteDesktopOleDrop(desktopDrag, new Point(eventArgs.X, eventArgs.Y));
                    }
                }
                return;
            }

            if (!TryGetVirtualBoxDrag(eventArgs, out var itemKeys, out var dragSession))
            {
                if (TryGetExternalFileDrop(eventArgs, out var externalPaths))
                {
                    _overRecycleBin = false;
                    ClearExternalDragPreview();
                    var dropPoint = ToDip(PointToClient(new Point(eventArgs.X, eventArgs.Y)));
                    DiagnosticLog.Info(
                        $"Icon surface external drop monitor={_monitor.Id} paths={externalPaths.Length} " +
                        $"point={dropPoint.X:0},{dropPoint.Y:0} move={eventArgs.Effect == Forms.DragDropEffects.Move}");
                    if (IsOverRecycleBin(dropPoint))
                    {
                        await DeleteExternalDropToRecycleBinAsync(externalPaths);
                    }
                    else
                    {
                        await ImportExternalDropToDesktopAsync(
                            externalPaths,
                            eventArgs.Effect == Forms.DragDropEffects.Move,
                            dropPoint);
                    }
                }
                return;
            }

            var point = ToDip(PointToClient(new Point(eventArgs.X, eventArgs.Y)));
            if (!UpdateBoxDropPreview(point, itemKeys) || _boxDropPreviewCells.Count == 0)
            {
                return;
            }

            var layout = BuildBoxDropDesktopLayout();
            // Mark the source session before committing the state transition.
            // ReleaseAssignedItemsToDesktopAsync is currently synchronous, but
            // keeping the marker first also preserves the contract if that
            // operation later gains asynchronous shell work.
            dragSession.HandledByDesktop = true;
            var released = await _runtime.ReleaseAssignedItemsToDesktopAsync(
                itemKeys,
                new Point(eventArgs.X, eventArgs.Y));
            if (!released)
            {
                return;
            }

            if (!_runtime.IsDesktopAutoArrangeEnabled && layout.Count > 0)
            {
                _runtime.SetDesktopIconLayout(layout);
            }
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("Desktop drop of box items failed", exception);
        }
        finally
        {
            ClearBoxDropPreview();
        }
    }

    private bool TryGetVirtualBoxDrag(
        Forms.DragEventArgs eventArgs,
        out IReadOnlyList<string> itemKeys,
        out DesktopBoxForm.InternalDragSession dragSession)
    {
        itemKeys = [];
        dragSession = null!;
        if (!_runtime.IsVirtualBoxDesktopDropEnabled ||
            eventArgs.Data is null ||
            !eventArgs.Data.GetDataPresent(DesktopBoxForm.ItemKeysFormat) ||
            eventArgs.Data.GetData(DesktopBoxForm.ItemKeysFormat) is not string[] keys ||
            !eventArgs.Data.GetDataPresent(DesktopBoxForm.SourceBoxFormat) ||
            eventArgs.Data.GetData(DesktopBoxForm.SourceBoxFormat) is not string sourceValue ||
            !Guid.TryParse(sourceValue, out var sourceBoxId) ||
            !eventArgs.Data.GetDataPresent(DesktopBoxForm.DragSessionFormat) ||
            eventArgs.Data.GetData(DesktopBoxForm.DragSessionFormat) is not DesktopBoxForm.InternalDragSession session)
        {
            return false;
        }

        var source = _runtime.State.Boxes.FirstOrDefault(box => box.Id == sourceBoxId);
        if (source is null || source.IsMappedFolder || source.MappedFolder?.IsReadOnly == true)
        {
            return false;
        }

        itemKeys = keys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (itemKeys.Count == 0)
        {
            return false;
        }

        dragSession = session;
        return _monitor.IsPrimary;
    }

    private bool UpdateBoxDropPreview(PointF point, IReadOnlyList<string> itemKeys)
    {
        _boxDropPreviewCells.Clear();
        _boxDropItemKeys.Clear();
        _boxDropItemKeys.UnionWith(itemKeys);
        _boxDragPointer = point;
        _boxDragPrimaryKey = itemKeys.FirstOrDefault();
        if (!_monitor.IsPrimary)
        {
            return false;
        }

        var grid = CreateCurrentGrid();
        var target = GetCellAtPoint(point);
        if (target is not { } requestedTarget)
        {
            RequestDragRender();
            return false;
        }

        var stationary = _items.Select(item => new DesktopIconGridItem(
            item.Item.Key.ToString(),
            new DesktopIconGridCell(item.Cell.Column, item.Cell.Row)));
        var result = DesktopIconDragLayoutEngine.CalculateInsertion(
            stationary,
            itemKeys,
            new DesktopIconGridCell(requestedTarget.Column, requestedTarget.Row),
            grid.ColumnCount,
            grid.RowCount);
        if (!result.IsValid)
        {
            RequestDragRender();
            return false;
        }

        foreach (var (key, cell) in result.Placements)
        {
            _boxDropPreviewCells[key] = new GridCell(cell.Column, cell.Row);
        }
        RequestDragRender();
        return true;
    }

    private IReadOnlyDictionary<string, DesktopIconLayoutSnapshot> BuildBoxDropDesktopLayout()
    {
        var layout = _runtime.State.DesktopIconLayout.ToDictionary(
            pair => pair.Key,
            pair => new DesktopIconLayoutSnapshot
            {
                MonitorId = pair.Value.MonitorId,
                Column = pair.Value.Column,
                Row = pair.Value.Row
            },
            StringComparer.OrdinalIgnoreCase);
        foreach (var (key, placement) in _boxDropPreviewCells)
        {
            layout[key] = new DesktopIconLayoutSnapshot
            {
                MonitorId = _monitor.Id,
                Column = placement.Column,
                Row = placement.Row
            };
        }
        return layout;
    }

    private void ClearBoxDropPreview()
    {
        if (_boxDropPreviewCells.Count == 0 && _boxDropItemKeys.Count == 0 &&
            _boxDragPointer is null && _boxDragPrimaryKey is null)
        {
            return;
        }

        _boxDropPreviewCells.Clear();
        _boxDropItemKeys.Clear();
        _boxDragPointer = null;
        _boxDragPrimaryKey = null;
        _geometryDirty = true;
        RequestDragRender();
    }

    private void BeginDesktopDrag(string anchorKey)
    {
        var anchor = _items.FirstOrDefault(item =>
            string.Equals(item.Item.Key.ToString(), anchorKey, StringComparison.OrdinalIgnoreCase));
        if (anchor is null)
        {
            return;
        }

        _dragItemKeys.Clear();
        foreach (var item in _items.Where(item => _selection.Contains(item.Item.Key.ToString())))
        {
            var key = item.Item.Key.ToString();
            _dragItemKeys.Add(key);
        }
        if (_dragItemKeys.Count == 0)
        {
            return;
        }

        _dragAnchorCell = anchor.Cell;
        _dragAnchorKey = anchorKey;
        _lastDragPreviewAnchorCell = null;
        _pendingRenameItem = null;
        var anchorIconBounds = GetIconBounds(anchor.Bounds);
        _dragIconGrabOffset = new PointF(
            _pressPoint.X - anchorIconBounds.X,
            _pressPoint.Y - anchorIconBounds.Y);
        _dragStarted = true;
        _dragPointer = _pressPoint;
        _dragPointerOverBox = false;
        _dragBaseReady = false;
        Forms.Cursor.Current = Forms.Cursors.SizeAll;
        UpdateDesktopDragPreview(_pressPoint);
        _runtime.UpdateDesktopItemDropPreview(
            PointToScreen(new Point(
                (int)Math.Round(_pressPoint.X * _scale),
                (int)Math.Round(_pressPoint.Y * _scale))),
            _dragItemKeys.ToArray(),
            out _dragPointerOverBox);
    }

    // Tracks only the drop anchor cell while the pointer moves. The reflow
    // itself is computed once on drop (CommitDesktopDrop), so dragging stays
    // cheap and no preview is painted - matching the native desktop.
    private void UpdateDesktopDragPreview(PointF point)
    {
        if (_dragAnchorCell is null || _dragAnchorKey is null)
        {
            _lastDragPreviewAnchorCell = null;
            return;
        }

        var grid = CreateCurrentGrid();
        // Resolve the insertion cell from the visual center of the grabbed
        // icon so the drop lands where the icon appears to be, not where the
        // pointer happened to press.
        var floatingIconTopLeft = new PointF(
            point.X - _dragIconGrabOffset.X,
            point.Y - _dragIconGrabOffset.Y);
        var targetPoint = new PointF(
            floatingIconTopLeft.X + _iconSize / 2,
            floatingIconTopLeft.Y + _iconSize / 2);
        _lastDragPreviewAnchorCell = GetCellAtPoint(targetPoint);
    }

    private void CommitDesktopDrop()
    {
        if (_dragAnchorKey is null ||
            _lastDragPreviewAnchorCell is not { } targetCell ||
            _dragItemKeys.Count == 0)
        {
            return;
        }

        if (_runtime.IsDesktopAutoArrangeEnabled)
        {
            _runtime.ResetDesktopIconLayoutForAutoArrange();
            return;
        }

        var grid = CreateCurrentGrid();
        var (direction, columnOffset, rowOffset) = ResolveSqueeze(grid, targetCell);
        var result = DesktopIconDragLayoutEngine.Calculate(
            _items.Select(item => new DesktopIconGridItem(
                item.Item.Key.ToString(),
                new DesktopIconGridCell(item.Cell.Column, item.Cell.Row))),
            _dragItemKeys,
            anchorKey: _dragAnchorKey,
            requestedAnchor: new DesktopIconGridCell(
                targetCell.Column + columnOffset,
                targetCell.Row + rowOffset),
            columnCount: grid.ColumnCount,
            rowCount: grid.RowCount,
            direction: direction);
        if (!result.IsValid)
        {
            return;
        }

        var layout = result.Placements.ToDictionary(
            entry => entry.Key,
            entry => new DesktopIconLayoutSnapshot
            {
                MonitorId = _monitor.Id,
                Column = entry.Value.Column,
                Row = entry.Value.Row
            },
            StringComparer.OrdinalIgnoreCase);
        _runtime.SetDesktopIconLayout(layout);
    }

    // Decides where the dragged icon is inserted based on where its center
    // sits inside the target cell, and which way the displaced icons give
    // way. Dropping onto the left half of an occupied cell inserts at that
    // cell and pushes the row to the right (the dragged icon takes the cell,
    // its former occupant moves right - "ACBX" when C replaces B). Dropping
    // onto the right half inserts one cell further right. A dead-center drop
    // keeps the classic column-major downward cascade.
    private (DesktopIconSqueezeDirection Direction, int ColumnOffset, int RowOffset) ResolveSqueeze(
        DesktopGrid grid,
        GridCell targetCell)
    {
        var cellBounds = GetCellBounds(grid, targetCell);
        var floatingIconTopLeft = new PointF(
            _dragPointer.X - _dragIconGrabOffset.X,
            _dragPointer.Y - _dragIconGrabOffset.Y);
        var targetPoint = new PointF(
            floatingIconTopLeft.X + _iconSize / 2,
            floatingIconTopLeft.Y + _iconSize / 2);
        // A single icon dropped onto one of the four orthogonal neighbours
        // of its own cell swaps the two icons (the engine's swap path), so
        // the anchor must stay on the neighbour itself instead of shifting
        // half a cell sideways.
        if (_dragItemKeys.Count == 1 &&
            _dragAnchorCell is { } sourceCell &&
            Math.Abs(targetCell.Column - sourceCell.Column) +
            Math.Abs(targetCell.Row - sourceCell.Row) == 1)
        {
            return (DesktopIconSqueezeDirection.Down, 0, 0);
        }

        var dx = cellBounds.Width > 0
            ? (targetPoint.X - cellBounds.Left) / cellBounds.Width - 0.5
            : 0;
        var dy = cellBounds.Height > 0
            ? (targetPoint.Y - cellBounds.Top) / cellBounds.Height - 0.5
            : 0;
        const float CenterBand = 0.25f;
        if (Math.Abs(dx) <= CenterBand && Math.Abs(dy) <= CenterBand)
        {
            return (DesktopIconSqueezeDirection.Down, 0, 0);
        }
        if (Math.Abs(dx) >= Math.Abs(dy))
        {
            return (DesktopIconSqueezeDirection.Right, dx > 0 ? 1 : 0, 0);
        }
        return (DesktopIconSqueezeDirection.Down, 0, dy > 0 ? 1 : 0);
    }

    private void EndDesktopDrag()
    {
        _overRecycleBin = false;
        ClearExternalDragPreview();
        _runtime.ClearDesktopItemDropPreviews();
        _dragStarted = false;
        _dragAnchorCell = null;
        _dragAnchorKey = null;
        _lastDragPreviewAnchorCell = null;
        _dragIconGrabOffset = PointF.Empty;
        _dragPointerOverBox = false;
        _dragItemKeys.Clear();
        _geometryDirty = true;
        Forms.Cursor.Current = Forms.Cursors.Default;
    }

    private GridCell? GetCellAtPoint(PointF point)
    {
        var grid = CreateCurrentGrid();
        if (grid.ColumnCount == 0 || grid.RowCount == 0 || !grid.Bounds.Contains(point))
        {
            return null;
        }

        return new GridCell(
            Math.Clamp((int)Math.Floor((point.X - grid.Bounds.Left) / grid.HorizontalSpacing), 0, grid.ColumnCount - 1),
            Math.Clamp((int)Math.Floor((point.Y - grid.Bounds.Top) / grid.VerticalSpacing), 0, grid.RowCount - 1));
    }

    private static RectangleF RectangleFromPoints(PointF first, PointF second) => new(
        Math.Min(first.X, second.X),
        Math.Min(first.Y, second.Y),
        Math.Abs(first.X - second.X),
        Math.Abs(first.Y - second.Y));

    private bool UpdateMarqueeSelection(PointF point)
    {
        var selectionBounds = RectangleFromPoints(_selectionStart, point);
        if (selectionBounds == _selectionRectangle)
        {
            return false;
        }

        _selectionRectangle = selectionBounds;
        _selection.Clear();
        _selection.UnionWith(_selectionBase);
        foreach (var item in _items)
        {
            if (IsSelectionHit(selectionBounds, GetItemHitBounds(item)))
            {
                _selection.Add(item.Key);
            }
        }
        return true;
    }

    private static bool IsSelectionHit(RectangleF selection, RectangleF itemBounds) =>
        selection.Width > 0 && selection.Height > 0
            ? selection.IntersectsWith(itemBounds)
            : itemBounds.Contains(selection.Location);

    private RectangleF GetItemHitBounds(DesktopIconGeometry entry)
    {
        if (!entry.HitBounds.IsEmpty)
        {
            return entry.HitBounds;
        }

        // Defensive fallback for a geometry that was never cached. Measure on
        // a logical (96-DPI) canvas so the metrics match the DIP drawing space.
        using var measureBitmap = DesktopLayerBitmapFactory.Create(1, 1);
        using var measureGraphics = Graphics.FromImage(measureBitmap);
        using var measureFont = ResolveIconLabelFont();
        return CalculateItemHitBounds(
            entry.Bounds,
            GetDesktopWorkAreaBounds(),
            measureGraphics,
            measureFont,
            entry.Item.DisplayName);
    }

    private RectangleF CalculateItemHitBounds(
        RectangleF itemBounds,
        RectangleF workAreaBounds,
        Graphics measureGraphics,
        Font measureFont,
        string displayName)
    {
        var iconBounds = GetIconBounds(itemBounds);
        // Only the actual label footprint should react to the pointer; the
        // remainder of the grid cell (the blank gap between rows and columns)
        // must stay a neutral hit area like the surrounding desktop.
        var textBounds = GetItemTextBounds(
            measureGraphics,
            displayName,
            itemBounds,
            iconBounds,
            measureFont,
            selected: false);
        var textHitBounds = GetTextHitBounds(
            measureGraphics,
            displayName,
            textBounds,
            measureFont);
        return RectangleF.Intersect(
            workAreaBounds,
            RectangleF.Inflate(RectangleF.Union(iconBounds, textHitBounds), 2, 2));
    }

    private RectangleF GetItemVisualBounds(RectangleF iconBounds, RectangleF textBounds)
    {
        var contentBounds = textBounds.IsEmpty
            ? iconBounds
            : RectangleF.Union(iconBounds, textBounds);
        var padding = DesktopItemVisualStyle.SelectionPadding(_iconSize);
        return RectangleF.Inflate(contentBounds, padding, padding);
    }

    private bool IsRaisedVisual(string itemKey, IReadOnlySet<string>? selectedItemKeys = null) =>
        (selectedItemKeys ?? _selection).Contains(itemKey) ||
        (_runtime.State.Settings.Appearance.HoverFeedback &&
         string.Equals(_hoveredItemKey, itemKey, StringComparison.OrdinalIgnoreCase));

    private void OnMouseDoubleClick(object? sender, Forms.MouseEventArgs eventArgs)
    {
        if (eventArgs.Button != Forms.MouseButtons.Left)
        {
            return;
        }
        var item = GetItemAt(ToDip(eventArgs.Location));
        if (item is not null)
        {
            TryAction(() => _runtime.FileOperations.Open(item.Item));
        }
    }

    private void ShowItemContextMenu(DesktopItemRef item, Point location)
    {
        var selectedItems = _items
            .Where(candidate => _selection.Contains(candidate.Item.Key.ToString()))
            .Select(candidate => candidate.Item)
            .Where(candidate => candidate.FileSystemPath is not null)
            .ToArray();
        if (selectedItems.Length == 0)
        {
            selectedItems = [item];
        }
        var session = ShellContextMenuSession.TryCreate(
                selectedItems.Select(candidate => candidate.ParsingName),
                Handle)
            ?? ShellContextMenuSession.TryCreate([item.ParsingName], Handle);
        if (session is null)
        {
            return;
        }

        var canRename = selectedItems.Length == 1 && selectedItems[0].FileSystemPath is not null;
        var command = ShellContextMenuCommand.None;
        _shellContextMenu = session;
        try
        {
            var screenPoint = PointToScreen(location);
            command = session.Show(Handle, screenPoint.X, screenPoint.Y, canRename);
        }
        finally
        {
            _shellContextMenu = null;
            session.Dispose();
        }

        if (command == ShellContextMenuCommand.Rename && canRename)
        {
            _ = RenameItemAsync(selectedItems[0]);
        }
    }

    private void UpdateDropTargetRegistration()
    {
        // The full-monitor layer sits above Explorer's (hidden) list view, so
        // external file drops would be swallowed by this window and never
        // reach the desktop. Keep the surface registered as an OLE target at
        // all times and handle FileDrop payloads in OnDragOver/OnDragDrop.
        AllowDrop = true;
    }

    private bool TryStartDesktopOleDrag()
    {
        var selectedItems = _items
            .Where(item => _dragItemKeys.Contains(item.Item.Key.ToString()))
            .Select(item => item.Item)
            .ToArray();
        if (selectedItems.Length == 0 ||
            selectedItems.Length != _dragItemKeys.Count ||
            selectedItems.Any(item => string.IsNullOrWhiteSpace(item.FileSystemPath)))
        {
            return false;
        }

        var paths = selectedItems.Select(item => item.FileSystemPath!).ToArray();
        var data = new Forms.DataObject();
        var dragSession = new DesktopIconSurfaceDragSession(this, _dragItemKeys.ToArray());
        data.SetData(DesktopIconDragSessionFormat, false, dragSession);
        // Expose the full multi-select key list on the OLE payload as well,
        // so every drop target resolves the complete group even if the
        // session object is not reachable through the data formats.
        data.SetData(DesktopBoxForm.ItemKeysFormat, _dragItemKeys.ToArray());
        var collection = new StringCollection();
        collection.AddRange(paths);
        data.SetFileDropList(collection);

        _desktopOleDragActive = true;
        UpdateDropTargetRegistration();
        try
        {
            // File uploads and other external targets are copy operations.
            // CrabDesk's private drop targets use the accompanying session to
            // perform virtual placement and assignment without moving files.
            Forms.Cursor.Current = Forms.Cursors.Default;
            DoDragDrop(data, ExternalFileDropEffects);
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("Desktop item OLE drag loop failed", exception);
        }
        finally
        {
            _desktopOleDragActive = false;
            UpdateDropTargetRegistration();
            CancelPendingDragRender();
            EndDesktopDrag();
            _pressedItem = null;
            Capture = false;
            PresentLayer();
        }

        return true;
    }

    private void UpdateDesktopOleDropPreview(Point screenPoint)
    {
        if (!_desktopOleDragActive || !_dragStarted)
        {
            return;
        }

        _dragPointer = ToDip(PointToClient(screenPoint));
        _dragPointerOverBox = false;
        UpdateDesktopDragPreview(_dragPointer);
        RequestDragRender();
    }

    private void CompleteDesktopOleDrop(
        DesktopIconSurfaceDragSession dragSession,
        Point screenPoint)
    {
        UpdateDesktopOleDropPreview(screenPoint);
        dragSession.HandledByDesktop = true;
        CommitDesktopDrop();
    }

    private bool IsOverRecycleBin(PointF point) =>
        GetItemAt(point) is { } item && IsRecycleBinItem(item.Item);

    private static bool IsRecycleBinItem(DesktopItemRef item) =>
        item.Kind == DesktopItemKind.Shell &&
        item.ParsingName.Contains(
            "645FF040-5081-101B-9F08-00AA002F954E",
            StringComparison.OrdinalIgnoreCase);

    private bool DraggedKeysAreFileSystemItems(DesktopIconSurfaceDragSession dragSession) =>
        dragSession.ItemKeys.Count > 0 &&
        dragSession.ItemKeys.All(key => _items.Any(item =>
            string.Equals(item.Item.Key.ToString(), key, StringComparison.OrdinalIgnoreCase) &&
            item.Item.FileSystemPath is not null));

    // A drop on the Recycle Bin moves the dragged files there instead of
    // placing them in the grid. The OLE drag loop owns the state cleanup
    // (EndDesktopDrag runs in TryStartDesktopOleDrag's finally block).
    private async void CompleteRecycleBinDrop(DesktopIconSurfaceDragSession dragSession)
    {
        _overRecycleBin = false;
        dragSession.HandledByDesktop = true;
        var items = _items
            .Where(item => dragSession.ItemKeys.Contains(
                item.Item.Key.ToString(),
                StringComparer.OrdinalIgnoreCase))
            .Select(item => item.Item)
            .Where(item => item.FileSystemPath is not null)
            .ToArray();
        if (items.Length == 0)
        {
            return;
        }

        try
        {
            await _runtime.FileOperations.DeleteAsync(items);
            await _runtime.RefreshItemsAsync(false);
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("Failed to move desktop items to the recycle bin", exception);
            Forms.MessageBox.Show(
                this,
                exception.Message,
                "删除失败",
                Forms.MessageBoxButtons.OK,
                Forms.MessageBoxIcon.Error);
        }
    }

    private static bool TryGetExternalFileDrop(
        Forms.DragEventArgs eventArgs,
        out string[] paths)
    {
        paths = [];
        if (eventArgs.Data?.GetDataPresent(Forms.DataFormats.FileDrop) != true ||
            eventArgs.Data.GetData(Forms.DataFormats.FileDrop) is not string[] files ||
            files.Length == 0)
        {
            return false;
        }

        paths = files
            .Where(path => !string.IsNullOrWhiteSpace(path) &&
                (Directory.Exists(path) || File.Exists(path)))
            .ToArray();
        return paths.Length > 0;
    }

    // External files dropped on the desktop land in the real desktop folder,
    // restoring the native Explorer behavior under the replacement layer.
    private async Task ImportExternalDropToDesktopAsync(
        IReadOnlyList<string> paths,
        bool move,
        PointF dropPointDip)
    {
        try
        {
            var desktopDirectory = Environment.GetFolderPath(
                Environment.SpecialFolder.DesktopDirectory);
            var result = await _runtime.FileOperations.ImportAsync(
                paths,
                desktopDirectory,
                move);
            await _runtime.RefreshItemsAsync(applyDesktopRules: false);
            PlaceDroppedItemsAtPoint(result.ImportedPaths, dropPointDip);
            if (result.FailedItems.Count > 0)
            {
                DiagnosticLog.Info(
                    $"External desktop drop imported {result.SuccessfulItems.Count} " +
                    $"failed={result.FailedItems.Count} move={move}");
            }
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("Failed to import dropped files to the desktop", exception);
            Forms.MessageBox.Show(
                this,
                exception.Message,
                "导入失败",
                Forms.MessageBoxButtons.OK,
                Forms.MessageBoxIcon.Error);
        }
    }

    // Newly imported desktop files land at the drop point (native "place
    // where you point" behavior) when a manual layout is active: the first
    // item takes the cell under the pointer and the rest fill the next free
    // cells in reading order.
    private void PlaceDroppedItemsAtPoint(
        IReadOnlyList<string> importedPaths,
        PointF dropPointDip)
    {
        DiagnosticLog.Info(
            $"Place dropped items paths={importedPaths.Count} point={dropPointDip.X:0},{dropPointDip.Y:0} " +
            $"autoArrange={_runtime.IsDesktopAutoArrangeEnabled} primary={_monitor.IsPrimary} " +
            $"layoutKeys={_runtime.State.DesktopIconLayout.Count}");
        if (importedPaths.Count == 0 ||
            _runtime.IsDesktopAutoArrangeEnabled ||
            !_monitor.IsPrimary)
        {
            DiagnosticLog.Info(
                $"Place dropped items skipped: empty={importedPaths.Count == 0} " +
                $"autoArrange={_runtime.IsDesktopAutoArrangeEnabled} primary={_monitor.IsPrimary}");
            return;
        }

        var grid = CreateCurrentGrid();
        if (grid.ColumnCount == 0 || grid.RowCount == 0)
        {
            DiagnosticLog.Info(
                $"Place dropped items skipped: no grid {grid.ColumnCount}x{grid.RowCount}");
            return;
        }
        if (GetCellAtPoint(dropPointDip) is not { } targetCell)
        {
            DiagnosticLog.Info(
                $"Place dropped items skipped: point outside grid bounds " +
                $"grid={grid.Bounds.X:0},{grid.Bounds.Y:0},{grid.Bounds.Width:0},{grid.Bounds.Height:0}");
            return;
        }

        var importedSet = importedPaths
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newItems = _runtime.Items
            .Where(item => item.FileSystemPath is not null &&
                           importedSet.Contains(Path.GetFullPath(item.FileSystemPath!)))
            .ToArray();
        if (newItems.Length == 0)
        {
            DiagnosticLog.Info(
                $"Place dropped items skipped: no matching items in runtime snapshot " +
                $"items={_runtime.Items.Count}");
            return;
        }
        DiagnosticLog.Info(
            $"Place dropped items target cell={targetCell.Column},{targetCell.Row} matched={newItems.Length}");

        var occupied = _runtime.State.DesktopIconLayout.Values
            .Where(placement =>
                string.IsNullOrWhiteSpace(placement.MonitorId) ||
                string.Equals(placement.MonitorId, _monitor.Id, StringComparison.OrdinalIgnoreCase))
            .Select(placement => new GridCell(placement.Column, placement.Row))
            .ToHashSet();
        var next = new Dictionary<string, DesktopIconLayoutSnapshot>(
            _runtime.State.DesktopIconLayout,
            StringComparer.OrdinalIgnoreCase);
        GridCell? cell = targetCell;
        foreach (var item in newItems)
        {
            cell = FindFirstFreeCellAtOrAfter(cell.Value, grid, occupied)
                   ?? FindFirstFreeCell(grid, occupied);
            if (cell is not { } freeCell)
            {
                break;
            }

            occupied.Add(freeCell);
            next[item.Key.ToString()] = new DesktopIconLayoutSnapshot
            {
                MonitorId = _monitor.Id,
                Column = freeCell.Column,
                Row = freeCell.Row
            };
        }

        // The refresh that followed the import already persisted the new
        // items at their automatic cells, so the keys exist in the layout.
        // Apply only when at least one imported item actually moved cells.
        var changed = newItems.Any(item =>
            !_runtime.State.DesktopIconLayout.TryGetValue(item.Key.ToString(), out var existing) ||
            existing.Column != next[item.Key.ToString()].Column ||
            existing.Row != next[item.Key.ToString()].Row);
        if (changed)
        {
            DiagnosticLog.Info(
                $"Place dropped items applying layout entries={next.Count} " +
                $"cells={string.Join(",", newItems.Select(item => $"{item.Key}=({next[item.Key.ToString()].Column},{next[item.Key.ToString()].Row})"))}");
            _runtime.SetDesktopIconLayout(next);
        }
        else
        {
            DiagnosticLog.Info(
                "Place dropped items no-op: imported items already sit at the requested cells");
        }
    }

    private static GridCell? FindFirstFreeCellAtOrAfter(
        GridCell start,
        DesktopGrid grid,
        IReadOnlySet<GridCell> occupied)
    {
        for (var column = start.Column; column < grid.ColumnCount; column++)
        {
            for (var row = column == start.Column ? start.Row : 0;
                 row < grid.RowCount;
                 row++)
            {
                var cell = new GridCell(column, row);
                if (!occupied.Contains(cell))
                {
                    return cell;
                }
            }
        }
        return null;
    }

    private async Task DeleteExternalDropToRecycleBinAsync(IReadOnlyList<string> paths)
    {
        try
        {
            var items = paths.Select(path => new DesktopItemRef
            {
                Key = new DesktopItemKey("file", path.ToUpperInvariant()),
                DisplayName = Path.GetFileName(path),
                ParsingName = path,
                FileSystemPath = path,
                Kind = Directory.Exists(path)
                    ? DesktopItemKind.Folder
                    : DesktopItemKind.File
            }).ToArray();
            await _runtime.FileOperations.DeleteAsync(items);
            await _runtime.RefreshItemsAsync(applyDesktopRules: false);
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("Failed to move dropped files to the recycle bin", exception);
            Forms.MessageBox.Show(
                this,
                exception.Message,
                "删除失败",
                Forms.MessageBoxButtons.OK,
                Forms.MessageBoxIcon.Error);
        }
    }

    private void ClearExternalDragPreview()
    {
        _externalDragPaths = null;
        // Keep the cached icon alive: a queued overlay frame may still render
        // this preview after the drop/leave (the render is BeginInvoke'd).
        // Disposing here lets DrawImage hit a disposed bitmap and throw.
        _externalDragIconPath = null;
    }

    // A small ghost card next to the cursor for external file drops: the
    // first file's icon, its name, and the remaining item count. Painted by
    // the drag overlay so it follows the pointer without repainting the
    // whole monitor layer.
    private void DrawExternalDragPreview(Graphics graphics)
    {
        if (_externalDragPaths is not { Length: > 0 } paths)
        {
            return;
        }
        try
        {
            DrawExternalDragPreviewCore(graphics, paths);
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("External drag preview drawing failed", exception);
        }
    }

    private void DrawExternalDragPreviewCore(Graphics graphics, IReadOnlyList<string> paths)
    {
        var first = paths[0];
        var icon = GetExternalDragIcon(first) ?? ShellIconProvider.GetGenericFileIcon();
        using var font = ResolveIconLabelFont();
        DragGhostRenderer.Draw(
            graphics,
            _externalDragPointer,
            icon,
            Path.GetFileName(first),
            paths.Count,
            font);
    }

    private Bitmap? GetExternalDragIcon(string path)
    {
        // ShellIconProvider hands out shared cached bitmap instances that it
        // owns. Never dispose them here: disposing one poisons the provider
        // cache and every later GetIcon for that key returns a dead bitmap,
        // making the ghost vanish. Just swap the reference.
        if (!string.Equals(_externalDragIconPath, path, StringComparison.OrdinalIgnoreCase))
        {
            _externalDragIcon = _runtime.IconProvider.GetIcon(path, 28);
            _externalDragIconPath = path;
        }
        return _externalDragIcon;
    }

    private void ReleaseExternalDragIcon()
    {
        // Shared provider bitmap - drop the reference without disposing.
        _externalDragIcon = null;
        _externalDragIconPath = null;
    }

    // Explorer-style feedback while a drag hovers the Recycle Bin: a subtle
    // accent backdrop behind the bin icon.
    private void DrawRecycleBinHighlight(Graphics graphics)
    {
        var bin = _items.FirstOrDefault(item => IsRecycleBinItem(item.Item));
        if (bin is null)
        {
            return;
        }

        var selectionColor = ParseColor(
            _runtime.State.Settings.Appearance.SelectionColor,
            Color.FromArgb(74, 91, 177));
        var iconBounds = GetIconBounds(bin.Bounds);
        using var fill = new SolidBrush(Color.FromArgb(86, selectionColor));
        using var border = new Pen(Color.FromArgb(238, selectionColor), 2f);
        using var path = RoundedRectangle(RectangleF.Inflate(iconBounds, 6, 6), 9);
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);
    }

    private static bool TryGetDesktopIconDrag(
        Forms.DragEventArgs eventArgs,
        out DesktopIconSurfaceDragSession dragSession)
    {
        dragSession = null!;
        if (eventArgs.Data?.GetDataPresent(DesktopIconDragSessionFormat) != true ||
            eventArgs.Data.GetData(DesktopIconDragSessionFormat) is not DesktopIconSurfaceDragSession session)
        {
            return false;
        }

        dragSession = session;
        return true;
    }

    // Explorer's slow double-click: click an icon to select it, then click
    // the same icon again after the double-click time but within the slow
    // limit to start an inline rename. The rename only starts on mouse up if
    // the second press did not turn into a drag.
    private void TryBeginSlowDoubleClickRename(DesktopIconGeometry item)
    {
        var now = DateTime.UtcNow;
        var key = item.Item.Key.ToString();
        var elapsed = (now - _lastRenameClickUtc).TotalMilliseconds;
        var isSlowDoubleClick =
            string.Equals(_lastRenameClickKey, key, StringComparison.OrdinalIgnoreCase) &&
            elapsed > Forms.SystemInformation.DoubleClickTime &&
            elapsed < SlowDoubleClickRenameLimitMilliseconds;
        _lastRenameClickKey = key;
        _lastRenameClickUtc = now;
        if (isSlowDoubleClick && item.Item.FileSystemPath is not null)
        {
            _pendingRenameItem = item;
            _pendingRenamePressUtc = now;
        }
    }

    private void CommitPendingSlowDoubleClickRename()
    {
        var pending = _pendingRenameItem;
        _pendingRenameItem = null;
        if (pending is null ||
            _dragStarted ||
            _selecting ||
            _desktopOleDragActive ||
            IsDisposed)
        {
            return;
        }

        // The press became neither a drag nor a marquee. Only rename while
        // the slow double-click window is still open.
        var elapsed = (DateTime.UtcNow - _pendingRenamePressUtc).TotalMilliseconds;
        if (elapsed > SlowDoubleClickRenameLimitMilliseconds)
        {
            return;
        }

        _lastRenameClickKey = null;
        _ = RenameItemAsync(pending.Item);
    }

    private async Task RenameItemAsync(DesktopItemRef item)
    {
        var newName = await ShowInlineRenameAsync(item);
        if (newName is null ||
            string.Equals(newName, item.DisplayName, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            await _runtime.RenameItemAsync(item, newName);
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error($"Failed to rename desktop item '{item.DisplayName}'.", exception);
            Forms.MessageBox.Show(
                this,
                exception.Message,
                "重命名失败",
                Forms.MessageBoxButtons.OK,
                Forms.MessageBoxIcon.Error);
        }
    }

    private async Task<string?> ShowInlineRenameAsync(DesktopItemRef item)
    {
        var entry = _items.LastOrDefault(candidate => string.Equals(
            candidate.Item.Key.ToString(),
            item.Key.ToString(),
            StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return null;
        }

        _renameEditor ??= new DesktopRenameEditor();
        var labelBounds = GetItemLabelEditBounds(entry);
        var scale = (float)Math.Max(_scale, 0.01d);
        var screenLocation = PointToScreen(new Point(
            (int)Math.Round(labelBounds.X * scale),
            (int)Math.Round(labelBounds.Y * scale)));
        var selectStem = item.Kind == DesktopItemKind.File ||
            item.Kind == DesktopItemKind.Shortcut;
        using var labelFont = ResolveIconLabelFont();
        return await _renameEditor.ShowAsync(
            screenLocation,
            new Size(
                (int)Math.Round(labelBounds.Width * scale),
                (int)Math.Round(labelBounds.Height * scale)),
            item.DisplayName,
            selectStem,
            _runtime.IsDarkTheme,
            labelFont);
    }

    private RectangleF GetItemLabelEditBounds(DesktopIconGeometry entry)
    {
        var iconBounds = GetIconBounds(entry.Bounds);
        using var measureBitmap = DesktopLayerBitmapFactory.Create(1, 1);
        using var measureGraphics = Graphics.FromImage(measureBitmap);
        using var font = ResolveIconLabelFont();
        var textBounds = GetItemTextBounds(
            measureGraphics,
            entry.Item.DisplayName,
            entry.Bounds,
            iconBounds,
            font,
            selected: false);
        var hit = GetTextHitBounds(measureGraphics, entry.Item.DisplayName, textBounds, font);
        var lineHeight = Math.Max(1, font.GetHeight(measureGraphics));
        var maxWidth = Math.Max(0, entry.Bounds.Width - 6);
        var width = hit.IsEmpty
            ? maxWidth
            : Math.Min(maxWidth, Math.Max(48, hit.Width + 10));
        var centerX = hit.IsEmpty
            ? textBounds.X + textBounds.Width / 2
            : hit.X + hit.Width / 2;
        var left = Math.Max(
            entry.Bounds.X + 1,
            Math.Min(centerX - width / 2, entry.Bounds.Right - width - 1));
        var top = Math.Max(1, textBounds.Y - 3);
        // Height follows the measured label so a wrapped (two-line) name gets
        // a two-line editor instead of a clipped single line; the multiline
        // input renders the full name centered.
        var labelHeight = hit.IsEmpty
            ? lineHeight
            : Math.Max(lineHeight, hit.Height);
        return new RectangleF(left, top, width, labelHeight + 8);
    }

    private DesktopIconGeometry? GetItemAt(PointF point)
    {
        for (var index = _items.Count - 1; index >= 0; index--)
        {
            var item = _items[index];
            var key = item.Item.Key.ToString();
            var hitBounds = _expandedItemHitBounds.TryGetValue(key, out var expandedBounds)
                ? expandedBounds
                : GetItemHitBounds(item);
            if (hitBounds.Contains(point))
            {
                return item;
            }
        }
        return null;
    }

    private DesktopIconGeometry? GetHoverItemAt(PointF point)
    {
        // Resolve the stable icon/cell hit first. Expanded label bounds are
        // useful for keeping the current item highlighted, but allowing them
        // to win over a neighbouring icon makes A/B hover transitions depend
        // on which frame happened to be rendered last.
        for (var index = _items.Count - 1; index >= 0; index--)
        {
            if (GetItemHitBounds(_items[index]).Contains(point))
            {
                return _items[index];
            }
        }

        if (_hoveredItemKey is not null &&
            _expandedItemHitBounds.TryGetValue(_hoveredItemKey, out var expandedBounds) &&
            expandedBounds.Contains(point))
        {
            return _items.LastOrDefault(item => string.Equals(
                item.Item.Key.ToString(),
                _hoveredItemKey,
                StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }

    private bool SetHoveredItem(DesktopIconGeometry? item)
    {
        var nextKey = _runtime.State.Settings.Appearance.HoverFeedback
            ? item?.Item.Key.ToString()
            : null;
        if (string.Equals(_hoveredItemKey, nextKey, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var cursorClient = PointToClient(Forms.Cursor.Position);
        DiagnosticLog.Info(
            $"Icon hover {_hoveredItemKey ?? "<none>"} -> {nextKey ?? "<none>"} " +
            $"at={cursorClient.X},{cursorClient.Y} " +
            $"hitBounds={item?.HitBounds ?? RectangleF.Empty}");
        if (_hoveredItemKey is { } previousKey && !_selection.Contains(previousKey))
        {
            _expandedItemHitBounds.Remove(previousKey);
        }
        _hoveredItemKey = nextKey;
        return true;
    }

    private bool IsPointerOverBox(Point screenPoint) =>
        _boxPointerHitTest?.Invoke(screenPoint) == true;

    private void SynchronizeNativeMetrics(DesktopIconViewState desktopViewState)
    {
        // Explorer owns both values.  Keeping the replacement surface on the
        // same metrics makes its grid align with the selected desktop icon
        // size and lets Ctrl+wheel update the visual layer immediately.
        var nativeIconSize = desktopViewState.IconSize ?? (int)DefaultIconSize;
        if (DesktopIconPositionService.TryGetItemSpacing(_desktopListView, out var nativeSpacing))
        {
            _lastKnownNativeSpacing = nativeSpacing;
        }
        else
        {
            nativeSpacing = _lastKnownNativeSpacing;
        }
        var scale = (float)Math.Max(_scale, 0.01d);
        _iconSize = Math.Clamp(nativeIconSize / scale, 16f, 256f);
        _horizontalSpacing = Math.Clamp(nativeSpacing.Width / scale, _iconSize + 8, 512f);
        _verticalSpacing = Math.Clamp(nativeSpacing.Height / scale, _iconSize + 30, 512f);
    }

    private static IOrderedEnumerable<DesktopItemRef> OrderDesktopItems(
        IReadOnlyList<DesktopItemRef> items,
        DesktopIconSortState sort) => DesktopItemSortService.Order(items, sort);

    private PointF ToDip(Point point) => new(point.X / (float)_scale, point.Y / (float)_scale);

    private static Color ParseColor(string value, Color fallback)
    {
        try
        {
            return ColorTranslator.FromHtml(value);
        }
        catch
        {
            return fallback;
        }
    }

    private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void TryAction(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("Desktop icon surface action failed", exception);
        }
    }

    private readonly record struct DesktopGrid(
        RectangleF Bounds,
        float HorizontalSpacing,
        float VerticalSpacing,
        int ColumnCount,
        int RowCount);

    private readonly record struct DesktopGridTopology(int ColumnCount, int RowCount);

    private readonly record struct GridCell(int Column, int Row);

    private sealed record DesktopIconGeometry(
        DesktopItemRef Item,
        RectangleF Bounds,
        GridCell Cell)
    {
        internal string Key => Item.Key.ToString();
        internal RectangleF HitBounds { get; set; }
    }
}

/// <summary>
/// Private metadata carried alongside a standard FileDrop payload. CrabDesk
/// uses it to keep internal drops virtual while external applications receive
/// the normal filesystem paths.
/// </summary>
internal sealed class DesktopIconSurfaceDragSession(
    DesktopIconSurface source,
    IReadOnlyList<string> itemKeys)
{
    internal DesktopIconSurface Source { get; } = source;
    public IReadOnlyList<string> ItemKeys { get; } = itemKeys;
    public bool HandledByBox { get; set; }
    public bool HandledByDesktop { get; set; }
}
