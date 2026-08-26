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
    internal static Forms.DragDropEffects ResolveDesktopDragEffect(
        Forms.DragDropEffects allowedEffects,
        bool acceptsFolder,
        bool overRecycleBin,
        bool controlPressed)
    {
        var preferredEffect = overRecycleBin
            ? Forms.DragDropEffects.Move
            : acceptsFolder
                ? controlPressed
                    ? Forms.DragDropEffects.Copy
                    : Forms.DragDropEffects.Move
                : Forms.DragDropEffects.Copy;
        return (allowedEffects & preferredEffect) != 0
            ? preferredEffect
            : Forms.DragDropEffects.None;
    }
    internal static RectangleF CalculateDesktopFolderDropHighlightBounds(RectangleF iconBounds) =>
        RectangleF.Inflate(iconBounds, 12, 12);
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
    private static readonly IntPtr MaNoActivate = new(3);
    private readonly CrabDeskRuntime _runtime;
    private readonly MonitorLayout _monitor;
    private readonly IntPtr _desktopListView;
    private readonly double _scale;
    private readonly List<DesktopIconGeometry> _items = [];
    private readonly HashSet<string> _selection = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _selectionBase = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dragItemKeys = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, RectangleF>? _desktopDragInitialVisualBounds;
    private GridCell? _boxDropTargetCell;
    // A highlighted long label can extend beyond its grid cell. Retain that
    // visual footprint while the pointer crosses the expanded label.
    private readonly Dictionary<string, RectangleF> _expandedItemHitBounds = new(StringComparer.OrdinalIgnoreCase);
    // The shell provider owns and may evict its cached bitmaps. Keep copies
    // here because this full-surface renderer can reuse an icon across frames.
    private readonly Dictionary<(string ParsingName, int PixelSize), Bitmap> _desktopIconCache = [];
    private readonly HashSet<(string ParsingName, int PixelSize)> _pendingDesktopIconLoads = [];
    private readonly CancellationTokenSource _desktopIconLoadCancellation = new();
    private int _desktopIconCacheVersion;
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
    private IReadOnlyList<string>? _virtualBoxDragItemKeys;
    private DesktopBoxForm.InternalDragSession? _virtualBoxDragSession;
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
    private Func<bool>? _boxVisualsInParent;
    private Func<RectangleF?>? _boxDynamicBounds;
    private Func<RectangleF?>? _boxDynamicDirtyBounds;
    private Func<int>? _boxDynamicVersion;
    private Action? _boxDynamicStateUpdate;
    private Func<Point, bool>? _boxPointerHitTest;
    private Func<bool>? _boxPartialAnimationOnly;
    private readonly Forms.Timer _hoverReconcileTimer;
    private readonly Forms.Timer _dragPointerTimer;
    private readonly DesktopDragOverlay _dragOverlay;
    private readonly DesktopHoverOverlay _hoverOverlay;
    private DesktopRenameEditor? _renameEditor;
    private string? _renamingItemKey;
    private bool _overRecycleBin;
    private string? _desktopFolderDropTargetKey;
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
    private bool _boxVisualRenderPending;
    private RectangleF? _pendingBoxVisualBounds;
    private IReadOnlyDictionary<string, RectangleF>? _pendingDesktopReleaseInitialVisualBounds;
    private readonly HashSet<string> _pendingDesktopReleaseItemKeys = new(StringComparer.OrdinalIgnoreCase);
    private bool _hoverReconcilePending;
    private bool _presentingLayer;
    private bool _presentRequested;
    private Bitmap? _layerBitmap;
    private Bitmap? _staticLayerBitmap;
    private RectangleF? _lastHoverOverlayBounds;
    private bool _hoverOverlayUnavailable;
    private bool _dragBaseReady;
    private int _lastBoxDynamicVersion = int.MinValue;
    private int _lastPresentedParentBoxDynamicVersion = int.MinValue;
    private RectangleF? _lastParentBoxVisualBounds;
    private bool _boxRendererDiagnosticWritten;

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
        _dragPointerTimer = new Forms.Timer { Interval = 16 };
        _dragPointerTimer.Tick += OnDragPointerTimerTick;
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
            _desktopIconLoadCancellation.Cancel();
            _hoverReconcileTimer.Stop();
            _hoverReconcileTimer.Dispose();
            _dragPointerTimer.Stop();
            _dragPointerTimer.Dispose();
            ClearDesktopIconCache();
            _layerBitmap?.Dispose();
            _layerBitmap = null;
            _staticLayerBitmap?.Dispose();
            _staticLayerBitmap = null;
            _dragOverlay.Dispose();
            _hoverOverlay.Dispose();
            _renameEditor?.Dispose();
            _renameEditor = null;
            _renamingItemKey = null;
            ReleaseExternalDragIcon();
            LayeredWindowPresenter.Release(Handle);
            _shellContextMenu?.Dispose();
            _shellContextMenu = null;
            _desktopIconLoadCancellation.Dispose();
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

    internal bool RefreshReleasedItems(IReadOnlyCollection<string> releasedItemKeys)
    {
        if (!_monitor.IsPrimary || releasedItemKeys.Count == 0 || IsDisposed || !IsHandleCreated)
        {
            return false;
        }

        _pendingDesktopReleaseInitialVisualBounds ??= CaptureSettledItemVisualBounds();
        _pendingDesktopReleaseItemKeys.UnionWith(releasedItemKeys);
        _geometryDirty = true;
        _dragBaseReady = false;
        if (!IsDragCompositeActive)
        {
            QueueBoxVisualFrame();
        }
        return true;
    }

    internal bool RefreshRemovedItems(IReadOnlyCollection<string> removedItemKeys)
    {
        if (removedItemKeys.Count == 0 || IsDisposed || !IsHandleCreated)
        {
            return false;
        }

        var removedKeySet = removedItemKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!_items.Any(item => removedKeySet.Contains(item.Item.Key.ToString())))
        {
            return false;
        }

        if (IsDragCompositeActive)
        {
            // If the filesystem move completes before the nested OLE loop has
            // unwound, the normal drag settlement already owns the before and
            // after bounds. Publishing another pending transaction would draw
            // the same dirty regions twice after mouse-up.
            _geometryDirty = true;
            _dragBaseReady = false;
            return true;
        }

        _pendingDesktopReleaseInitialVisualBounds ??= CaptureSettledItemVisualBounds();
        _pendingDesktopReleaseItemKeys.UnionWith(removedItemKeys);
        _geometryDirty = true;
        _dragBaseReady = false;
        QueueBoxVisualFrame();
        return true;
    }

    internal string MonitorId => _monitor.Id;

    internal void SetBoxRenderer(Action<Graphics, RectangleF>? renderer) =>
        _boxRenderer = renderer;

    internal void SetDragBoxRenderer(Action<Graphics, RectangleF>? renderer) =>
        _dragBoxRenderer = renderer;

    internal void SetBoxTransformActive(Func<bool>? provider) =>
        _boxTransformActive = provider;

    internal void SetBoxVisualsInParent(Func<bool>? provider) =>
        _boxVisualsInParent = provider;

    internal void SetBoxDynamicBounds(Func<RectangleF?>? provider) =>
        _boxDynamicBounds = provider;

    internal void SetBoxDynamicDirtyBounds(Func<RectangleF?>? provider) =>
        _boxDynamicDirtyBounds = provider;

    internal void SetBoxDynamicVersion(Func<int>? provider) =>
        _boxDynamicVersion = provider;

    internal void SetBoxDynamicStateUpdater(Action? updater) =>
        _boxDynamicStateUpdate = updater;

    internal void SetBoxPointerHitTest(Func<Point, bool>? hitTest) =>
        _boxPointerHitTest = hitTest;

    internal void SetBoxPartialAnimationOnly(Func<bool>? provider) =>
        _boxPartialAnimationOnly = provider;

    internal static bool ShouldPresentPartialBoxAnimationInParent(
        bool partialAnimationEligible,
        bool boxVisualsInParent,
        bool pointerGhostOverlayActive,
        bool selecting,
        bool staticFrameChanged) =>
        partialAnimationEligible &&
        !selecting &&
        (!pointerGhostOverlayActive ||
         (boxVisualsInParent && !staticFrameChanged));

    internal static bool ShouldHideDragOverlayAfterPartialBoxAnimation(
        bool pointerGhostOverlayActive) =>
        !pointerGhostOverlayActive;

    internal static bool ShouldReuseBoxParentFrame(
        bool visualsInParent,
        bool pointerGhostOverlayActive,
        bool selecting,
        bool staticFrameChanged,
        bool parentFrameAvailable,
        bool lastPresentSucceeded,
        int presentedVersion,
        int currentVersion) =>
        visualsInParent &&
        pointerGhostOverlayActive &&
        !selecting &&
        !staticFrameChanged &&
        parentFrameAvailable &&
        lastPresentSucceeded &&
        presentedVersion == currentVersion;

    internal static bool ShouldKeepDragBaseForPartialBoxUpdate(
        bool dragBaseReady,
        bool partialUpdatePending,
        bool visualsInParent,
        bool pointerGhostOverlayActive,
        bool selecting) =>
        dragBaseReady &&
        partialUpdatePending &&
        visualsInParent &&
        pointerGhostOverlayActive &&
        !selecting;

    internal static bool ShouldSynchronizeDragPointer(
        bool dragActive,
        PointF currentPoint,
        PointF cursorPoint) =>
        dragActive && currentPoint != cursorPoint;

    internal static bool ShouldPublishVirtualBoxGhostFromOle(
        bool keysChanged,
        bool pointerInitialized) =>
        keysChanged || !pointerInitialized;

    internal static Rectangle CalculatePartialBoxAnimationDirtyPixels(
        RectangleF dynamicBounds,
        double scale,
        Size surfaceSize)
    {
        if (dynamicBounds.Width <= 0 || dynamicBounds.Height <= 0 ||
            surfaceSize.Width <= 0 || surfaceSize.Height <= 0)
        {
            return Rectangle.Empty;
        }

        const float antialiasAllowance = 2f;
        var effectiveScale = Math.Max(scale, 0.01d);
        var inflatedBounds = RectangleF.Inflate(
            dynamicBounds,
            antialiasAllowance,
            antialiasAllowance);
        var left = (int)Math.Floor(inflatedBounds.Left * effectiveScale);
        var top = (int)Math.Floor(inflatedBounds.Top * effectiveScale);
        var right = (int)Math.Ceiling(inflatedBounds.Right * effectiveScale);
        var bottom = (int)Math.Ceiling(inflatedBounds.Bottom * effectiveScale);
        return Rectangle.Intersect(
            new Rectangle(0, 0, surfaceSize.Width, surfaceSize.Height),
            Rectangle.FromLTRB(left, top, right, bottom));
    }

    internal static RectangleF CalculateDynamicBoxFrameDirtyBounds(
        RectangleF? previousBounds,
        RectangleF currentBounds) =>
        previousBounds is { } previous
            ? RectangleF.Union(previous, currentBounds)
            : currentBounds;

    internal static IReadOnlyList<RectangleF> CalculateDesktopDropDirtyBounds(
        IReadOnlyDictionary<string, RectangleF> before,
        IReadOnlyDictionary<string, RectangleF> after,
        IReadOnlyCollection<string> draggedItemKeys,
        RectangleF? additionalDirtyBounds = null)
    {
        var draggedKeys = new HashSet<string>(draggedItemKeys, StringComparer.OrdinalIgnoreCase);
        var allKeys = new HashSet<string>(before.Keys, StringComparer.OrdinalIgnoreCase);
        allKeys.UnionWith(after.Keys);
        var dirtyBounds = new List<RectangleF>();
        foreach (var key in allKeys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase))
        {
            var hadBefore = before.TryGetValue(key, out var previousBounds);
            var hasAfter = after.TryGetValue(key, out var currentBounds);
            if (!draggedKeys.Contains(key) &&
                hadBefore &&
                hasAfter &&
                AreEquivalentBounds(previousBounds, currentBounds))
            {
                continue;
            }

            if (hadBefore)
            {
                AddDesktopDropDirtyBounds(dirtyBounds, previousBounds);
            }
            if (hasAfter)
            {
                AddDesktopDropDirtyBounds(dirtyBounds, currentBounds);
            }
        }

        if (additionalDirtyBounds is { } extraBounds)
        {
            AddDesktopDropDirtyBounds(dirtyBounds, extraBounds);
        }

        return dirtyBounds
            .OrderBy(bounds => bounds.Top)
            .ThenBy(bounds => bounds.Left)
            .ToArray();
    }

    private static bool AreEquivalentBounds(RectangleF first, RectangleF second)
    {
        const float tolerance = 0.01f;
        return Math.Abs(first.X - second.X) <= tolerance &&
            Math.Abs(first.Y - second.Y) <= tolerance &&
            Math.Abs(first.Width - second.Width) <= tolerance &&
            Math.Abs(first.Height - second.Height) <= tolerance;
    }

    private static void AddDesktopDropDirtyBounds(List<RectangleF> dirtyBounds, RectangleF candidate)
    {
        if (candidate.Width <= 0 || candidate.Height <= 0)
        {
            return;
        }

        for (var index = 0; index < dirtyBounds.Count;)
        {
            if (!BoundsOverlapOrTouch(dirtyBounds[index], candidate))
            {
                index++;
                continue;
            }

            candidate = RectangleF.Union(candidate, dirtyBounds[index]);
            dirtyBounds.RemoveAt(index);
            index = 0;
        }
        dirtyBounds.Add(candidate);
    }

    private static bool BoundsOverlapOrTouch(RectangleF first, RectangleF second) =>
        first.Left <= second.Right &&
        first.Right >= second.Left &&
        first.Top <= second.Bottom &&
        first.Bottom >= second.Top;

    private bool AreBoxVisualsInParent =>
        _boxVisualsInParent?.Invoke() == true && _dragBoxRenderer is not null;

    private bool IsPointerGhostOverlayActive =>
        (_desktopOleDragActive && _dragStarted) ||
        (_virtualBoxDropTargetEnabled && _boxDropItemKeys.Count > 0);

    private bool ShouldTrackPhysicalDragPointer =>
        (_desktopOleDragActive && _dragStarted) || _virtualBoxDropTargetEnabled;

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

    internal void RequestBoxVisualFrame(RectangleF dirtyBounds)
    {
        if (dirtyBounds.Width <= 0 || dirtyBounds.Height <= 0 ||
            IsDisposed || !IsHandleCreated)
        {
            return;
        }

        _pendingBoxVisualBounds = _pendingBoxVisualBounds is { } pending
            ? RectangleF.Union(pending, dirtyBounds)
            : dirtyBounds;
        if (IsDragCompositeActive)
        {
            // Retain the settled box area until the drag finishes. The active
            // overlay may draw an interim frame, while the final desktop pass
            // commits this same area through a partial layered-window update.
            _boxVisualRenderPending = false;
            RequestDragFrame();
            return;
        }
        if (_boxVisualRenderPending)
        {
            return;
        }

        QueueBoxVisualFrame();
    }

    private void QueueBoxVisualFrame()
    {
        if (_boxVisualRenderPending || IsDisposed || !IsHandleCreated)
        {
            return;
        }

        _boxVisualRenderPending = true;
        try
        {
            BeginInvoke((Action)RenderQueuedBoxVisualFrame);
        }
        catch (InvalidOperationException)
        {
            _boxVisualRenderPending = false;
            _pendingBoxVisualBounds = null;
        }
    }

    private void RenderQueuedBoxVisualFrame()
    {
        if (!_boxVisualRenderPending || IsDisposed)
        {
            return;
        }

        _boxVisualRenderPending = false;
        if (_pendingDesktopReleaseInitialVisualBounds is not null && IsDragCompositeActive)
        {
            return;
        }

        var dirtyBounds = _pendingBoxVisualBounds;
        _pendingBoxVisualBounds = null;
        if (_pendingDesktopReleaseInitialVisualBounds is { } initialVisualBounds)
        {
            var releasedItemKeys = _pendingDesktopReleaseItemKeys.ToArray();
            _pendingDesktopReleaseInitialVisualBounds = null;
            _pendingDesktopReleaseItemKeys.Clear();
            CancelPendingDragRender();
            if (_geometryDirty)
            {
                RebuildGeometry();
                _geometryDirty = false;
            }

            var settledVisualBounds = CaptureSettledItemVisualBounds();
            var releaseDirtyBounds = CalculateDesktopDropDirtyBounds(
                initialVisualBounds,
                settledVisualBounds,
                releasedItemKeys,
                dirtyBounds);
            var partialPresentSucceeded = releaseDirtyBounds.Count > 0;
            foreach (var releaseBounds in releaseDirtyBounds)
            {
                if (PresentSettledPartialFrame(releaseBounds))
                {
                    continue;
                }

                partialPresentSucceeded = false;
                break;
            }

            if (partialPresentSucceeded)
            {
                _dragOverlay.HideOverlay();
                DiagnosticLog.Info(
                    $"Desktop items refreshed partially monitor={_monitor.Id} " +
                    $"dirtyRegions={releaseDirtyBounds.Count} items={releasedItemKeys.Length}");
                return;
            }

            DiagnosticLog.Info(
                $"Desktop item partial refresh fell back monitor={_monitor.Id} " +
                $"dirtyRegions={releaseDirtyBounds.Count}");
            PresentLayer();
            return;
        }

        if (dirtyBounds is not { } bounds || !PresentSettledPartialFrame(bounds))
        {
            PresentLayer();
        }
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
        SynchronizeDragPointersToPhysicalCursor(requestRender: false);
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

    private void UpdatePhysicalDragPointerTracking()
    {
        if (IsDisposed)
        {
            return;
        }

        _dragPointerTimer.Enabled = ShouldTrackPhysicalDragPointer;
    }

    private void OnDragPointerTimerTick(object? sender, EventArgs eventArgs)
    {
        if (IsDisposed || !IsHandleCreated || !ShouldTrackPhysicalDragPointer)
        {
            _dragPointerTimer.Stop();
            return;
        }

        SynchronizeDragPointersToPhysicalCursor(requestRender: true);
    }

    private bool SynchronizeDragPointersToPhysicalCursor(bool requestRender)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return false;
        }

        var cursorPoint = ToDip(PointToClient(Forms.Cursor.Position));
        var changed = false;
        if (ShouldSynchronizeDragPointer(
                _desktopOleDragActive && _dragStarted,
                _dragPointer,
                cursorPoint))
        {
            _dragPointer = cursorPoint;
            UpdateDesktopDragPreview(cursorPoint);
            changed = true;
        }

        var boxGhostActive = _virtualBoxDropTargetEnabled && _boxDropItemKeys.Count > 0;
        if (boxGhostActive &&
            (_boxDragPointer is not { } boxPointer ||
             ShouldSynchronizeDragPointer(true, boxPointer, cursorPoint)))
        {
            _boxDragPointer = cursorPoint;
            changed = true;
        }

        if (changed && requestRender)
        {
            RequestDragRender();
        }
        return changed;
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
        _virtualBoxDragItemKeys = null;
        _virtualBoxDragSession = null;
        UpdatePhysicalDragPointerTracking();
        UpdateDropTargetRegistration();
        if (!enabled)
        {
            ClearBoxDropState();
        }
    }

    // The replacement layer owns the desktop background, so the normal
    // Explorer click monitor must not clear this gesture while WinForms is
    // still delivering captured mouse events.
    internal bool IsPointerInteractionActive =>
        _selecting || _dragStarted || _pressedItem is not null;

    internal bool IsItemDragActive => _dragStarted;

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

    internal bool CommitActiveInlineRename() =>
        _renameEditor?.CommitExternally() == true;

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
        var keepDragBaseForPartialBoxUpdate = ShouldKeepDragBaseForPartialBoxUpdate(
            _dragBaseReady,
            _pendingBoxVisualBounds is not null,
            AreBoxVisualsInParent,
            IsPointerGhostOverlayActive,
            _selecting);
        if (_lastBoxDynamicVersion != boxDynamicVersion)
        {
            _lastBoxDynamicVersion = boxDynamicVersion;
            if (!keepDragBaseForPartialBoxUpdate)
            {
                _dragBaseReady = false;
            }
        }

        if (IsDragCompositeActive)
        {
            _hoverOverlay.HideOverlay();
            _lastHoverOverlayBounds = null;
            EnsureStaticLayerBitmap();
            var staticFrameChanged = !_dragBaseReady;
            if (staticFrameChanged)
            {
                var preparedPartially = !_selecting &&
                    _boxDynamicBounds?.Invoke() is { } dynamicBounds &&
                    TryPrepareDynamicBoxBase(workAreaBounds, dynamicBounds);
                if (!preparedPartially)
                {
                    using var baseGraphics = Graphics.FromImage(_staticLayerBitmap!);
                    DrawSettledLayer(
                        baseGraphics,
                        workAreaBounds,
                        includeBoxDragGhost: false,
                        selectedItemKeys: _selecting ? _selectionBase : null,
                        includeSelectionRectangle: !_selecting);
                }
                _dragBaseReady = true;
            }

            if (!staticFrameChanged &&
                keepDragBaseForPartialBoxUpdate &&
                _pendingBoxVisualBounds is { } partialBoxBounds)
            {
                return PresentActiveBoxPartialFrame(
                    workAreaBounds,
                    partialBoxBounds,
                    boxDynamicVersion);
            }

            // Keep a height animation on the parent from its first frame to
            // its settled frame. With no pointer ghost the existing parent
            // pixels provide the unchanged header; when a ghost is active,
            // wait for the full parent frame before switching to strip-only
            // updates and keep only that ghost in the child overlay.
            if (ShouldPresentPartialBoxAnimationInParent(
                    _boxPartialAnimationOnly?.Invoke() == true,
                    AreBoxVisualsInParent,
                    IsPointerGhostOverlayActive,
                    _selecting,
                    staticFrameChanged))
            {
                return PresentPartialBoxAnimationFrame(workAreaBounds);
            }

            // Keep changing box pixels on the monitor-sized parent while the
            // small child overlay owns only drag ghosts and desktop previews.
            // A translucent box therefore never crosses between two layered
            // windows at transform or hover-animation boundaries.
            if (AreBoxVisualsInParent &&
                !_selecting)
            {
                if (ShouldReuseBoxParentFrame(
                        visualsInParent: true,
                        pointerGhostOverlayActive: IsPointerGhostOverlayActive,
                        selecting: false,
                        staticFrameChanged: staticFrameChanged,
                        parentFrameAvailable: _layerBitmap is not null,
                        lastPresentSucceeded: _lastPresentSucceeded,
                        presentedVersion: _lastPresentedParentBoxDynamicVersion,
                        currentVersion: boxDynamicVersion))
                {
                    // The target-box pixels have not changed. Keep the
                    // already-presented parent frame and move only the small
                    // desktop-icon ghost overlay with the pointer.
                    return PresentDragOverlay(workAreaBounds, _layerBitmap);
                }

                var presented = PresentBoxVisualsInParentFrame(workAreaBounds);
                if (presented)
                {
                    _lastPresentedParentBoxDynamicVersion = boxDynamicVersion;
                }
                return presented;
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
        _lastPresentedParentBoxDynamicVersion = int.MinValue;
        _lastParentBoxVisualBounds = null;

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

    private bool PresentActiveBoxPartialFrame(
        RectangleF workAreaBounds,
        RectangleF dirtyBounds,
        int boxDynamicVersion)
    {
        _pendingBoxVisualBounds = null;
        var dirtyPixels = CalculatePartialBoxAnimationDirtyPixels(
            dirtyBounds,
            _scale,
            ClientSize);
        if (dirtyPixels.Width <= 0 || dirtyPixels.Height <= 0)
        {
            return PresentActiveBoxPartialFallback(workAreaBounds, boxDynamicVersion);
        }

        EnsureLayerBitmap();
        var dirtyDipBounds = new RectangleF(
            (float)(dirtyPixels.X / _scale),
            (float)(dirtyPixels.Y / _scale),
            (float)(dirtyPixels.Width / _scale),
            (float)(dirtyPixels.Height / _scale));
        using (var graphics = Graphics.FromImage(_layerBitmap!))
        {
            graphics.CompositingMode = CompositingMode.SourceCopy;
            using (var clearBrush = new SolidBrush(Color.Transparent))
            {
                graphics.FillRectangle(clearBrush, dirtyPixels);
            }
            graphics.CompositingMode = CompositingMode.SourceOver;
            ConfigureLayerGraphics(graphics, workAreaBounds, fastRender: true);
            graphics.SetClip(dirtyDipBounds, CombineMode.Intersect);
            using var hitTestBackground = new SolidBrush(Color.FromArgb(DesktopHitTestAlpha, Color.Black));
            graphics.FillRectangle(hitTestBackground, dirtyDipBounds);
            DrawDesktopItems(
                graphics,
                includeHoverFeedback: _hoverOverlayUnavailable,
                clipBounds: dirtyDipBounds);
            _boxRenderer?.Invoke(graphics, dirtyDipBounds);
            _dragBoxRenderer?.Invoke(graphics, dirtyDipBounds);
            graphics.ResetTransform();
        }

        _lastPresentSucceeded = LayeredWindowPresenter.TryPresentPartial(
            Handle,
            _layerBitmap!,
            PointToScreen(Point.Empty),
            dirtyPixels,
            out _lastPresentDiagnostic);
        if (!_lastPresentSucceeded)
        {
            return PresentActiveBoxPartialFallback(workAreaBounds, boxDynamicVersion);
        }

        _lastPresentedParentBoxDynamicVersion = boxDynamicVersion;
        return PresentDragOverlay(workAreaBounds, _layerBitmap!);
    }

    private bool PresentActiveBoxPartialFallback(
        RectangleF workAreaBounds,
        int boxDynamicVersion)
    {
        EnsureStaticLayerBitmap();
        using (var baseGraphics = Graphics.FromImage(_staticLayerBitmap!))
        {
            DrawSettledLayer(
                baseGraphics,
                workAreaBounds,
                includeBoxDragGhost: false);
        }
        _dragBaseReady = true;
        var presented = PresentBoxVisualsInParentFrame(workAreaBounds);
        if (presented)
        {
            _lastPresentedParentBoxDynamicVersion = boxDynamicVersion;
        }
        return presented;
    }

    // Icon drags keep the grabbed icons attached to the pointer through the
    // small drag overlay. The composite path is shared with marquee selection,
    // dynamic box visuals, and external file drops.
    private bool IsDragCompositeActive =>
        _selecting ||
        _dragStarted ||
        _externalDragPaths is { Length: > 0 } ||
        _boxTransformActive?.Invoke() == true;

    private bool PresentDragOverlay(
        RectangleF workAreaBounds,
        Bitmap? fallbackBaseBitmap = null)
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
        Bitmap fallbackBitmap;
        Bitmap? baseBitmap;
        if (fallbackBaseBitmap is not null)
        {
            // The parent already contains the complete dynamic box frame.
            // Reuse the other monitor-sized bitmap as the combined fallback
            // so drawing the ghost cannot read from and write to one bitmap.
            EnsureStaticLayerBitmap();
            fallbackBitmap = _staticLayerBitmap!;
            baseBitmap = fallbackBaseBitmap;
            _dragBaseReady = false;
        }
        else
        {
            EnsureLayerBitmap();
            fallbackBitmap = _layerBitmap!;
            baseBitmap = _staticLayerBitmap;
        }

        if (baseBitmap is null)
        {
            _lastPresentSucceeded = false;
            _lastPresentDiagnostic = $"overlay={overlayDiagnostic}; fallback base unavailable";
            return false;
        }

        using (var graphics = Graphics.FromImage(fallbackBitmap))
        {
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.DrawImageUnscaled(baseBitmap, 0, 0);
            graphics.CompositingMode = CompositingMode.SourceOver;
            ConfigureLayerGraphics(graphics, workAreaBounds, fastRender: true);
            DrawDynamicDragVisuals(graphics, workAreaBounds);
            graphics.ResetTransform();
        }
        _lastPresentSucceeded = LayeredWindowPresenter.TryPresent(
            Handle,
            fallbackBitmap,
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

    private bool PresentBoxVisualsInParentFallbackFrame(RectangleF workAreaBounds)
    {
        EnsureLayerBitmap();
        using (var graphics = Graphics.FromImage(_layerBitmap!))
        {
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.DrawImageUnscaled(_staticLayerBitmap!, 0, 0);
            graphics.CompositingMode = CompositingMode.SourceOver;
            ConfigureLayerGraphics(graphics, workAreaBounds, fastRender: false);
            _dragBoxRenderer?.Invoke(graphics, workAreaBounds);
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
                $"Desktop icon dynamic box presentation failed monitor={_monitor.Id}: {_lastPresentDiagnostic}",
                new InvalidOperationException(_lastPresentDiagnostic));
            return false;
        }

        _lastParentBoxVisualBounds = _boxDynamicBounds?.Invoke();
        return PresentDragOverlay(workAreaBounds, _layerBitmap!);
    }

    private bool TryPrepareDynamicBoxBase(
        RectangleF workAreaBounds,
        RectangleF dynamicBounds)
    {
        if (!_lastPresentSucceeded || _layerBitmap is null || _staticLayerBitmap is null)
        {
            return false;
        }

        var dirtyPixels = CalculatePartialBoxAnimationDirtyPixels(
            dynamicBounds,
            _scale,
            ClientSize);
        if (dirtyPixels.Width <= 0 || dirtyPixels.Height <= 0)
        {
            return false;
        }

        var dirtyDipBounds = new RectangleF(
            (float)(dirtyPixels.X / _scale),
            (float)(dirtyPixels.Y / _scale),
            (float)(dirtyPixels.Width / _scale),
            (float)(dirtyPixels.Height / _scale));
        using var graphics = Graphics.FromImage(_staticLayerBitmap);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        using (var clearBrush = new SolidBrush(Color.Transparent))
        {
            graphics.FillRectangle(clearBrush, dirtyPixels);
        }
        graphics.CompositingMode = CompositingMode.SourceOver;
        ConfigureLayerGraphics(graphics, workAreaBounds, fastRender: true);
        graphics.SetClip(dirtyDipBounds, CombineMode.Intersect);
        using var hitTestBackground = new SolidBrush(Color.FromArgb(DesktopHitTestAlpha, Color.Black));
        graphics.FillRectangle(hitTestBackground, dirtyDipBounds);
        DrawDesktopItems(
            graphics,
            includeHoverFeedback: _hoverOverlayUnavailable,
            clipBounds: dirtyDipBounds);
        _boxRenderer?.Invoke(graphics, dirtyDipBounds);
        graphics.ResetTransform();
        return true;
    }

    private bool PresentBoxVisualsInParentFrame(RectangleF workAreaBounds)
    {
        if (_boxDynamicBounds?.Invoke() is not { } currentBounds)
        {
            return PresentBoxVisualsInParentFallbackFrame(workAreaBounds);
        }

        var dirtyBounds = CalculateDynamicBoxFrameDirtyBounds(
            _lastParentBoxVisualBounds,
            currentBounds);
        var dirtyPixels = CalculatePartialBoxAnimationDirtyPixels(
            dirtyBounds,
            _scale,
            ClientSize);
        if (dirtyPixels.Width <= 0 || dirtyPixels.Height <= 0)
        {
            return PresentBoxVisualsInParentFallbackFrame(workAreaBounds);
        }

        EnsureLayerBitmap();
        var dirtyDipBounds = new RectangleF(
            (float)(dirtyPixels.X / _scale),
            (float)(dirtyPixels.Y / _scale),
            (float)(dirtyPixels.Width / _scale),
            (float)(dirtyPixels.Height / _scale));
        using (var graphics = Graphics.FromImage(_layerBitmap!))
        {
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.DrawImage(
                _staticLayerBitmap!,
                dirtyPixels,
                dirtyPixels,
                GraphicsUnit.Pixel);
            graphics.CompositingMode = CompositingMode.SourceOver;
            ConfigureLayerGraphics(graphics, workAreaBounds, fastRender: true);
            graphics.SetClip(dirtyDipBounds, CombineMode.Intersect);
            _dragBoxRenderer?.Invoke(graphics, dirtyDipBounds);
            graphics.ResetTransform();
        }

        _lastPresentSucceeded = LayeredWindowPresenter.TryPresentPartial(
            Handle,
            _layerBitmap!,
            PointToScreen(Point.Empty),
            dirtyPixels,
            out _lastPresentDiagnostic);
        if (!_lastPresentSucceeded)
        {
            return PresentBoxVisualsInParentFallbackFrame(workAreaBounds);
        }

        _lastParentBoxVisualBounds = currentBounds;
        // The parent now contains the complete box frame. Only the drag ghost
        // remains in the child overlay, so no box pixels are handed between
        // windows at animation or transform boundaries.
        return PresentDragOverlay(workAreaBounds, _layerBitmap!);
    }

    /// <summary>
    /// Presents a box-local height or scroll animation through a dirty
    /// rectangle on the parent layered window. This keeps the box in one
    /// compositor surface without repainting the full monitor per frame.
    /// </summary>
    private bool PresentPartialBoxAnimationFrame(RectangleF workAreaBounds)
    {
        EnsureLayerBitmap();
        var dynamicBounds = _boxDynamicDirtyBounds?.Invoke();
        var dirtyPixels = dynamicBounds is { } bounds
            ? CalculatePartialBoxAnimationDirtyPixels(bounds, _scale, ClientSize)
            : Rectangle.Empty;
        if (dirtyPixels.Width <= 0 || dirtyPixels.Height <= 0)
        {
            return PresentPartialBoxAnimationFallbackFrame(workAreaBounds);
        }

        using (var graphics = Graphics.FromImage(_layerBitmap!))
        {
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.DrawImage(
                _staticLayerBitmap!,
                dirtyPixels,
                dirtyPixels,
                GraphicsUnit.Pixel);
            graphics.CompositingMode = CompositingMode.SourceOver;
            ConfigureLayerGraphics(graphics, workAreaBounds, fastRender: true);
            var dirtyDipBounds = new RectangleF(
                (float)(dirtyPixels.X / _scale),
                (float)(dirtyPixels.Y / _scale),
                (float)(dirtyPixels.Width / _scale),
                (float)(dirtyPixels.Height / _scale));
            graphics.SetClip(dirtyDipBounds, CombineMode.Intersect);
            if (AreBoxVisualsInParent)
            {
                // During an icon drag the animated box remains parent-owned,
                // while the child overlay owns only the cursor ghost. Draw the
                // box explicitly because DrawDynamicDragVisuals intentionally
                // skips parent-owned box pixels.
                _dragBoxRenderer?.Invoke(graphics, dirtyDipBounds);
            }
            DrawDynamicDragVisuals(graphics, dirtyDipBounds);
            graphics.ResetTransform();
        }

        _lastPresentSucceeded = LayeredWindowPresenter.TryPresentPartial(
            Handle,
            _layerBitmap!,
            PointToScreen(Point.Empty),
            dirtyPixels,
            out _lastPresentDiagnostic);
        if (!_lastPresentSucceeded)
        {
            return PresentPartialBoxAnimationFallbackFrame(workAreaBounds);
        }
        if (ShouldHideDragOverlayAfterPartialBoxAnimation(IsPointerGhostOverlayActive))
        {
            _dragOverlay.HideOverlay();
            return true;
        }
        return PresentDragOverlay(workAreaBounds, _layerBitmap!);
    }

    private bool PresentSettledPartialFrame(RectangleF dirtyBounds)
    {
        if (!_lastPresentSucceeded || _layerBitmap is null || _staticLayerBitmap is null ||
            IsDragCompositeActive)
        {
            return false;
        }

        var dirtyPixels = CalculatePartialBoxAnimationDirtyPixels(
            dirtyBounds,
            _scale,
            ClientSize);
        if (dirtyPixels.Width <= 0 || dirtyPixels.Height <= 0)
        {
            return false;
        }

        var workAreaBounds = GetDesktopWorkAreaBounds();
        var dirtyDipBounds = new RectangleF(
            (float)(dirtyPixels.X / _scale),
            (float)(dirtyPixels.Y / _scale),
            (float)(dirtyPixels.Width / _scale),
            (float)(dirtyPixels.Height / _scale));
        using (var graphics = Graphics.FromImage(_layerBitmap))
        {
            graphics.CompositingMode = CompositingMode.SourceCopy;
            using (var clearBrush = new SolidBrush(Color.Transparent))
            {
                graphics.FillRectangle(clearBrush, dirtyPixels);
            }
            graphics.CompositingMode = CompositingMode.SourceOver;
            ConfigureLayerGraphics(graphics, workAreaBounds, fastRender: false);
            graphics.SetClip(dirtyDipBounds, CombineMode.Intersect);
            using var hitTestBackground = new SolidBrush(Color.FromArgb(DesktopHitTestAlpha, Color.Black));
            graphics.FillRectangle(hitTestBackground, dirtyDipBounds);
            DrawDesktopItems(
                graphics,
                includeHoverFeedback: _hoverOverlayUnavailable,
                clipBounds: dirtyDipBounds);
            DrawBoxItemDragGhost(graphics);
            _boxRenderer?.Invoke(graphics, dirtyDipBounds);
            graphics.ResetTransform();
        }

        using (var baseGraphics = Graphics.FromImage(_staticLayerBitmap))
        {
            baseGraphics.CompositingMode = CompositingMode.SourceCopy;
            baseGraphics.DrawImage(
                _layerBitmap,
                dirtyPixels,
                dirtyPixels,
                GraphicsUnit.Pixel);
        }

        _lastPresentSucceeded = LayeredWindowPresenter.TryPresentPartial(
            Handle,
            _layerBitmap,
            PointToScreen(Point.Empty),
            dirtyPixels,
            out _lastPresentDiagnostic);
        if (_lastPresentSucceeded)
        {
            _lastParentBoxVisualBounds = null;
            _dragOverlay.HideOverlay();
        }
        return _lastPresentSucceeded;
    }

    private bool PresentPartialBoxAnimationFallbackFrame(RectangleF workAreaBounds)
    {
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
                $"Desktop icon partial box animation presentation failed monitor={_monitor.Id}: {_lastPresentDiagnostic}",
                new InvalidOperationException(_lastPresentDiagnostic));
        }
        else
        {
            // The fallback parent frame already contains both the animated
            // box and the drag ghost, so remove the child copy afterwards.
            _dragOverlay.HideOverlay();
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

        if (string.Equals(_renamingItemKey, item.Item.Key.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            graphics.ResetTransform();
            return;
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
        if (_desktopFolderDropTargetKey is not null)
        {
            DrawDesktopFolderDropHighlight(graphics);
        }
        DrawBoxItemDragGhost(graphics);
        if (!AreBoxVisualsInParent &&
            (_dragStarted || _boxDropItemKeys.Count > 0 ||
             _boxTransformActive?.Invoke() == true))
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
        if (_desktopFolderDropTargetKey is not null &&
            _items.FirstOrDefault(item => string.Equals(
                item.Item.Key.ToString(),
                _desktopFolderDropTargetKey,
                StringComparison.OrdinalIgnoreCase)) is { } folderTarget)
        {
            bounds = UnionVisualBounds(
                bounds,
                CalculateDesktopFolderDropHighlightBounds(GetIconBounds(folderTarget.Bounds)));
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
            if (_boxDragPointer is { } pointer)
            {
                bounds = UnionVisualBounds(bounds, new RectangleF(pointer.X - 40, pointer.Y - 40, 112, 112));
            }
        }

        if ((!_selecting || _dragStarted || _boxDropItemKeys.Count > 0 ||
             _boxTransformActive?.Invoke() == true) &&
            !AreBoxVisualsInParent &&
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
        bool includeBoxDragGhost = true,
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
        if (includeBoxDragGhost)
        {
            DrawBoxItemDragGhost(graphics);
        }
        if (!_boxRendererDiagnosticWritten)
        {
            _boxRendererDiagnosticWritten = true;
            DiagnosticLog.Info(
                $"Icon settled layer box renderer configured={_boxRenderer is not null} " +
                $"monitor={_monitor.Id} clip={workAreaBounds}");
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
            DiagnosticLog.Verbose(
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
        bool includeHoverFeedback = false,
        RectangleF? clipBounds = null)
    {
        selectedItemKeys ??= _selection;
        var itemsToDraw = clipBounds is { } dirtyBounds
            ? _items
                .Where(item => item.Bounds.IntersectsWith(dirtyBounds))
                .OrderBy(item => IsRaisedVisual(item.Item.Key.ToString(), selectedItemKeys))
                .ToArray()
            : _items
                .OrderBy(item => IsRaisedVisual(item.Item.Key.ToString(), selectedItemKeys))
                .ToArray();
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
        // Icon backgrounds and glyphs are painted first, then every label is
        // painted afterwards so an expanded two-line or full name is never
        // covered by the icon pixels of the row below.
        var labelBoundsByKey = new Dictionary<string, RectangleF>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in itemsToDraw)
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

            var hoverTarget =
                _runtime.State.Settings.Appearance.HoverFeedback &&
                string.Equals(_hoveredItemKey, itemKey, StringComparison.OrdinalIgnoreCase);
            var hovered = includeHoverFeedback && hoverTarget;
            var iconBounds = GetIconBounds(entry.Bounds);
            var textBounds = GetItemTextBounds(
                graphics,
                entry.Item.DisplayName,
                entry.Bounds,
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

        foreach (var entry in itemsToDraw)
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
            if (string.Equals(_renamingItemKey, itemKey, StringComparison.OrdinalIgnoreCase))
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

    internal static IReadOnlyList<int> SelectDirtyItemIndexes(
        IReadOnlyList<RectangleF> visualBounds,
        RectangleF dirtyBounds) =>
        Enumerable.Range(0, visualBounds.Count)
            .Where(index => visualBounds[index].IntersectsWith(dirtyBounds))
            .ToArray();

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

        if (_pendingDesktopIconLoads.Add(key))
        {
            _ = LoadDesktopIconAsync(key, _desktopIconCacheVersion);
        }

        // Shell extraction can block while Explorer refreshes its image list.
        // Draw the stock placeholder now and replace only the affected pixels
        // after the worker has copied the resolved image into this surface.
        return ShellIconProvider.GetGenericFileIcon();
    }

    private async Task LoadDesktopIconAsync(
        (string ParsingName, int PixelSize) key,
        int cacheVersion)
    {
        Bitmap? bitmap = null;
        try
        {
            bitmap = await Task.Run(() =>
            {
                var source = _runtime.IconProvider.GetIcon(key.ParsingName, key.PixelSize);
                return source is null ? null : new Bitmap(source);
            }, _desktopIconLoadCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch
        {
            bitmap?.Dispose();
            bitmap = null;
        }

        if (_desktopIconLoadCancellation.IsCancellationRequested || IsDisposed || !IsHandleCreated)
        {
            bitmap?.Dispose();
            return;
        }

        try
        {
            BeginInvoke((Action)(() =>
            {
                if (cacheVersion != _desktopIconCacheVersion || IsDisposed)
                {
                    bitmap?.Dispose();
                    return;
                }
                _pendingDesktopIconLoads.Remove(key);
                if (bitmap is null || _desktopIconCache.ContainsKey(key))
                {
                    bitmap?.Dispose();
                    return;
                }

                _desktopIconCache[key] = bitmap;
                var dirtyBounds = _items
                    .Where(item => string.Equals(item.Item.ParsingName, key.ParsingName, StringComparison.Ordinal))
                    .Select(item => item.Bounds)
                    .Aggregate((RectangleF?)null, (current, bounds) => current is null
                        ? bounds
                        : RectangleF.Union(current.Value, bounds));
                if (dirtyBounds is { } dirty)
                {
                    RequestBoxVisualFrame(dirty);
                }
            }));
        }
        catch (InvalidOperationException)
        {
            bitmap?.Dispose();
        }
    }

    private int ClearDesktopIconCache()
    {
        _desktopIconCacheVersion++;
        var count = _desktopIconCache.Count;
        foreach (var bitmap in _desktopIconCache.Values)
        {
            bitmap.Dispose();
        }
        _desktopIconCache.Clear();
        _pendingDesktopIconLoads.Clear();
        return count;
    }

    // The grabbed icons stay attached to the pointer, like the native desktop
    // ghost. The moving overlay contains only their icons and labels.
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

    private void DrawBoxItemDragGhost(Graphics graphics)
    {
        if (_boxDropItemKeys.Count == 0 || _boxDragPointer is not { } pointer)
        {
            return;
        }
        DrawBoxDragGhost(graphics, pointer);
    }

    private void DrawBoxDragGhost(Graphics graphics, PointF pointer)
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
        _runtime.CommitActiveDesktopInlineRename();
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
                _runtime.ClearDesktopSelection();
                if (hoverChanged)
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
            var contextTargetSelected = _selection.Contains(key);
            _runtime.PrepareDesktopSelection(
                this,
                DesktopSelectionPolicy.PreserveExistingSelection(
                    DesktopSelectionGesture.ContextItem,
                    additive: false,
                    contextTargetSelected));
            var selectionChanged = false;
            if (!contextTargetSelected)
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

        if (item is null)
        {
            var additive = (Forms.Control.ModifierKeys & Forms.Keys.Control) != 0;
            _runtime.PrepareDesktopSelection(
                this,
                DesktopSelectionPolicy.PreserveExistingSelection(
                    DesktopSelectionGesture.Marquee,
                    additive,
                    targetAlreadySelected: false));
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
        var targetAlreadySelected = _selection.Contains(itemKey);
        _runtime.PrepareDesktopSelection(
            this,
            DesktopSelectionPolicy.PreserveExistingSelection(
                DesktopSelectionGesture.PrimaryItem,
                controlPressed,
                targetAlreadySelected));
        if (controlPressed && targetAlreadySelected)
        {
            _selection.Remove(itemKey);
            _pressedItem = null;
            _selecting = false;
            Capture = false;
            PresentLayer();
            return;
        }

        if (!controlPressed && !targetAlreadySelected)
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
            EndDesktopDragAndPresent();
        }

        _pressedItem = null;
        Capture = false;
        _runtime.CompleteDesktopPointerInteraction();
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
            CancelPendingDragRender();
            EndDesktopDragAndPresent();
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
            var folderTarget = GetDesktopFolderDropTarget(dropPoint, desktopDrag);
            var folderTargetChanged = SetDesktopFolderDropTarget(folderTarget);
            var overRecycleBin = folderTarget is null &&
                                 IsOverRecycleBin(dropPoint) &&
                                 DraggedKeysAreFileSystemItems(desktopDrag);
            if (overRecycleBin != _overRecycleBin || folderTargetChanged)
            {
                _overRecycleBin = overRecycleBin;
                ClearBoxDropState();
                RequestDragRender();
            }
            UpdateDesktopOleDropPreview(new Point(eventArgs.X, eventArgs.Y));
            eventArgs.Effect = ResolveDesktopDragEffect(
                eventArgs.AllowedEffect,
                acceptsFolder: folderTarget is not null,
                overRecycleBin: _overRecycleBin,
                controlPressed: (eventArgs.KeyState & 8) != 0);
            return;
        }

        if (!TryGetVirtualBoxDrag(eventArgs, out var itemKeys, out _))
        {
            if (TryGetExternalFileDrop(eventArgs, out var externalPaths))
            {
                OnExternalFileDragOver(eventArgs, externalPaths);
                return;
            }
            ClearBoxDropState();
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
        var acceptsDrop = UpdateBoxDropPlacement(point, itemKeys);
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
        if (wasEmpty)
        {
            DiagnosticLog.Info(
                $"External drag entered count={paths.Length} recycle={overRecycleBin}");
        }
        RequestDragRender();
        // External folder drags default to a filesystem move (matching
        // Explorer); holding Ctrl forces a copy. The recycle bin always
        // advertises Move so a drop there deletes the sources.
        var controlPressed = (eventArgs.KeyState & 8) != 0;
        var preferredEffect = controlPressed
            ? Forms.DragDropEffects.Copy
            : Forms.DragDropEffects.Move;
        var fallbackEffect = controlPressed
            ? Forms.DragDropEffects.Move
            : Forms.DragDropEffects.Copy;
        eventArgs.Effect = overRecycleBin
            ? (eventArgs.AllowedEffect & Forms.DragDropEffects.Move) != 0
                ? Forms.DragDropEffects.Move
                : Forms.DragDropEffects.None
            : (eventArgs.AllowedEffect & preferredEffect) != 0
                ? preferredEffect
                : (eventArgs.AllowedEffect & fallbackEffect) != 0
                    ? fallbackEffect
                    : Forms.DragDropEffects.None;
    }

    // The box window owns the OLE drag route while the pointer is over it,
    // so this surface receives no DragOver there and its ghost would freeze
    // (or be cleared by DragLeave). The box forwards its drag state here so
    // the dragged card keeps following the pointer into the box.
    internal void ForwardDragFromBox(
        PointF pointDip,
        IReadOnlyList<string>? externalPaths,
        IReadOnlyList<string>? itemKeys)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        // A desktop icon drag also carries FileDrop paths in its payload, so
        // the active desktop OLE drag must win over the external-file branch:
        // otherwise the real drag ghost freezes at its last position outside
        // the box while a stray external card takes over inside it.
        if (itemKeys is { Count: > 0 } && _dragStarted && _desktopOleDragActive)
        {
            _dragPointer = pointDip;
            _dragPointerOverBox = true;
            UpdateDesktopDragPreview(_dragPointer);
            RequestDragRender();
            return;
        }

        if (itemKeys is { Count: > 0 } && _virtualBoxDropTargetEnabled)
        {
            var keysChanged = !_boxDropItemKeys.SetEquals(itemKeys);
            if (keysChanged)
            {
                _boxDropItemKeys.Clear();
                _boxDropItemKeys.UnionWith(itemKeys);
                _boxDragPrimaryKey = itemKeys.FirstOrDefault();
            }
            var publishGhost = ShouldPublishVirtualBoxGhostFromOle(
                keysChanged,
                _boxDragPointer is not null);
            if (publishGhost)
            {
                _boxDragPointer = pointDip;
            }
            _boxDropTargetCell = null;
            if (publishGhost)
            {
                RequestDragRender();
            }
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
        _desktopFolderDropTargetKey = null;
        ClearExternalDragPreview();
        // Preserve a box-item ghost across the OLE handoff into a box window.
        // SetVirtualBoxDropTargetEnabled(false) owns the final cleanup.
        if (!_virtualBoxDropTargetEnabled)
        {
            ClearBoxDropState();
        }
        RequestDragRender();
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
                    var folderTarget = GetDesktopFolderDropTarget(dropPoint, desktopDrag);
                    _desktopFolderDropTargetKey = null;
                    if (folderTarget is not null)
                    {
                        _overRecycleBin = false;
                        RequestDragRender();
                        await CompleteDesktopFolderDropAsync(
                            desktopDrag,
                            folderTarget.Item,
                            move: (eventArgs.KeyState & 8) == 0);
                    }
                    else if (_overRecycleBin && IsOverRecycleBin(dropPoint))
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
                    _dragOverlay.HideOverlay();
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
            if (!UpdateBoxDropPlacement(point, itemKeys) ||
                _boxDropTargetCell is not { } targetCell)
            {
                return;
            }

            var layout = BuildBoxDropDesktopLayout(itemKeys, targetCell);
            if (layout is null)
            {
                return;
            }
            // Mark the source session before committing the state transition.
            // Keeping the marker first preserves the contract if the release
            // later gains asynchronous shell work.
            dragSession.HandledByDesktop = true;
            var released = await _runtime.ReleaseAssignedItemsToDesktopAtDropAsync(
                itemKeys,
                layout);
            if (!released)
            {
                return;
            }
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("Desktop drop of box items failed", exception);
        }
        finally
        {
            ClearBoxDropState();
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
            !_monitor.IsPrimary)
        {
            return false;
        }

        if (_virtualBoxDragItemKeys is { } cachedKeys &&
            _virtualBoxDragSession is { } cachedSession)
        {
            itemKeys = cachedKeys;
            dragSession = cachedSession;
            return true;
        }

        if (eventArgs.Data is null ||
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
        _virtualBoxDragItemKeys = itemKeys;
        _virtualBoxDragSession = session;
        return true;
    }

    private bool UpdateBoxDropPlacement(PointF point, IReadOnlyList<string> itemKeys)
    {
        var keysChanged = !_boxDropItemKeys.SetEquals(itemKeys);
        if (keysChanged)
        {
            _boxDropItemKeys.Clear();
            _boxDropItemKeys.UnionWith(itemKeys);
            _boxDragPrimaryKey = itemKeys.FirstOrDefault();
        }
        var publishGhost = ShouldPublishVirtualBoxGhostFromOle(
            keysChanged,
            _boxDragPointer is not null);
        if (publishGhost)
        {
            // OLE can deliver DragOver far faster than the monitor refresh
            // rate. It owns target-cell hit testing, but the 16 ms physical
            // pointer timer owns subsequent ghost frames so layered-window
            // uploads cannot flood and stall the nested drag loop.
            _boxDragPointer = point;
        }
        if (!_monitor.IsPrimary)
        {
            _boxDropTargetCell = null;
            return false;
        }

        var target = GetCellAtPoint(point);
        _boxDropTargetCell = target;
        if (publishGhost)
        {
            RequestDragRender();
        }
        return target is not null;
    }

    private IReadOnlyDictionary<string, DesktopIconLayoutSnapshot>? BuildBoxDropDesktopLayout(
        IReadOnlyList<string> itemKeys,
        GridCell requestedTarget)
    {
        var grid = CreateCurrentGrid();
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
            return null;
        }

        var layout = _runtime.State.DesktopIconLayout.ToDictionary(
            pair => pair.Key,
            pair => new DesktopIconLayoutSnapshot
            {
                MonitorId = pair.Value.MonitorId,
                Column = pair.Value.Column,
                Row = pair.Value.Row
            },
            StringComparer.OrdinalIgnoreCase);
        foreach (var (key, placement) in result.Placements)
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

    private void ClearBoxDropState()
    {
        if (_boxDropTargetCell is null && _boxDropItemKeys.Count == 0 &&
            _boxDragPointer is null && _boxDragPrimaryKey is null)
        {
            return;
        }

        _boxDropTargetCell = null;
        _boxDropItemKeys.Clear();
        _boxDragPointer = null;
        _boxDragPrimaryKey = null;
        RequestDragRender();
    }

    private void BeginDesktopDrag(string anchorKey)
    {
        _desktopDragInitialVisualBounds = null;
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
        _desktopDragInitialVisualBounds = CaptureSettledItemVisualBounds();
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
            // The final frame below already rebuilds this surface from the
            // new layout. A workspace refresh would first clear every icon
            // bitmap and make the whole desktop visibly reload.
            _runtime.ResetDesktopIconLayoutForAutoArrange(refreshWorkspace: false);
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
        // Commit persistence without a second full workspace refresh. The
        // caller presents the settled desktop exactly once after ending the
        // drag, while retaining the icon bitmap cache.
        _runtime.SetDesktopIconLayout(layout, refreshWorkspace: false);
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
        _desktopFolderDropTargetKey = null;
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

    private void EndDesktopDragAndPresent()
    {
        var initialVisualBounds = _desktopDragInitialVisualBounds;
        var draggedItemKeys = _dragItemKeys.ToArray();
        var pendingBoxVisualBounds = _pendingBoxVisualBounds;
        _desktopDragInitialVisualBounds = null;
        _pendingBoxVisualBounds = null;
        _boxVisualRenderPending = false;
        EndDesktopDrag();
        // EndDesktopDrag can invalidate a queued ghost frame while box drop
        // previews are being cleared. It must not run after the settled drop
        // frame and turn a local update back into a full monitor present.
        CancelPendingDragRender();

        if (initialVisualBounds is null || draggedItemKeys.Length == 0)
        {
            PresentLayer();
            return;
        }

        if (_geometryDirty)
        {
            RebuildGeometry();
            _geometryDirty = false;
        }
        var settledVisualBounds = CaptureSettledItemVisualBounds();
        var dirtyBounds = CalculateDesktopDropDirtyBounds(
            initialVisualBounds,
            settledVisualBounds,
            draggedItemKeys,
            pendingBoxVisualBounds);
        var partialPresentSucceeded = dirtyBounds.Count > 0;
        foreach (var bounds in dirtyBounds)
        {
            if (PresentSettledPartialFrame(bounds))
            {
                continue;
            }

            partialPresentSucceeded = false;
            break;
        }

        if (!partialPresentSucceeded)
        {
            DiagnosticLog.Info(
                $"Desktop icon drag settled with full fallback monitor={_monitor.Id} " +
                $"dirtyRegions={dirtyBounds.Count}");
            PresentLayer();
            return;
        }

        _dragBaseReady = false;
        _dragOverlay.HideOverlay();
        var workAreaBounds = GetDesktopWorkAreaBounds();
        if (!PresentHoverOverlay(workAreaBounds))
        {
            RequestDragRender();
        }
        DiagnosticLog.Info(
            $"Desktop icon drag settled partially monitor={_monitor.Id} " +
            $"dirtyRegions={dirtyBounds.Count} dragged={draggedItemKeys.Length}");
    }

    private IReadOnlyDictionary<string, RectangleF> CaptureSettledItemVisualBounds()
    {
        var visualBounds = new Dictionary<string, RectangleF>(StringComparer.OrdinalIgnoreCase);
        using var measureBitmap = DesktopLayerBitmapFactory.Create(1, 1);
        using var measureGraphics = Graphics.FromImage(measureBitmap);
        using var font = ResolveIconLabelFont();
        foreach (var entry in _items)
        {
            var key = entry.Item.Key.ToString();
            var selected = _selection.Contains(key);
            var hovered = _runtime.State.Settings.Appearance.HoverFeedback &&
                string.Equals(_hoveredItemKey, key, StringComparison.OrdinalIgnoreCase);
            var iconBounds = GetIconBounds(entry.Bounds);
            var textBounds = GetItemTextBounds(
                measureGraphics,
                entry.Item.DisplayName,
                entry.Bounds,
                iconBounds,
                font,
                DesktopIconLabelDisplayPolicy.ShowsFullLabel(selected, hovered));
            var textHitBounds = GetTextHitBounds(
                measureGraphics,
                entry.Item.DisplayName,
                textBounds,
                font);
            visualBounds[key] = GetItemVisualBounds(iconBounds, textHitBounds);
        }
        return visualBounds;
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
        DiagnosticLog.Info(
            $"Desktop context menu selection={_selection.Count} shells={selectedItems.Length} " +
            $"clicked={item.DisplayName}");
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

        var completedEffect = Forms.DragDropEffects.None;
        _desktopOleDragActive = true;
        UpdatePhysicalDragPointerTracking();
        UpdateDropTargetRegistration();
        try
        {
            // File uploads and other external targets are copy operations.
            // CrabDesk's private drop targets use the accompanying session to
            // perform virtual placement and assignment without moving files.
            Forms.Cursor.Current = Forms.Cursors.Default;
            completedEffect = DoDragDrop(data, ExternalFileDropEffects);
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("Desktop item OLE drag loop failed", exception);
        }
        finally
        {
            _desktopOleDragActive = false;
            UpdatePhysicalDragPointerTracking();
            UpdateDropTargetRegistration();
            CancelPendingDragRender();
            _pressedItem = null;
            EndDesktopDragAndPresent();
            Capture = false;
            if (!dragSession.HandledByBox &&
                !dragSession.HandledByDesktop &&
                (completedEffect & Forms.DragDropEffects.Move) != 0)
            {
                _ = _runtime.ReconcileExternalDesktopMoveAsync(selectedItems);
            }
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

    private DesktopIconGeometry? GetDesktopFolderDropTarget(
        PointF point,
        DesktopIconSurfaceDragSession dragSession)
    {
        var candidate = GetItemAt(point);
        if (candidate is null)
        {
            return null;
        }

        var draggedKeySet = dragSession.ItemKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var draggedItems = _items
            .Where(item => draggedKeySet.Contains(item.Item.Key.ToString()))
            .Select(item => item.Item)
            .ToArray();
        return draggedItems.Length == draggedKeySet.Count &&
               DesktopFolderDropPolicy.CanAccept(draggedItems, candidate.Item)
            ? candidate
            : null;
    }

    private bool SetDesktopFolderDropTarget(DesktopIconGeometry? target)
    {
        var nextKey = target?.Item.Key.ToString();
        if (string.Equals(
                _desktopFolderDropTargetKey,
                nextKey,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        _desktopFolderDropTargetKey = nextKey;
        return true;
    }

    private async Task CompleteDesktopFolderDropAsync(
        DesktopIconSurfaceDragSession dragSession,
        DesktopItemRef folderTarget,
        bool move)
    {
        dragSession.HandledByDesktop = true;
        var draggedKeySet = dragSession.ItemKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var draggedItems = _items
            .Where(item => draggedKeySet.Contains(item.Item.Key.ToString()))
            .Select(item => item.Item)
            .ToArray();
        if (draggedItems.Length != draggedKeySet.Count ||
            draggedItems.Any(item => item.FileSystemPath is null) ||
            folderTarget.FileSystemPath is not { } targetPath)
        {
            return;
        }

        try
        {
            if (!Directory.Exists(targetPath))
            {
                throw new DirectoryNotFoundException($"目标文件夹“{folderTarget.DisplayName}”已不存在。");
            }

            var imported = await _runtime.ImportDesktopItemsIntoFolderAsync(
                draggedItems,
                targetPath,
                move);
            DiagnosticLog.Info(
                $"Desktop folder drop target={targetPath} move={move} " +
                $"ok={imported.SucceededCount} failed={imported.FailedCount}");
            ShowDesktopFolderDropFailures(imported);
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("Failed to drop desktop items into a desktop folder", exception);
            DesktopConfirmationDialog.ShowMessage(
                this,
                _runtime.IsDarkTheme,
                move ? "移动失败" : "复制失败",
                exception.Message,
                DesktopDialogKind.Error);
        }
    }

    private void ShowDesktopFolderDropFailures(FileImportBatchResult result)
    {
        if (!result.HasFailures)
        {
            return;
        }

        var details = string.Join(
            Environment.NewLine,
            result.FailedItems.Take(3).Select(item =>
                $"- {Path.GetFileName(item.SourcePath)}: {item.ErrorMessage}"));
        if (result.FailedCount > 3)
        {
            details += Environment.NewLine + $"另有 {result.FailedCount - 3} 项未处理。";
        }

        DesktopConfirmationDialog.ShowMessage(
            this,
            _runtime.IsDarkTheme,
            "操作未完成",
            $"已处理 {result.SucceededCount} 项，{result.FailedCount} 项失败。" +
            $"{Environment.NewLine}{Environment.NewLine}{details}",
            DesktopDialogKind.Warning);
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
            DesktopConfirmationDialog.ShowMessage(
                this,
                _runtime.IsDarkTheme,
                "删除失败",
                exception.Message,
                DesktopDialogKind.Error);
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
            var existingDesktopPaths = paths
                .Where(path => IsExistingDesktopItemPath(path, _runtime.Items))
                .ToArray();
            if (existingDesktopPaths.Length > 0)
            {
                PlaceExistingDesktopPathsAtPoint(existingDesktopPaths, dropPointDip);
            }

            var existingPathSet = existingDesktopPaths
                .Select(Path.GetFullPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var externalPaths = paths
                .Where(path => !existingPathSet.Contains(Path.GetFullPath(path)))
                .ToArray();
            if (externalPaths.Length == 0)
            {
                DiagnosticLog.Info(
                    $"External desktop drop reused existing items={existingDesktopPaths.Length}");
                return;
            }

            _runtime.RegisterTargetedDesktopRefresh(
                externalPaths.Append(desktopDirectory));
            var result = await _runtime.FileOperations.ImportAsync(
                externalPaths,
                desktopDirectory,
                move);
            if (result.SuccessfulItems.Count > 0)
            {
                _runtime.RegisterTargetedDesktopRefresh(
                    result.SuccessfulItems.SelectMany(item =>
                        new[] { item.SourcePath, item.DestinationPath! }));
            }
            if (result.ImportedPaths.Count > 0)
            {
                await _runtime.RefreshItemsSnapshotAsync(applyDesktopRules: false);
                if (!PlaceDroppedItemsAtPoint(result.ImportedPaths, dropPointDip))
                {
                    _runtime.RefreshDesktopSurfaces();
                }
            }
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
            DesktopConfirmationDialog.ShowMessage(
                this,
                _runtime.IsDarkTheme,
                "导入失败",
                exception.Message,
                DesktopDialogKind.Error);
        }
    }

    // Newly imported desktop files land at the drop point (native "place
    // where you point" behavior) when a manual layout is active: the first
    // item takes the cell under the pointer and any existing occupants shift
    // forward using the same insertion rule as a box-to-desktop drop.
    private bool PlaceDroppedItemsAtPoint(
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
            return false;
        }

        var grid = CreateCurrentGrid();
        if (grid.ColumnCount == 0 || grid.RowCount == 0)
        {
            DiagnosticLog.Info(
                $"Place dropped items skipped: no grid {grid.ColumnCount}x{grid.RowCount}");
            return false;
        }
        if (GetCellAtPoint(dropPointDip) is not { } targetCell)
        {
            DiagnosticLog.Info(
                $"Place dropped items skipped: point outside grid bounds " +
                $"grid={grid.Bounds.X:0},{grid.Bounds.Y:0},{grid.Bounds.Width:0},{grid.Bounds.Height:0}");
            return false;
        }

        var importedItemKeys = ResolveImportedItemKeysInDropOrder(
            importedPaths,
            _runtime.Items);
        if (importedItemKeys.Count == 0)
        {
            DiagnosticLog.Info(
                $"Place dropped items skipped: no matching items in runtime snapshot " +
                $"items={_runtime.Items.Count}");
            return false;
        }
        DiagnosticLog.Info(
            $"Place dropped items target cell={targetCell.Column},{targetCell.Row} matched={importedItemKeys.Count}");

        var importedKeySet = importedItemKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stationary = _items
            .Where(item => !importedKeySet.Contains(item.Key))
            .Select(item => new DesktopIconGridItem(
                item.Key,
                new DesktopIconGridCell(item.Cell.Column, item.Cell.Row)));
        var result = DesktopIconDragLayoutEngine.CalculateInsertion(
            stationary,
            importedItemKeys,
            new DesktopIconGridCell(targetCell.Column, targetCell.Row),
            grid.ColumnCount,
            grid.RowCount);
        if (!result.IsValid)
        {
            DiagnosticLog.Info(
                $"Place dropped items skipped: insertion layout unavailable " +
                $"grid={grid.ColumnCount}x{grid.RowCount} items={_items.Count} imported={importedItemKeys.Count}");
            return false;
        }

        var next = new Dictionary<string, DesktopIconLayoutSnapshot>(
            _runtime.State.DesktopIconLayout,
            StringComparer.OrdinalIgnoreCase);
        foreach (var (itemKey, cell) in result.Placements)
        {
            next[itemKey] = new DesktopIconLayoutSnapshot
            {
                MonitorId = _monitor.Id,
                Column = cell.Column,
                Row = cell.Row
            };
        }

        DiagnosticLog.Info(
            $"Place dropped items applying layout entries={next.Count} " +
            $"cells={string.Join(",", importedItemKeys.Select(itemKey => $"{itemKey}=({next[itemKey].Column},{next[itemKey].Row})"))}");
        if (!_runtime.SetDesktopIconLayout(next, refreshWorkspace: false))
        {
            return false;
        }

        _runtime.RefreshDesktopItemsChanged(importedItemKeys);
        return true;
    }

    private bool PlaceExistingDesktopPathsAtPoint(
        IReadOnlyList<string> existingDesktopPaths,
        PointF dropPointDip)
    {
        if (existingDesktopPaths.Count == 0 ||
            _runtime.IsDesktopAutoArrangeEnabled ||
            !_monitor.IsPrimary)
        {
            return false;
        }

        var grid = CreateCurrentGrid();
        if (GetCellAtPoint(dropPointDip) is not { } targetCell)
        {
            return false;
        }

        var movingKeys = ResolveImportedItemKeysInDropOrder(
            existingDesktopPaths,
            _runtime.Items);
        if (movingKeys.Count == 0)
        {
            return false;
        }

        var result = DesktopIconDragLayoutEngine.Calculate(
            _items.Select(item => new DesktopIconGridItem(
                item.Key,
                new DesktopIconGridCell(item.Cell.Column, item.Cell.Row))),
            movingKeys,
            movingKeys[0],
            new DesktopIconGridCell(targetCell.Column, targetCell.Row),
            grid.ColumnCount,
            grid.RowCount);
        if (!result.IsValid)
        {
            return false;
        }

        var next = new Dictionary<string, DesktopIconLayoutSnapshot>(
            _runtime.State.DesktopIconLayout,
            StringComparer.OrdinalIgnoreCase);
        foreach (var (itemKey, cell) in result.Placements)
        {
            next[itemKey] = new DesktopIconLayoutSnapshot
            {
                MonitorId = _monitor.Id,
                Column = cell.Column,
                Row = cell.Row
            };
        }

        if (_runtime.SetDesktopIconLayout(next, refreshWorkspace: false))
        {
            _runtime.RefreshDesktopItemsChanged(movingKeys);
        }
        return true;
    }

    internal static bool IsExistingDesktopItemPath(
        string path,
        IReadOnlyList<DesktopItemRef> runtimeItems)
    {
        var normalizedPath = Path.GetFullPath(path);
        return runtimeItems.Any(item =>
            item.FileSystemPath is { } itemPath &&
            string.Equals(
                Path.GetFullPath(itemPath),
                normalizedPath,
                StringComparison.OrdinalIgnoreCase));
    }

    internal static IReadOnlyList<string> ResolveImportedItemKeysInDropOrder(
        IReadOnlyList<string> importedPaths,
        IReadOnlyList<DesktopItemRef> runtimeItems)
    {
        var itemsByPath = new Dictionary<string, DesktopItemRef>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in runtimeItems)
        {
            if (item.FileSystemPath is null)
            {
                continue;
            }

            itemsByPath.TryAdd(Path.GetFullPath(item.FileSystemPath), item);
        }

        var orderedKeys = new List<string>(importedPaths.Count);
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var importedPath in importedPaths)
        {
            if (!itemsByPath.TryGetValue(Path.GetFullPath(importedPath), out var item))
            {
                continue;
            }

            var itemKey = item.Key.ToString();
            if (seenKeys.Add(itemKey))
            {
                orderedKeys.Add(itemKey);
            }
        }
        return orderedKeys;
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
            DesktopConfirmationDialog.ShowMessage(
                this,
                _runtime.IsDarkTheme,
                "删除失败",
                exception.Message,
                DesktopDialogKind.Error);
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

    private void DrawDesktopFolderDropHighlight(Graphics graphics)
    {
        var target = _items.FirstOrDefault(item => string.Equals(
            item.Item.Key.ToString(),
            _desktopFolderDropTargetKey,
            StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            return;
        }

        var selectionColor = ParseColor(
            _runtime.State.Settings.Appearance.SelectionColor,
            Color.FromArgb(74, 91, 177));
        var highlightBounds = CalculateDesktopFolderDropHighlightBounds(
            GetIconBounds(target.Bounds));
        using var fill = new SolidBrush(Color.FromArgb(72, selectionColor));
        using var border = new Pen(Color.FromArgb(242, selectionColor), 2f);
        using var path = RoundedRectangle(highlightBounds, 10);
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);
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
        var isSlowDoubleClick = SlowDoubleClickRenamePolicy.IsSlowDoubleClick(
            _lastRenameClickKey,
            _lastRenameClickUtc,
            key,
            now,
            Forms.SystemInformation.DoubleClickTime);
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
        if (elapsed > SlowDoubleClickRenamePolicy.RenameLimitMilliseconds)
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
            DesktopConfirmationDialog.ShowMessage(
                this,
                _runtime.IsDarkTheme,
                "重命名失败",
                exception.Message,
                DesktopDialogKind.Error);
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
        _renamingItemKey = item.Key.ToString();
        RequestRender();
        try
        {
            return await _renameEditor.ShowAsync(
                screenLocation,
                new Size(
                    (int)Math.Round(labelBounds.Width * scale),
                    (int)Math.Round(labelBounds.Height * scale)),
                item.DisplayName,
                selectStem,
                _runtime.IsDarkTheme,
                labelFont,
                wordWrap: true);
        }
        finally
        {
            _renamingItemKey = null;
            RequestRender();
        }
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
        var lineHeight = Math.Max(1, font.GetHeight(measureGraphics));
        var workArea = GetDesktopWorkAreaBounds();
        var width = DesktopRenameEditor.CalculateEditorWidth(
            Math.Max(1, textBounds.Width),
            Math.Max(48, workArea.Width - 8));
        var centerX = textBounds.X + textBounds.Width / 2;
        var left = Math.Max(
            workArea.Left + 2,
            Math.Min(centerX - width / 2, workArea.Right - width - 2));
        // Keep the static label width, then grow only downward for however
        // many lines the complete name needs at that same wrap width.
        var wrappedTextHeight = MeasureFullLabelHeight(
            measureGraphics,
            entry.Item.DisplayName,
            font,
            width);
        var height = DesktopRenameEditor.CalculateEditorHeight(
            wrappedTextHeight,
            lineHeight,
            Math.Max(1, workArea.Height - 2));
        var top = Math.Max(workArea.Top + 1, textBounds.Y);
        if (top + height > workArea.Bottom - 1)
        {
            top = Math.Max(workArea.Top + 1, workArea.Bottom - height - 1);
        }
        return new RectangleF(left, top, width, height);
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
