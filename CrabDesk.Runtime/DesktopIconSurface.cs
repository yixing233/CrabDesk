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
    private const int WmMouseActivate = 0x0021;
    private const int WmContextMenu = 0x007B;
    private const int WsClipSiblings = 0x04000000;
    private const int WsExLayered = 0x00080000;
    private const float DefaultIconSize = 48;
    private const float DefaultHorizontalSpacing = 88;
    private const float DefaultVerticalSpacing = 96;
    private const float DesktopGridEdgeInset = 8;
    private const int CompactLabelLineCount = 2;
    private const int SelectedFillAlpha = 112;
    private const int HoverFillAlpha = 156;
    private const int HoverBorderAlpha = 232;
    private const float HoverBrightness = 0.30f;
    // A per-pixel-alpha layered window is click-through where alpha is zero.
    // Keep the desktop background visually transparent while leaving it
    // targetable for blank-area marquee selection.
    private const int DesktopHitTestAlpha = 1;
    // Let a layered-window MouseLeave settle before recalculating hover.
    private const int HoverReconcileDelayMilliseconds = 32;
    private static readonly IntPtr MaNoActivate = new(3);
    private readonly CrabDeskRuntime _runtime;
    private readonly MonitorLayout _monitor;
    private readonly IntPtr _desktopListView;
    private readonly double _scale;
    private readonly List<DesktopIconGeometry> _items = [];
    private readonly HashSet<string> _selection = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _selectionBase = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dragItemKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GridCell> _dragPreviewCells = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GridCell> _dragTargetCells = new(StringComparer.OrdinalIgnoreCase);
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
    private readonly Forms.Timer _hoverReconcileTimer;
    private readonly DesktopDragOverlay _dragOverlay;
    private bool _geometryDirty = true;
    private bool _dragRenderPending;
    private bool _hoverReconcilePending;
    private bool _presentingLayer;
    private bool _presentRequested;
    private Bitmap? _layerBitmap;
    private Bitmap? _staticLayerBitmap;
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
        // Register as an OLE target only for the short virtual box-to-desktop
        // drag window. Ordinary Explorer file drops must continue to target
        // the native desktop underneath this presentation layer.
        AllowDrop = false;
        DragEnter += OnDragOver;
        DragOver += OnDragOver;
        DragLeave += OnDragLeave;
        DragDrop += OnDragDrop;
        _dragOverlay = new DesktopDragOverlay();
        Controls.Add(_dragOverlay);
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
            _layerBitmap = new Bitmap(
                ClientSize.Width,
                ClientSize.Height,
                System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        }
    }

    private void EnsureStaticLayerBitmap()
    {
        if (_staticLayerBitmap is null ||
            _staticLayerBitmap.Width != ClientSize.Width ||
            _staticLayerBitmap.Height != ClientSize.Height)
        {
            _staticLayerBitmap?.Dispose();
            _staticLayerBitmap = new Bitmap(
                ClientSize.Width,
                ClientSize.Height,
                System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
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
            EnsureStaticLayerBitmap();
            var staticFrameChanged = !_dragBaseReady;
            if (staticFrameChanged)
            {
                using var baseGraphics = Graphics.FromImage(_staticLayerBitmap!);
                DrawSettledLayer(
                    baseGraphics,
                    workAreaBounds,
                    includeDragPreview: false,
                    includeBoxDropPreview: false,
                    selectedItemKeys: _selecting ? _selectionBase : null,
                    includeSelectionRectangle: !_selecting);
                _dragBaseReady = true;
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
            DrawSettledLayer(graphics, workAreaBounds, includeDragPreview: true);
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
        return _lastPresentSucceeded;
    }

    private bool IsDragCompositeActive =>
        _selecting || _dragStarted || _boxTransformActive?.Invoke() == true;

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
        if (_dragStarted)
        {
            DrawDesktopDragPreview(graphics);
        }
        DrawBoxItemDropPreview(graphics);
        if (_dragStarted || _boxDropItemKeys.Count > 0 ||
            _boxTransformActive?.Invoke() == true)
        {
            _dragBoxRenderer?.Invoke(graphics, clipBounds);
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

        if (_dragStarted)
        {
            var grid = CreateCurrentGrid();
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

            if (!_dragPointerOverBox)
            {
                foreach (var cell in _dragTargetCells.Values)
                {
                    bounds = UnionVisualBounds(
                        bounds,
                        RectangleF.Inflate(GetIconBounds(GetCellBounds(grid, cell)), 10, 10));
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
        bool includeDragPreview,
        bool includeBoxDropPreview = true,
        IReadOnlySet<string>? selectedItemKeys = null,
        bool includeSelectionRectangle = true)
    {
        graphics.Clear(Color.Transparent);
        graphics.CompositingMode = CompositingMode.SourceOver;
        ConfigureLayerGraphics(graphics, workAreaBounds, fastRender: false);
        using var hitTestBackground = new SolidBrush(Color.FromArgb(DesktopHitTestAlpha, Color.Black));
        graphics.FillRectangle(hitTestBackground, workAreaBounds);
        DrawDesktopItems(
            graphics,
            includeDragPreview,
            selectedItemKeys,
            includeSelectionRectangle);
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

    private void DrawDesktopDragPreview(Graphics graphics)
    {
        using var font = ResolveIconLabelFont();
        using var textFormat = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Near,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.LineLimit
        };
        DrawDragPreview(graphics, font, textFormat);
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
        foreach (var entry in _items)
        {
            entry.HitBounds = CalculateItemHitBounds(entry.Bounds, workAreaBounds);
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
        bool includeDragPreview = true,
        IReadOnlySet<string>? selectedItemKeys = null,
        bool includeSelectionRectangle = true)
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
        var previewGrid = _dragStarted || _boxDropPreviewCells.Count > 0
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
            if (previewGrid is { } grid)
            {
                if (_dragPreviewCells.TryGetValue(itemKey, out var previewCell))
                {
                    drawBounds = GetCellBounds(grid, previewCell);
                }
                else if (_boxDropPreviewCells.TryGetValue(itemKey, out var boxPreviewCell))
                {
                    drawBounds = GetCellBounds(grid, boxPreviewCell);
                }
            }
            var hovered =
                _runtime.State.Settings.Appearance.HoverFeedback &&
                string.Equals(_hoveredItemKey, itemKey, StringComparison.OrdinalIgnoreCase);
            var iconBounds = GetIconBounds(drawBounds);
            var textBounds = GetItemTextBounds(
                graphics,
                entry.Item.DisplayName,
                drawBounds,
                iconBounds,
                font,
                selected || hovered);
            labelBoundsByKey[itemKey] = textBounds;
            var visualBounds = GetItemVisualBounds(iconBounds, textBounds);
            if (selected || hovered)
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
                var hoverColor = BrightenColor(selectionColor, HoverBrightness);
                using var fill = new SolidBrush(Color.FromArgb(HoverFillAlpha, hoverColor));
                using var border = new Pen(Color.FromArgb(HoverBorderAlpha, hoverColor), 1);
                using var path = RoundedRectangle(visualBounds, SelectionCornerRadius);
                graphics.FillPath(fill, path);
                graphics.DrawPath(border, path);
            }
            else if (selected)
            {
                using var fill = new SolidBrush(Color.FromArgb(SelectedFillAlpha, selectionColor));
                using var path = RoundedRectangle(visualBounds, SelectionCornerRadius);
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

        if (includeDragPreview && _dragStarted)
        {
            DrawDragPreview(graphics, font, textFormat);
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
        using var highlightFill = new SolidBrush(Color.FromArgb(SelectedFillAlpha, selectionColor));
        using var highlightBorder = new Pen(Color.FromArgb(190, selectionColor), 1f);
        foreach (var entry in _items)
        {
            if (!IsDynamicMarqueeSelection(entry))
            {
                continue;
            }

            var visualBounds = RectangleF.Inflate(
                entry.HitBounds.IsEmpty ? GetItemHitBounds(entry) : entry.HitBounds,
                SelectionPadding,
                SelectionPadding);
            using var path = RoundedRectangle(visualBounds, SelectionCornerRadius);
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

    private void DrawDragPreview(Graphics graphics, Font font, StringFormat textFormat)
    {
        var selectionColor = ParseColor(_runtime.State.Settings.Appearance.SelectionColor, Color.FromArgb(74, 91, 177));
        var grid = CreateCurrentGrid();

        // The target cells stay visible as a quiet insertion preview while
        // the selected icons themselves remain attached to the pointer. When
        // the pointer is still over the source cells, the floating preview is
        // sufficient and the extra layer only creates visual noise.
        var sourceCells = _items
            .Where(item => _dragItemKeys.Contains(item.Item.Key.ToString()))
            .ToDictionary(item => item.Item.Key.ToString(), item => item.Cell,
                StringComparer.OrdinalIgnoreCase);
        var showTargetPreview = _dragTargetCells.Any(entry =>
            !sourceCells.TryGetValue(entry.Key, out var sourceCell) ||
            sourceCell != entry.Value);
        if (!_dragPointerOverBox && showTargetPreview)
        {
            foreach (var entry in _items.Where(item => _dragTargetCells.ContainsKey(item.Item.Key.ToString())))
            {
                var key = entry.Item.Key.ToString();
                if (!_dragTargetCells.TryGetValue(key, out var cell))
                {
                    continue;
                }

                var bounds = GetCellBounds(grid, cell);
                // The insertion marker follows the same visual bounds as the
                // icon itself. Drawing a border around the whole cell makes
                // it appear shifted because the icon is centered inside the
                // wider native spacing rectangle.
                var previewBounds = RectangleF.Inflate(GetIconBounds(bounds), 4, 4);
                using var fill = new SolidBrush(Color.FromArgb(78, selectionColor));
                using var border = new Pen(Color.FromArgb(238, selectionColor), 2f);
                using var path = RoundedRectangle(previewBounds, 5);
                graphics.FillPath(fill, path);
                graphics.DrawPath(border, path);
            }
        }

        var anchor = _items.FirstOrDefault(item =>
            string.Equals(item.Item.Key.ToString(), _dragAnchorKey, StringComparison.OrdinalIgnoreCase));
        if (anchor is null)
        {
            return;
        }

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

        DrawBoxDragFloatingPreview(graphics, pointer, accent);
    }

    private void DrawBoxDragFloatingPreview(Graphics graphics, PointF pointer, Color accent)
    {
        var primaryItem = _boxDragPrimaryKey is null
            ? null
            : _runtime.Items.FirstOrDefault(item =>
                string.Equals(item.Key.ToString(), _boxDragPrimaryKey, StringComparison.OrdinalIgnoreCase));
        var icon = primaryItem is null
            ? ShellIconProvider.GetGenericFileIcon()
            : GetDesktopIconBitmap(
                    primaryItem,
                    Math.Clamp((int)Math.Round(_iconSize * _scale), 16, 256))
                ?? ShellIconProvider.GetGenericFileIcon();
        var iconSize = Math.Clamp(_iconSize, 32f, 56f);
        var stackCount = Math.Min(3, Math.Max(1, _boxDropItemKeys.Count));
        var stackOffset = (stackCount - 1) * 3f;
        var origin = new PointF(
            pointer.X - iconSize * 0.3f - stackOffset,
            pointer.Y - iconSize * 0.3f - stackOffset);
        for (var index = stackCount - 1; index >= 0; index--)
        {
            var offset = index * 3f;
            var tile = new RectangleF(
                origin.X + offset - 3,
                origin.Y + offset - 3,
                iconSize + 6,
                iconSize + 6);
            using var tileFill = new SolidBrush(Color.FromArgb(42 + index * 10, accent));
            using var tileBorder = new Pen(Color.FromArgb(176, accent), 1.2f);
            using var tilePath = RoundedRectangle(tile, 5);
            graphics.FillPath(tileFill, tilePath);
            graphics.DrawPath(tileBorder, tilePath);
        }

        if (icon is not null)
        {
            DrawImageWithAlpha(
                graphics,
                icon,
                new RectangleF(origin.X, origin.Y, iconSize, iconSize),
                0.9f);
        }

        if (_boxDropItemKeys.Count <= 1)
        {
            return;
        }

        var badgeText = _boxDropItemKeys.Count.ToString();
        using var badgeFont = new Font("Segoe UI", 8, FontStyle.Bold, GraphicsUnit.Point);
        using var badgeFill = new SolidBrush(Color.FromArgb(242, accent));
        using var badgeTextBrush = new SolidBrush(Color.White);
        using var badgeFormat = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        var badgeSize = Math.Max(17, graphics.MeasureString(badgeText, badgeFont).Width + 8);
        var badge = new RectangleF(
            origin.X + iconSize - badgeSize / 2,
            origin.Y + iconSize - 12,
            badgeSize,
            17);
        using var badgePath = RoundedRectangle(badge, badge.Height / 2);
        graphics.FillPath(badgeFill, badgePath);
        graphics.DrawString(badgeText, badgeFont, badgeTextBrush, badge, badgeFormat);
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

        // The layout rectangle is intentionally wide enough for wrapping, but
        // it should not make the surrounding blank desktop area behave like a
        // click target. Use the measured label width for short names and only
        // fall back to the full width when the label actually needs wrapping.
        var unconstrained = graphics.MeasureString(displayName, font);
        var width = Math.Min(textBounds.Width, Math.Max(font.Size, unconstrained.Width + 4));
        var lineHeight = Math.Max(1, font.GetHeight(graphics));
        var lineCount = Math.Max(1, (int)Math.Ceiling(unconstrained.Width / Math.Max(1, textBounds.Width)));
        var height = Math.Min(textBounds.Height, lineHeight * lineCount + 4);
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
        var point = ToDip(eventArgs.Location);
        var item = GetItemAt(point);
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
                    PresentLayer();
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
            if (selectionChanged || hoverChanged)
            {
                PresentLayer();
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
                if (SetHoveredItem(GetHoverItemAt(ToDip(cursorClientPoint))))
                {
                    RequestDragRender();
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
        if (_dragPointerOverBox)
        {
            if (_dragPreviewCells.Count > 0 || _dragTargetCells.Count > 0)
            {
                _dragBaseReady = false;
            }
            _dragPreviewCells.Clear();
            _dragTargetCells.Clear();
        }
        else
        {
            UpdateDesktopDragPreview(point);
        }
        RequestDragRender();
    }

    private void OnMouseUp(object? sender, Forms.MouseEventArgs eventArgs)
    {
        var point = ToDip(eventArgs.Location);
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
            if (_dragPointerOverBox)
            {
                _dragPreviewCells.Clear();
                _dragTargetCells.Clear();
            }
            else
            {
                UpdateDesktopDragPreview(point);
            }
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
        var currentItem = ClientRectangle.Contains(clientPoint)
            ? GetHoverItemAt(ToDip(clientPoint))
            : null;
        if (SetHoveredItem(currentItem))
        {
            RequestDragRender();
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

            UpdateDesktopOleDropPreview(new Point(eventArgs.X, eventArgs.Y));
            eventArgs.Effect = (eventArgs.AllowedEffect & Forms.DragDropEffects.Copy) != 0
                ? Forms.DragDropEffects.Copy
                : Forms.DragDropEffects.None;
            return;
        }

        if (!TryGetVirtualBoxDrag(eventArgs, out var itemKeys, out _))
        {
            ClearBoxDropPreview();
            eventArgs.Effect = Forms.DragDropEffects.None;
            return;
        }

        var point = ToDip(PointToClient(new Point(eventArgs.X, eventArgs.Y)));
        var acceptsDrop = UpdateBoxDropPreview(point, itemKeys);
        eventArgs.Effect = acceptsDrop &&
                           (eventArgs.AllowedEffect & Forms.DragDropEffects.Move) != 0
            ? Forms.DragDropEffects.Move
            : Forms.DragDropEffects.None;
    }

    private void OnDragLeave(object? sender, EventArgs eventArgs) => ClearBoxDropPreview();

    private async void OnDragDrop(object? sender, Forms.DragEventArgs eventArgs)
    {
        try
        {
            if (TryGetDesktopIconDrag(eventArgs, out var desktopDrag))
            {
                if (ReferenceEquals(desktopDrag.Source, this))
                {
                    CompleteDesktopOleDrop(desktopDrag, new Point(eventArgs.X, eventArgs.Y));
                }
                return;
            }

            if (!TryGetVirtualBoxDrag(eventArgs, out var itemKeys, out var dragSession))
            {
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
        _dragPreviewCells.Clear();
        _dragTargetCells.Clear();
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
        var anchorIconBounds = GetIconBounds(anchor.Bounds);
        _dragIconGrabOffset = new PointF(
            _pressPoint.X - anchorIconBounds.X,
            _pressPoint.Y - anchorIconBounds.Y);
        _dragStarted = true;
        _dragPointer = _pressPoint;
        _dragPointerOverBox = false;
        Forms.Cursor.Current = Forms.Cursors.SizeAll;
        UpdateDesktopDragPreview(_pressPoint);
        _runtime.UpdateDesktopItemDropPreview(
            PointToScreen(new Point(
                (int)Math.Round(_pressPoint.X * _scale),
                (int)Math.Round(_pressPoint.Y * _scale))),
            _dragItemKeys.ToArray(),
            out _dragPointerOverBox);
        if (_dragPointerOverBox)
        {
            _dragPreviewCells.Clear();
            _dragTargetCells.Clear();
        }
    }

    private void UpdateDesktopDragPreview(PointF point)
    {
        if (_dragAnchorCell is not { } anchorCell ||
            _dragAnchorKey is null)
        {
            _dragPreviewCells.Clear();
            _dragTargetCells.Clear();
            _lastDragPreviewAnchorCell = null;
            return;
        }

        var grid = CreateCurrentGrid();
        // Resolve the insertion cell from the visual center of the floating
        // icon. Using the cell's top-left grab offset makes the marker lag by
        // one column/row while the icon itself has already crossed the grid
        // boundary, especially when the pointer grabbed an icon off-center.
        var floatingIconTopLeft = new PointF(
            point.X - _dragIconGrabOffset.X,
            point.Y - _dragIconGrabOffset.Y);
        var targetPoint = new PointF(
            floatingIconTopLeft.X + _iconSize / 2,
            floatingIconTopLeft.Y + _iconSize / 2);
        var targetCell = GetCellAtPoint(targetPoint);
        if (targetCell is not { } requestedTarget)
        {
            if (_dragPreviewCells.Count > 0 || _dragTargetCells.Count > 0)
            {
                _dragBaseReady = false;
            }
            _dragPreviewCells.Clear();
            _dragTargetCells.Clear();
            _lastDragPreviewAnchorCell = null;
            return;
        }

        if (_lastDragPreviewAnchorCell == requestedTarget && _dragPreviewCells.Count > 0)
        {
            return;
        }

        _dragPreviewCells.Clear();
        _dragTargetCells.Clear();
        _dragBaseReady = false;
        _lastDragPreviewAnchorCell = requestedTarget;

        var result = DesktopIconDragLayoutEngine.Calculate(
            _items.Select(item => new DesktopIconGridItem(
                item.Item.Key.ToString(),
                new DesktopIconGridCell(item.Cell.Column, item.Cell.Row))),
            _dragItemKeys,
            anchorKey: _dragAnchorKey,
            requestedAnchor: new DesktopIconGridCell(requestedTarget.Column, requestedTarget.Row),
            columnCount: grid.ColumnCount,
            rowCount: grid.RowCount);
        if (!result.IsValid)
        {
            return;
        }

        foreach (var (key, cell) in result.Placements)
        {
            _dragPreviewCells[key] = new GridCell(cell.Column, cell.Row);
        }
        foreach (var (key, cell) in result.DraggedPlacements)
        {
            _dragTargetCells[key] = new GridCell(cell.Column, cell.Row);
        }
    }

    private void CommitDesktopDrop()
    {
        if (_dragPreviewCells.Count == 0 || _dragTargetCells.Count == 0)
        {
            return;
        }

        if (_runtime.IsDesktopAutoArrangeEnabled)
        {
            _runtime.ResetDesktopIconLayoutForAutoArrange();
            return;
        }

        var layout = _dragPreviewCells.ToDictionary(
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

    private void EndDesktopDrag()
    {
        _runtime.ClearDesktopItemDropPreviews();
        _dragStarted = false;
        _dragAnchorCell = null;
        _dragAnchorKey = null;
        _lastDragPreviewAnchorCell = null;
        _dragIconGrabOffset = PointF.Empty;
        _dragPointerOverBox = false;
        _dragItemKeys.Clear();
        _dragPreviewCells.Clear();
        _dragTargetCells.Clear();
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

        return CalculateItemHitBounds(entry.Bounds, GetDesktopWorkAreaBounds());
    }

    private RectangleF CalculateItemHitBounds(RectangleF itemBounds, RectangleF workAreaBounds)
    {
        var iconBounds = GetIconBounds(itemBounds);
        var textTop = iconBounds.Bottom + 3;
        var textHeight = Math.Max(0, itemBounds.Bottom - textTop - 3);
        var textBounds = new RectangleF(
            itemBounds.X + 2,
            textTop,
            Math.Max(0, itemBounds.Width - 4),
            textHeight);
        return RectangleF.Intersect(
            workAreaBounds,
            RectangleF.Inflate(RectangleF.Union(iconBounds, textBounds), 2, 2));
    }

    private RectangleF GetItemVisualBounds(RectangleF iconBounds, RectangleF textBounds)
    {
        var contentBounds = textBounds.IsEmpty
            ? iconBounds
            : RectangleF.Union(iconBounds, textBounds);
        return RectangleF.Inflate(contentBounds, SelectionPadding, SelectionPadding);
    }

    private bool IsRaisedVisual(string itemKey, IReadOnlySet<string>? selectedItemKeys = null) =>
        (selectedItemKeys ?? _selection).Contains(itemKey) ||
        (_runtime.State.Settings.Appearance.HoverFeedback &&
         string.Equals(_hoveredItemKey, itemKey, StringComparison.OrdinalIgnoreCase));

    private float SelectionPadding => Math.Max(1f, _iconSize / 24f);

    private float SelectionCornerRadius => Math.Max(2f, _iconSize / 12f);

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
        AllowDrop = _virtualBoxDropTargetEnabled || _desktopOleDragActive;
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
            DoDragDrop(data, Forms.DragDropEffects.Copy);
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

    private async Task RenameItemAsync(DesktopItemRef item)
    {
        var newName = DesktopRenameDialog.Show(this, _runtime.IsDarkTheme, item);
        if (newName is null)
        {
            return;
        }

        try
        {
            await _runtime.RenameItemAsync(item, newName);
            ClearSelection();
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

        _hoveredItemKey = nextKey;
        return true;
    }

    private static Color BrightenColor(Color color, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        return Color.FromArgb(
            color.A,
            (int)Math.Round(color.R + (255 - color.R) * amount),
            (int)Math.Round(color.G + (255 - color.G) * amount),
            (int)Math.Round(color.B + (255 - color.B) * amount));
    }

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
