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
    internal const string ItemKeysFormat = "CrabDesk.DesktopItemKeys";
    internal const string SourceBoxFormat = "CrabDesk.SourceBoxId";
    internal const string DragSessionFormat = "CrabDesk.InternalDragSession";
    private const int WmNcHitTest = 0x0084;
    private const int WmMouseActivate = 0x0021;
    private const int WmContextMenu = 0x007B;
    private const int WmEraseBkgnd = 0x0014;
    private const int WmMouseWheel = 0x020A;
    private const int WsClipSiblings = 0x04000000;
    private const int WsExLayered = 0x00080000;
    private const int WsExNoParentNotify = 0x00000004;
    // Keep the hover intent guard short enough that expansion feels immediate
    // while still filtering out a quick pointer pass over the header.
    private const int HoverExpansionDelayMilliseconds = 45;
    private const int HoverCollapseDelayMilliseconds = 120;
    private const double ScrollAnimationDurationMilliseconds = 190;
    private const double ScrollEaseExponent = 2.2;
    private const double ScrollWheelStepFraction = 0.75;
    private const int ScrollHoverResumeDelayMilliseconds = 120;
    // Budgeted in frames, not in milliseconds: the frame clock can only deliver
    // one frame per ~15.6 ms system tick, so 150 ms was a nine-frame animation
    // at best and a five-frame one at the rate the clock actually ran. 220 ms is
    // about fourteen frames, which is what makes the travel read as motion
    // rather than as a few large steps. The floor matters for the same reason:
    // 60 ms could only ever be four frames, so short distances snapped.
    private const int BoxHeightAnimationMilliseconds = 220;
    private const int MinimumBoxHeightAnimationMilliseconds = 110;
    private const int DragRenderCoalesceMilliseconds = 16;
    private const float MappedFolderTabBarHeight = (float)DesktopItemLayoutEngine.TabBarHeight;
    private const int CompactGridLabelLineCount = 2;
    private static readonly IntPtr HtTransparent = new(-1);
    private static readonly IntPtr MaNoActivate = new(3);
    private static readonly (string Name, string Hex)[] AccentPalette =
    [
        ("海蓝", "#FF4EA1D3"),
        ("青绿", "#FF2AA198"),
        ("草绿", "#FF4CAF72"),
        ("明黄", "#FFF2B84B"),
        ("暖橙", "#FFF28C48"),
        ("珊瑚红", "#FFE46464"),
        ("薰衣草紫", "#FF8B72D6"),
        ("莓果粉", "#FFE66AA2"),
        ("雾灰", "#FF7B8794")
    ];
    private static readonly MappedFolderItemCategory[] MappedFolderTabOrder =
    [
        MappedFolderItemCategory.Folder,
        MappedFolderItemCategory.Image,
        MappedFolderItemCategory.Document,
        MappedFolderItemCategory.Archive,
        MappedFolderItemCategory.Other
    ];

    internal static IReadOnlyList<Guid> OrderFocusedGeometry(
        IEnumerable<Guid> orderedBoxIds,
        Guid focusedBoxId) =>
        orderedBoxIds
            .Where(boxId => boxId != focusedBoxId)
            .Append(focusedBoxId)
            .ToArray();

    private readonly CrabDeskRuntime _runtime;
    private readonly MonitorLayout _monitor;
    private readonly double _scale;
    private readonly Dictionary<IconBitmapKey, Bitmap?> _iconCache = [];
    private readonly HashSet<IconBitmapKey> _pendingIconLoads = [];
    private readonly Dictionary<IconBitmapKey, IconLoadRetry> _iconLoadRetries = [];
    private readonly CancellationTokenSource _iconLoadCancellation = new();
    private readonly SemaphoreSlim _iconLoadGate = new(4, 4);
    private bool _boxIconPreloadPending;
    private readonly HashSet<IconBitmapKey> _boxIconPreloadKeys = [];
    private DateTimeOffset? _boxIconPreloadStartedAt;
    private readonly HashSet<string> _selection = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Guid> _hoverExpandedBoxes = [];
    private readonly HoverExpansionController _hoverExpansion = new(
        TimeSpan.FromMilliseconds(HoverExpansionDelayMilliseconds),
        TimeSpan.FromMilliseconds(HoverCollapseDelayMilliseconds));
    private readonly HashSet<string> _selectionBase = new(StringComparer.OrdinalIgnoreCase);
    // Where a Shift+click range starts, together with the box that owns it: a
    // range never spans two boxes or two tabs. Shift itself leaves the anchor
    // untouched so repeated Shift+clicks re-extend from the same item.
    private string? _selectionAnchorKey;
    private Guid? _selectionAnchorBoxId;
    private readonly Dictionary<ItemViewKey, double> _scrollOffsets = [];
    private readonly Dictionary<Guid, MappedFolderItemCategory> _activeMappedFolderCategories = [];
    private readonly Dictionary<Guid, Guid?> _activeManualTabIds = [];
    private readonly Dictionary<Guid, IReadOnlyList<DesktopItemRef>> _boxItems = [];
    private readonly Dictionary<Guid, BoxHeightAnimation> _heightAnimations = [];
    // Refilled on every animation frame rather than reallocated. This runs ~64
    // times a second for as long as a box is moving, and a gen0 collection that
    // lands inside a 220 ms animation is a dropped frame the user can see.
    private readonly List<Guid> _animationFrameBoxIds = [];
    private readonly List<Guid> _animationFrameCompletedBoxIds = [];
    private readonly Dictionary<Guid, BoxHeightVisualCache> _heightAnimationVisualCaches = [];
    private readonly HashSet<Guid> _heightAnimationCacheRequestBoxIds = [];
    private RectangleF? _pendingHeightAnimationFrameDirtyBounds;
    private Guid? _pendingHeightAnimationCachePrewarmBoxId;
    private Guid? _prewarmedHeightAnimationCacheBoxId;
    private readonly List<BoxGeometry> _boxes = [];
    private readonly List<ItemGeometry> _items = [];
    private readonly List<ItemGeometry> _marqueeSelectionItems = [];
    private readonly HashSet<string> _marqueeSelectionKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(Guid BoxId, string ItemKey), RectangleF> _expandedItemHitBounds = [];
    private bool _geometryDirty = true;
    private IReadOnlyList<LayoutRect> _lastWindowRegionRectangles = [];
    private readonly DesktopAnimationFrameClock _animationFrameClock;
    private readonly Forms.Timer _hoverTimer;
    private readonly Forms.Timer _dragRenderTimer;
    private readonly Forms.Timer _scrollHoverResumeTimer;
    private ItemViewKey? _scrollAnimationKey;
    private double _scrollAnimationFrom;
    private double _scrollAnimationTo;
    private long _scrollAnimationStartedTimestamp;
    private readonly DesktopHoverOverlay _itemHoverOverlay;
    private readonly DesktopHoverOverlay _headerActionOverlay;
    private DesktopRenameEditor? _renameEditor;
    private Guid? _renamingBoxId;
    private string? _renamingItemKey;
    private DateTime _lastDragRenderUtc = DateTime.MinValue;
    private bool _dragRenderPending;
    private bool _hoverReconcilePending;
    private bool _confirmationInProgress;
    private bool _staticRenderDiagnosticWritten;
    private Bitmap? _hitMaskBitmap;
    private bool _hitMaskPresented;
    private Bitmap? _movingBoxVisualCache;
    private Guid? _movingBoxVisualCacheBoxId;
    private RectangleF _movingBoxVisualCacheBounds;
    private PointF _movingBoxVisualCacheAnchor;
    private readonly Forms.ToolTip _headerToolTip;
    private readonly Forms.Form _titleEditorWindow;
    private readonly FormsIntegration.ElementHost _titleEditorHost;
    private readonly WpfControls.TextBox _titleEditor;
    private ShellContextMenuSession? _shellContextMenu;
    private DesktopBox? _editingBox;
    private Font? _titleEditorFont;
    private DesktopBox? _movingBox;
    private DesktopBox? _resizingBox;
    private ResizeEdges _resizeEdges;
    private DesktopItemRef? _pressedItem;
    private string? _lastRenameClickKey;
    private DateTime _lastRenameClickUtc = DateTime.MinValue;
    private DesktopItemRef? _pendingRenameItem;
    private Guid? _pendingRenameBoxId;
    private DateTime _pendingRenamePressUtc;
    private DesktopBox? _selectionBox;
    private BoxGeometry? _selectionGeometry;
    private Guid? _pressedBoxId;
    private LayoutRect _startBounds;
    private PointF _pressPoint;
    private bool _monitorTransferSeen;
    private string? _monitorTransferPreviousMonitorId;
    // The render request consumes _monitorTransferPreviousMonitorId so the
    // source icon surface can be refreshed immediately. Keep the latest
    // source monitor until transform completion for the targeted model commit.
    private string? _monitorTransferLastPreviousMonitorId;
    private PointF _selectionStart;
    private RectangleF _selectionRectangle;
    private bool _dragStarted;
    private bool _dragDropCommitted;
    private bool _dragCancelled;
    private bool _showVirtualDesktopDropCursor;
    private DropPreviewState? _dropPreview;
    private string? _hoveredItemKey;
    private RectangleF? _lastItemHoverOverlayBounds;
    private bool _itemHoverOverlayUnavailable;
    private bool _headerActionOverlayUnavailable;
    private Guid? _focusedBoxId;
    private Guid? _hoveredBoxId;
    private Guid? _hoveredSearchBoxId;
    private Guid? _hoveredAutoExpandBoxId;
    private Guid? _hoveredMenuBoxId;
    private Guid? _openBoxMenuBoxId;
    private LayoutRect? _transformDirtyBounds;
    private string? _lastRegionDiagnostic;
    private bool _lastPresentSucceeded;
    private bool _desktopAttached;
    private string _lastPresentDiagnostic = string.Empty;
    private string _lastLoggedPresentDiagnostic = string.Empty;
    private bool _presentingLayer;
    private bool _isCompositedByIconSurface;
    private Action? _iconLayerRenderRequest;
    private Action<RectangleF>? _iconLayerPartialRenderRequest;
    private Action<PointF, IReadOnlyList<string>?, IReadOnlyList<string>?, PointF?>? _iconDragStateForward;
    private PointF _dragIconGrabOffset;
    private int _iconCacheVersion;
    private int _dynamicVisualVersion;
    private int _paintCount;
    private bool _resourcesDisposed;
    private bool _regionFailureHandled;

    internal DesktopBoxForm(
        CrabDeskRuntime runtime,
        MonitorLayout monitor)
    {
        _runtime = runtime;
        _monitor = monitor;
        _scale = monitor.DpiScale;
        Text = "CrabDesk Desktop Boxes";
        FormBorderStyle = Forms.FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = Forms.FormStartPosition.Manual;
        AutoScaleMode = Forms.AutoScaleMode.None;
        BackColor = Color.FromArgb(31, 34, 39);
        ClientSize = new Size((int)monitor.PixelBounds.Width, (int)monitor.PixelBounds.Height);
        DoubleBuffered = true;
        AllowDrop = true;
        SetStyle(
            Forms.ControlStyles.AllPaintingInWmPaint |
            Forms.ControlStyles.UserPaint |
            Forms.ControlStyles.OptimizedDoubleBuffer,
            true);

        _animationFrameClock = new DesktopAnimationFrameClock();
        _animationFrameClock.Frame += OnAnimationFrame;
        _hoverTimer = new Forms.Timer { Interval = 1 };
        _hoverTimer.Tick += OnHoverTimer;
        _scrollHoverResumeTimer = new Forms.Timer { Interval = ScrollHoverResumeDelayMilliseconds };
        _scrollHoverResumeTimer.Tick += OnScrollHoverResumeTimerTick;
        _dragRenderTimer = new Forms.Timer { Interval = DragRenderCoalesceMilliseconds };
        _dragRenderTimer.Tick += OnDragRenderTimerTick;
        _itemHoverOverlay = new DesktopHoverOverlay();
        Controls.Add(_itemHoverOverlay);
        _headerActionOverlay = new DesktopHoverOverlay();
        Controls.Add(_headerActionOverlay);
        _headerToolTip = new Forms.ToolTip
        {
            InitialDelay = 450,
            ReshowDelay = 100,
            AutoPopDelay = 4000,
            ShowAlways = true
        };
        _titleEditor = new WpfControls.TextBox
        {
            BorderThickness = new Wpf.Thickness(0),
            Padding = new Wpf.Thickness(0),
            VerticalContentAlignment = Wpf.VerticalAlignment.Center,
            AcceptsReturn = false,
            TextWrapping = Wpf.TextWrapping.NoWrap
        };
        WpfMedia.TextOptions.SetTextFormattingMode(_titleEditor, WpfMedia.TextFormattingMode.Display);
        WpfMedia.TextOptions.SetTextRenderingMode(_titleEditor, WpfMedia.TextRenderingMode.Grayscale);
        _titleEditorWindow = new Forms.Form
        {
            FormBorderStyle = Forms.FormBorderStyle.None,
            ShowInTaskbar = false,
            StartPosition = Forms.FormStartPosition.Manual,
            AutoScaleMode = Forms.AutoScaleMode.None,
            Padding = Forms.Padding.Empty,
            Margin = Forms.Padding.Empty
        };
        _titleEditorHost = new FormsIntegration.ElementHost
        {
            Dock = Forms.DockStyle.Fill,
            Margin = Forms.Padding.Empty,
            Child = _titleEditor
        };
        _titleEditor.KeyDown += OnTitleEditorKeyDown;
        _titleEditor.TextChanged += OnTitleEditorTextChanged;
        _titleEditorWindow.Deactivate += OnTitleEditorWindowDeactivate;
        _titleEditorWindow.Controls.Add(_titleEditorHost);
        InitializeBoxSearch();

        MouseDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeave += OnMouseLeave;
        MouseUp += OnMouseUp;
        MouseDoubleClick += OnMouseDoubleClick;
        MouseWheel += OnMouseWheel;
        DragEnter += OnDragOver;
        DragOver += OnDragOver;
        DragLeave += OnDragLeave;
        DragDrop += OnDragDrop;
        QueryContinueDrag += OnQueryContinueDrag;
        GiveFeedback += OnGiveFeedback;
    }

    protected override bool ShowWithoutActivation => true;

    protected override Forms.CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.Style &= ~WsClipSiblings;
            // The box child itself is a near-transparent input layer. The
            // visual boxes are composited above desktop icons by their shared
            // icon layer, avoiding sibling-composition differences in Explorer.
            // WS_EX_NOPARENTNOTIFY keeps every button press out of Explorer's
            // desktop window chain, which answers WM_PARENTNOTIFY synchronously.
            parameters.ExStyle |= WsExLayered | WsExNoParentNotify;
            return parameters;
        }
    }

    private bool UsesLayeredPresentation =>
        IsHandleCreated &&
        (DesktopWindowTools.GetSurfaceExtendedStyle(Handle) & WsExLayered) != 0;

    protected override void WndProc(ref Forms.Message message)
    {
        var diagnosticMessage = message.Msg;
        var diagnosticStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        using var watchdogScope = UiThreadWatchdog.EnterWindowMessage("box window", diagnosticMessage);
        try
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
            // DefWindowProc forwards this child-window message to SHELLDLL_DefView.
            // CrabDesk menus are opened explicitly from OnMouseDown instead.
            message.Result = IntPtr.Zero;
            return;
        }
        if (message.Msg == WmEraseBkgnd)
        {
            if (UsesLayeredPresentation)
            {
                // Pixel ownership belongs to UpdateLayeredWindow for the
                // layered path. Letting WinForms erase it would expose a
                // temporary rectangular backing surface during drags.
                message.Result = new IntPtr(1);
                return;
            }
            // A regular child must erase its old region when a box moves;
            // otherwise every drag frame remains in the GDI backing surface.
        }
        if (message.Msg == WmNcHitTest)
        {
            var packed = message.LParam.ToInt64();
            var screenPoint = new Point(
                unchecked((short)(packed & 0xffff)),
                unchecked((short)((packed >> 16) & 0xffff)));
            var clientPoint = PointToClient(screenPoint);
            if (clientPoint.X < 0 || clientPoint.Y < 0 ||
                clientPoint.X >= ClientSize.Width || clientPoint.Y >= ClientSize.Height ||
                !IsInteractivePointSafe(ToDip(clientPoint)))
            {
                message.Result = HtTransparent;
                return;
            }
        }
        if (message.Msg == WmMouseWheel &&
            (Forms.Control.ModifierKeys & Forms.Keys.Control) != 0)
        {
            // The low-level hook already converts Ctrl+wheel over a box into
            // a box-icon zoom. Swallowing the native message here stops
            // WinForms from bubbling it to the Explorer list view, which
            // would zoom the desktop icons as well.
            // The zoom resizes every item under the pointer; drop the stale
            // highlight the same way scrolling does.
            ClearItemHover();
            message.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref message);
        }
        finally
        {
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(diagnosticStarted);
            if (elapsed.TotalMilliseconds >= 100)
            {
                DiagnosticLog.Info(
                    $"Slow box window message monitor={_monitor.Id} " +
                    $"msg=0x{diagnosticMessage:X4} elapsedMs={elapsed.TotalMilliseconds:0}");
            }
        }
    }

    private IReadOnlyList<DesktopBox> DesktopBoxes =>
        BoxStacking.OrderBackToFront(_runtime.State.Boxes, _monitor.Id, _focusedBoxId);

    private void RebuildBoxItemCache()
    {
        _boxItems.Clear();
        foreach (var box in DesktopBoxes)
        {
            _boxItems[box.Id] = _runtime.GetItemsForBox(box.Id);
        }
    }

    private IReadOnlyList<DesktopItemRef> GetCachedItemsForBox(Guid boxId) =>
        _boxItems.GetValueOrDefault(boxId) ?? [];

    internal bool RefreshWorkspace()
    {
        HideItemHoverOverlay();
        HideHeaderActionOverlay();
        ClearHeightAnimationVisualCaches();
        RebuildBoxItemCache();
        _geometryDirty = true;
        if (!DesktopBoxes.Any(box => box.ExpandOnHover))
        {
            ClearHoverState();
        }
        else
        {
            var activeBoxIds = DesktopBoxes.Select(box => box.Id).ToHashSet();
            _hoverExpandedBoxes.RemoveWhere(id => !activeBoxIds.Contains(id));
        }
        var visibleKeys = _boxItems.Values
            .SelectMany(items => items)
            .Select(item => item.Key.ToString())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _selection.RemoveWhere(key => !visibleKeys.Contains(key));
        PruneIconCache();
        if (!UpdateWindowRegion())
        {
            return false;
        }
        var presented = PresentLayer();
        if (presented)
        {
            // Refresh intentionally hides transient child layers while box
            // geometry is rebuilt. Reconcile the physical cursor afterwards
            // even when the logical hovered box id did not change.
            QueueHoverReconcile();
            QueueBoxIconPreload();
        }
        return presented;
    }

    internal bool RefreshBoxItems(Guid boxId)
    {
        var diagnosticStarted = System.Diagnostics.Stopwatch.StartNew();
        var box = _runtime.State.Boxes.FirstOrDefault(candidate =>
            candidate.Id == boxId &&
            string.Equals(candidate.MonitorId, _monitor.Id, StringComparison.OrdinalIgnoreCase));
        if (box is null)
        {
            return false;
        }

        EnsureGeometry();
        var dirtyBounds = _boxes.FirstOrDefault(candidate => candidate.Box.Id == boxId)?.Bounds ??
            new RectangleF(
                (float)box.Bounds.X,
                (float)box.Bounds.Y,
                (float)box.Bounds.Width,
                (float)(IsEffectivelyCollapsed(box)
                    ? box.Appearance.TitleBarHeight
                    : box.Bounds.Height));

        HideItemHoverOverlay();
        _boxItems[boxId] = _runtime.GetItemsForBox(boxId);
        _geometryDirty = true;
        EnsureGeometry();
        var currentBounds = _boxes.FirstOrDefault(candidate => candidate.Box.Id == boxId)?.Bounds;
        if (currentBounds is { } updatedBounds)
        {
            dirtyBounds = RectangleF.Union(dirtyBounds, updatedBounds);
        }
        var visibleKeys = _boxItems.Values
            .SelectMany(items => items)
            .Select(item => item.Key.ToString())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _selection.RemoveWhere(key => !visibleKeys.Contains(key));
        PruneIconCache();

        dirtyBounds.Inflate(2, 2);
        if (_isCompositedByIconSurface && _iconLayerPartialRenderRequest is not null)
        {
            _iconLayerPartialRenderRequest(dirtyBounds);
        }
        else
        {
            PresentLayer();
        }
        DiagnosticLog.Info(
            $"Box item refresh timing monitor={_monitor.Id} box={boxId} " +
            $"elapsedMs={diagnosticStarted.ElapsedMilliseconds}");
        return true;
    }

    internal bool UpdateInteractionRegion()
    {
        if (!UpdateWindowRegion())
        {
            return false;
        }

        // A composited box already has its transparent hit-mask window in
        // place. Re-presenting it after a geometry-only update asks the icon
        // surface for a full frame, which made every desktop icon visibly
        // refresh at the end of a box drag. The transform trail has already
        // queued the precise visual dirty region for this case.
        if (!ShouldPresentAfterRegionUpdate(
                _isCompositedByIconSurface,
                _hitMaskPresented))
        {
            return _lastPresentSucceeded;
        }

        return PresentLayer();
    }

    internal bool ValidateWindowRegion() =>
        IsHandleCreated && DesktopWindowTools.VerifyRoundedRegion(
            Handle,
            _lastWindowRegionRectangles,
            _scale,
            out _);

    protected override void OnPaintBackground(Forms.PaintEventArgs eventArgs)
    {
        if (!UsesLayeredPresentation)
        {
            base.OnPaintBackground(eventArgs);
        }
    }

    protected override void OnPaint(Forms.PaintEventArgs eventArgs)
    {
        if (_isCompositedByIconSurface)
        {
            return;
        }
        if (!UsesLayeredPresentation)
        {
            PaintRegularSurface(eventArgs.Graphics);
            return;
        }

        // Layered windows do not use the WinForms paint buffer. A paint
        // message can still be generated while Windows changes the region;
        // retry only when the last explicit presentation failed so it cannot
        // overwrite a fresh drag frame with an intermediate backing paint.
        if (!_lastPresentSucceeded)
        {
            PresentLayer();
        }
    }

    protected override void OnInvalidated(Forms.InvalidateEventArgs eventArgs)
    {
        base.OnInvalidated(eventArgs);
        // Existing interaction code intentionally uses Invalidate() in many
        // paths. Make those requests synchronous for the layered surface;
        // otherwise a SetWindowRgn call during a box drag exposes the icon
        // layer until the asynchronous WM_PAINT is serviced.
        if (!_resourcesDisposed && IsHandleCreated &&
            (UsesLayeredPresentation || _isCompositedByIconSurface))
        {
            PresentLayer();
        }
    }

    internal string MonitorId => _monitor.Id;

    internal void AttachToDesktop(IntPtr parentHandle)
    {
        // Handle creation can invalidate the form. Keep its first layered
        // presentation in the final parent's composition tree: presenting as
        // a top-level window before SetParent leaves an invisible child layer.
        _desktopAttached = false;
        DesktopWindowTools.AttachAsDesktopChild(Handle, parentHandle);
        _desktopAttached = true;
    }

    internal void PrepareIconLayerComposition() =>
        _isCompositedByIconSurface = true;

    internal bool IsTransformActive => _movingBox is not null || _resizingBox is not null;

    internal string? TransformMonitorId => (_movingBox ?? _resizingBox)?.MonitorId;

    internal void MarkMonitorTransferRefreshPending(string previousMonitorId)
    {
        _monitorTransferPreviousMonitorId = previousMonitorId;
        _monitorTransferLastPreviousMonitorId = previousMonitorId;
    }

    internal string? ConsumeMonitorTransferPreviousMonitorId()
    {
        var previousMonitorId = _monitorTransferPreviousMonitorId;
        _monitorTransferPreviousMonitorId = null;
        return previousMonitorId;
    }

    internal bool IsItemDragActive => _dragStarted;

    private bool IsScrollAnimationActive =>
        _scrollAnimationKey is not null && _animationFrameClock.Enabled;

    internal bool HasDynamicAnimation =>
        _heightAnimations.Count > 0 || IsScrollAnimationActive;

    internal bool HasDynamicVisual =>
        IsTransformActive || _dragStarted || _dropPreview is not null || _selectionBox is not null ||
        _heightAnimations.Count > 0 || IsScrollAnimationActive;

    internal bool HasDynamicVisualForMonitor(string monitorId) =>
        HasDynamicVisualForMonitor(
            (_movingBox ?? _resizingBox)?.MonitorId,
            _monitor.Id,
            monitorId,
            HasDynamicVisual);

    /// <summary>
    /// A box being moved or resized keeps belonging to the form of the monitor
    /// where the gesture started, so ownership of its dynamic frame follows the
    /// live box instead of the form. Every other dynamic visual stays on the
    /// monitor the form itself covers.
    /// </summary>
    internal static bool HasDynamicVisualForMonitor(
        string? transformBoxMonitorId,
        string surfaceMonitorId,
        string monitorId,
        bool hasDynamicVisual) =>
        transformBoxMonitorId is not null
            ? string.Equals(transformBoxMonitorId, monitorId, StringComparison.OrdinalIgnoreCase)
            : string.Equals(surfaceMonitorId, monitorId, StringComparison.OrdinalIgnoreCase) &&
                hasDynamicVisual;

    /// <summary>
    /// True when the only dynamic visual on this surface is the hover
    /// expand/collapse height animation. Such frames are rendered through the
    /// single-window icon-layer channel so the box never has to be handed
    /// between the settled layer and the drag overlay at the animation
    /// boundaries (which flashes for one compositor frame).
    /// </summary>
    internal bool IsPartialAnimationOnly => IsPartialBoxAnimationOnly(
        _heightAnimations.Count > 0,
        IsScrollAnimationActive,
        IsTransformActive || _dragStarted || _dropPreview is not null || _selectionBox is not null);

    internal bool UsesPartialHeightAnimationComposition =>
        CanUsePartialHeightAnimationComposition(
            _heightAnimations.Count > 0,
            IsTransformActive,
            _selectionBox is not null,
            IsScrollAnimationActive);

    internal bool UsesPartialBoxAnimationComposition =>
        CanUsePartialBoxAnimationComposition(
            _heightAnimations.Count > 0,
            IsTransformActive,
            _selectionBox is not null,
            IsScrollAnimationActive,
            IsTransformActive || _dragStarted || _dropPreview is not null || _selectionBox is not null);

    internal static bool IsPartialBoxAnimationOnly(
        bool heightAnimationActive,
        bool scrollAnimationActive,
        bool otherDynamicVisualActive) =>
        (heightAnimationActive || scrollAnimationActive) && !otherDynamicVisualActive;

    internal static bool CanUsePartialHeightAnimationComposition(
        bool heightAnimationActive,
        bool transformActive,
        bool selectionActive,
        bool scrollAnimationActive) =>
        heightAnimationActive &&
        !transformActive &&
        !selectionActive &&
        !scrollAnimationActive;

    internal static bool CanUsePartialBoxAnimationComposition(
        bool heightAnimationActive,
        bool transformActive,
        bool selectionActive,
        bool scrollAnimationActive,
        bool otherDynamicVisualActive) =>
        CanUsePartialHeightAnimationComposition(
            heightAnimationActive,
            transformActive,
            selectionActive,
            scrollAnimationActive) ||
        (!heightAnimationActive &&
         scrollAnimationActive &&
         !transformActive &&
         !selectionActive &&
         !otherDynamicVisualActive);

    internal static bool ShouldPresentHitMask(bool bitmapPresented, bool presenterLost) =>
        !bitmapPresented || presenterLost;

    internal static int CalculateAnimationPresentCount(bool heightAnimationActive, bool scrollAnimationActive) =>
        heightAnimationActive || scrollAnimationActive ? 1 : 0;

    internal static bool ShouldCompositeBoxVisualsInParent(
        bool hasDynamicVisual,
        bool heightAnimationOnly) =>
        hasDynamicVisual && !heightAnimationOnly;

    internal static bool ShouldUsePartialTransformCommit(
        bool isCompositedByIconSurface,
        bool hasPartialRenderer) =>
        isCompositedByIconSurface && hasPartialRenderer;

    internal static bool ShouldRebuildWorkspaceAfterBoxTransform(bool monitorChanged) =>
        monitorChanged;

    internal static bool ShouldPresentAfterRegionUpdate(
        bool isCompositedByIconSurface,
        bool hitMaskPresented) =>
        !isCompositedByIconSurface || !hitMaskPresented;

    internal static bool ShouldRebuildHeightAnimationGeometry(
        bool compositedByIconSurface,
        bool animationCompleted) =>
        !compositedByIconSurface || animationCompleted;

    internal static RectangleF CalculateHeightAnimationFrameDirtyBounds(
        RectangleF boxBounds,
        float previousHeight,
        float currentHeight,
        float cornerRadius)
    {
        if (boxBounds.Width <= 0 || boxBounds.Height <= 0)
        {
            return RectangleF.Empty;
        }

        previousHeight = Math.Clamp(previousHeight, 0, boxBounds.Height);
        currentHeight = Math.Clamp(currentHeight, 0, boxBounds.Height);
        var smallerHeight = Math.Min(previousHeight, currentHeight);
        var largerHeight = Math.Max(previousHeight, currentHeight);
        var roundedEdgeAllowance = Math.Clamp(cornerRadius, 0, smallerHeight / 2);
        return RectangleF.FromLTRB(
            boxBounds.Left,
            Math.Max(boxBounds.Top, boxBounds.Top + smallerHeight - roundedEdgeAllowance),
            boxBounds.Right,
            Math.Min(boxBounds.Bottom, boxBounds.Top + largerHeight));
    }

    internal static bool ShouldCommitCompletedHeightAnimationPartially(
        bool compositedByIconSurface,
        bool hasCompletedAnimation,
        bool animationStillActive,
        bool otherDynamicVisualActive,
        bool hasPartialRenderer) =>
        compositedByIconSurface &&
        hasCompletedAnimation &&
        !animationStillActive &&
        !otherDynamicVisualActive &&
        hasPartialRenderer;

    internal static bool ShouldCreateHeightAnimationVisualCache(
        int requiredIconCount,
        int loadedIconCount) =>
        loadedIconCount >= requiredIconCount;

    internal static bool ShouldPresentLoadedBoxIcon(
        bool effectivelyCollapsed,
        bool heightAnimationActive) =>
        !effectivelyCollapsed && !heightAnimationActive;

    internal static bool ShouldRetainHeightAnimationVisualCache(bool effectivelyCollapsed) =>
        !effectivelyCollapsed;

    internal static TimeSpan CalculateBoxHeightAnimationDuration(
        double remainingDistance,
        double fullDistance) =>
        AnimationMath.ScaleDurationByDistance(
            remainingDistance,
            fullDistance,
            TimeSpan.FromMilliseconds(BoxHeightAnimationMilliseconds),
            TimeSpan.FromMilliseconds(MinimumBoxHeightAnimationMilliseconds));

    internal bool IsMarqueeSelectionActive => _selectionBox is not null;

    internal int DynamicVisualVersion => _dynamicVisualVersion;

    internal static bool ShouldPollHoverDuringDesktopInteraction(
        bool desktopPointerInteractionActive,
        bool desktopItemDragActive) =>
        !desktopPointerInteractionActive ||
        desktopItemDragActive;

    internal static bool ShouldTrackItemHoverDuringScroll(
        bool scrollAnimationActive,
        bool hoverResumePending) =>
        !scrollAnimationActive && !hoverResumePending;

    internal static bool ShouldShowHeaderActions(
        Guid boxId,
        Guid? hoveredBoxId,
        Guid? searchingBoxId) =>
        hoveredBoxId == boxId || searchingBoxId == boxId;

    internal static bool ShouldRestoreHeaderActionOverlay(
        bool hoverTargetUnchanged,
        bool hasActiveHeaderActions,
        bool overlayVisible) =>
        hoverTargetUnchanged && hasActiveHeaderActions && !overlayVisible;

    internal static bool ShouldSuspendHoverState(
        Guid? openBoxMenuBoxId,
        bool inlineRenameActive) =>
        openBoxMenuBoxId is not null || inlineRenameActive;

    internal static bool IsPointerInsideVisualBox(
        RectangleF visualBounds,
        PointF pointer) =>
        visualBounds.Contains(pointer);

    internal static bool IsPointerInsideExpandedBox(
        LayoutRect bounds,
        double interactionHeight,
        PointF pointer) =>
        pointer.X >= bounds.X &&
        pointer.X <= bounds.X + bounds.Width &&
        pointer.Y >= bounds.Y &&
        pointer.Y <= bounds.Y + Math.Max(bounds.Height, interactionHeight);

    internal static bool ShouldCloseBoxSearchForPointer(
        bool searchVisible,
        bool pointerInsideActiveBox) =>
        searchVisible && !pointerInsideActiveBox;

    internal static bool IsCollapsedHeaderHoverTarget(
        Guid candidateBoxId,
        Guid? topmostVisualBoxId,
        bool expandsOnHover,
        bool isCollapsed,
        bool pointerInHeader,
        bool pointerOverHeaderAction) =>
        candidateBoxId == topmostVisualBoxId &&
        expandsOnHover &&
        isCollapsed &&
        pointerInHeader &&
        !pointerOverHeaderAction;

    internal static bool ShouldDrawHeaderActionsInBaseLayer(
        bool compositedByIconSurface,
        bool overlayUnavailable) =>
        ShouldDrawHeaderActionsOnCurrentLayer(
            compositedByIconSurface,
            overlayUnavailable,
            dynamicTransform: false);

    internal static bool ShouldDrawHeaderActionsOnCurrentLayer(
        bool compositedByIconSurface,
        bool overlayUnavailable,
        bool dynamicTransform) =>
        !compositedByIconSurface || overlayUnavailable || dynamicTransform;

    internal static bool ShouldPresentHeaderActionOverlay(
        bool hasDynamicVisual,
        bool partialAnimationOnly) =>
        !hasDynamicVisual || partialAnimationOnly;

    internal static bool ShouldPrewarmHeightAnimationCache(
        bool compositedByIconSurface,
        bool animationEnabled,
        bool expandOnHover,
        bool effectivelyCollapsed,
        bool cacheExists) =>
        compositedByIconSurface &&
        animationEnabled &&
        expandOnHover &&
        effectivelyCollapsed &&
        !cacheExists;

    internal static (
        RectangleF Search,
        RectangleF AutoExpand,
        RectangleF Menu) CalculateHeaderActionBounds(RectangleF header)
    {
        const float buttonWidth = 26;
        const float buttonHeight = 28;
        var top = header.Y + (header.Height - buttonHeight) / 2;
        return (
            new RectangleF(header.X + 18, top, buttonWidth, buttonHeight),
            new RectangleF(header.Right - 74, top, buttonWidth, buttonHeight),
            new RectangleF(header.Right - 44, top, buttonWidth, buttonHeight));
    }

    internal static RectangleF CalculateTitleTextBounds(RectangleF header, bool centered)
    {
        // Calculate the physical forbidden zones defined by header buttons.
        // Left zone: Search button occupies [header.X + 18, header.X + 44].
        // Right zone: AutoExpand + Menu occupy [header.Right - 74, header.Right - 18].
        const float buttonMargin = 6f;
        var minLeft = header.X + 44f + buttonMargin;
        var maxRight = header.Right - 74f - buttonMargin;

        if (maxRight <= minLeft)
        {
            var fallbackCenter = (minLeft + maxRight) / 2;
            return new RectangleF(fallbackCenter, header.Y, 0, header.Height);
        }

        var availableBetweenButtons = maxRight - minLeft;

        if (!centered)
        {
            var leftAligned = Math.Max(minLeft, header.X + 52f);
            return new RectangleF(
                leftAligned,
                header.Y,
                Math.Max(0, maxRight - leftAligned),
                header.Height);
        }

        // When centered:
        // Try box-geometric center first (requires symmetric insets from box edges).
        var symmetricSideInset = Math.Max(header.Right - maxRight, minLeft - header.X);
        var symmetricWidth = header.Width - symmetricSideInset * 2;

        // If the box is wide enough to afford symmetric clearance (at least 60px available),
        // use box-geometric center so it looks perfectly balanced relative to the entire box.
        if (symmetricWidth >= 60f)
        {
            return new RectangleF(
                header.X + symmetricSideInset,
                header.Y,
                symmetricWidth,
                header.Height);
        }

        // Otherwise (narrow box, e.g. 2 columns or tight resizing), adapt gracefully:
        // center the title within the available space between the search button and the right buttons.
        return new RectangleF(
            minLeft,
            header.Y,
            availableBetweenButtons,
            header.Height);
    }

    internal static RectangleF CalculateFocusDirtyBounds(
        RectangleF? previousBounds,
        RectangleF currentBounds) =>
        previousBounds is { } previous
            ? RectangleF.Union(previous, currentBounds)
            : currentBounds;

    internal static RectangleF CalculateMovingBoxVisualCacheBounds(
        RectangleF boxBounds,
        double scale)
    {
        const float padding = 2;
        var effectiveScale = Math.Max(scale, 0.01d);
        var left = Math.Floor((boxBounds.Left - padding) * effectiveScale) / effectiveScale;
        var top = Math.Floor((boxBounds.Top - padding) * effectiveScale) / effectiveScale;
        var right = Math.Ceiling((boxBounds.Right + padding) * effectiveScale) / effectiveScale;
        var bottom = Math.Ceiling((boxBounds.Bottom + padding) * effectiveScale) / effectiveScale;
        return RectangleF.FromLTRB((float)left, (float)top, (float)right, (float)bottom);
    }

    internal static bool ShouldRenderDropPreviewSeparately(
        Guid previewBoxId,
        Guid? transformBoxId,
        IReadOnlySet<Guid> animatedBoxIds) =>
        previewBoxId != transformBoxId &&
        !animatedBoxIds.Contains(previewBoxId);

    internal static bool HasDesktopDropTargetVisualChanged(
        Guid? previousBoxId,
        bool? previousAcceptsDrop,
        int? previousManualTabIndex,
        Guid currentBoxId,
        bool currentAcceptsDrop,
        int? currentManualTabIndex) =>
        previousBoxId != currentBoxId ||
        previousAcceptsDrop != currentAcceptsDrop ||
        previousManualTabIndex != currentManualTabIndex;

    internal static bool ShouldRenderOleDropPreview(
        bool floatingCard,
        bool targetVisualChanged,
        bool folderTargetChanged,
        bool pointerChanged) =>
        targetVisualChanged ||
        folderTargetChanged ||
        (floatingCard && pointerChanged);

    internal RectangleF? GetDynamicVisualBounds() =>
        GetDynamicVisualBounds(useAnimationDirtyBounds: false);

    internal RectangleF? GetDynamicVisualBoundsForMonitor(string monitorId) =>
        GetDynamicVisualBounds(useAnimationDirtyBounds: false, monitorId);

    internal RectangleF? GetDynamicVisualDirtyBounds() =>
        GetDynamicVisualBounds(useAnimationDirtyBounds: true);

    internal RectangleF? GetDynamicVisualDirtyBoundsForMonitor(string monitorId) =>
        GetDynamicVisualBounds(useAnimationDirtyBounds: true, monitorId);

    private RectangleF? GetDynamicVisualBounds(
        bool useAnimationDirtyBounds,
        string? monitorId = null)
    {
        if (IsDisposed || _resourcesDisposed || !HasDynamicVisual)
        {
            return null;
        }

        if (monitorId is not null && !HasDynamicVisualForMonitor(monitorId))
        {
            return null;
        }

        EnsureGeometry();
        RectangleF? bounds = null;
        var heightAnimationFrameDirtyBounds = useAnimationDirtyBounds &&
            UsesPartialHeightAnimationComposition
            ? _pendingHeightAnimationFrameDirtyBounds
            : null;
        if (heightAnimationFrameDirtyBounds is not null)
        {
            // Animation requests are coalesced on the icon surface. The union
            // accumulated since the previous presentation is consumed here so
            // no skipped timer tick can leave stale pixels behind.
            _pendingHeightAnimationFrameDirtyBounds = null;
        }
        var transformBox = _movingBox ?? _resizingBox;
        if (transformBox is not null)
        {
            var geometry = _boxes.FirstOrDefault(box => box.Box.Id == transformBox.Id);
            if (!string.Equals(transformBox.MonitorId, _monitor.Id, StringComparison.OrdinalIgnoreCase) &&
                monitorId is not null &&
                string.Equals(transformBox.MonitorId, monitorId, StringComparison.OrdinalIgnoreCase))
            {
                geometry = CreateBoxGeometry(
                    transformBox,
                    (float)GetVisualBoxHeight(transformBox),
                    IsEffectivelyCollapsed(transformBox));
            }
            if (geometry is not null)
            {
                bounds = string.Equals(transformBox.MonitorId, _monitor.Id, StringComparison.OrdinalIgnoreCase)
                    ? GetTransformGeometry(geometry).Bounds
                    : geometry.Bounds;
            }
        }

        if (_dropPreview is { } preview &&
            ShouldRenderDropPreviewSeparately(
                preview.BoxId,
                transformBox?.Id,
                _heightAnimations.Keys.ToHashSet()))
        {
            var geometry = _boxes.FirstOrDefault(box => box.Box.Id == preview.BoxId);
            if (geometry is not null)
            {
                bounds = bounds is { } existing
                    ? RectangleF.Union(existing, geometry.Bounds)
                    : geometry.Bounds;
            }
        }

        if (_selectionBox is { } selectionBox)
        {
            var geometry = _boxes.FirstOrDefault(box => box.Box.Id == selectionBox.Id);
            if (geometry is not null && GetMarqueeSelectionOverlayBounds(geometry) is { } selectionBounds)
            {
                bounds = bounds is { } existing
                    ? RectangleF.Union(existing, selectionBounds)
                    : selectionBounds;
            }
        }

        if (IsScrollAnimationActive && _scrollAnimationKey is { } scrollKey)
        {
            var geometry = _boxes.FirstOrDefault(box => box.Box.Id == scrollKey.BoxId);
            if (geometry is not null)
            {
                bounds = bounds is { } existing
                    ? RectangleF.Union(existing, geometry.Bounds)
                    : geometry.Bounds;
            }
        }

        if (heightAnimationFrameDirtyBounds is { } frameDirtyBounds)
        {
            bounds = bounds is { } existing
                ? RectangleF.Union(existing, frameDirtyBounds)
                : frameDirtyBounds;
        }
        else
        {
            foreach (var animatedBoxId in _heightAnimations.Keys)
            {
                var box = DesktopBoxes.FirstOrDefault(candidate => candidate.Id == animatedBoxId);
                if (box is not null)
                {
                    var animationBounds = new RectangleF(
                        (float)box.Bounds.X,
                        (float)box.Bounds.Y,
                        (float)box.Bounds.Width,
                        (float)box.Bounds.Height);
                    bounds = bounds is { } existing
                        ? RectangleF.Union(existing, animationBounds)
                        : animationBounds;
                }
            }
        }

        return bounds;
    }

    internal void SetIconLayerRenderRequest(Action renderRequest) =>
        _iconLayerRenderRequest = renderRequest;

    internal void SetIconLayerPartialRenderRequest(Action<RectangleF> renderRequest) =>
        _iconLayerPartialRenderRequest = renderRequest;

    // The icon surface draws every drag ghost. While the OLE drag route is
    // owned by this (visually transparent) box window, forward the pointer
    // and payload so the ghost keeps following the mouse. Null payloads mean
    // the drag left this surface or ended.
    internal void SetIconDragStateForward(
        Action<PointF, IReadOnlyList<string>?, IReadOnlyList<string>?, PointF?> forward) =>
        _iconDragStateForward = forward;

    /// <summary>
    /// Draws this surface into the full-monitor desktop icon bitmap.  Desktop
    /// icons are already present in that bitmap, so drawing boxes afterwards
    /// lets their configured translucency reveal the icons underneath instead
    /// of hiding them with a native sibling window.
    /// </summary>
    internal void RenderOnIconLayer(Graphics graphics, RectangleF clipBounds)
    {
        if (IsDisposed || _resourcesDisposed)
        {
            return;
        }

        EnsureGeometry();
        foreach (var box in _boxes.Where(box => box.Bounds.IntersectsWith(clipBounds)))
        {
            DrawBox(graphics, box, clipBounds);
        }
    }

    /// <summary>
    /// Renders the settled portion of this monitor. During a transform the
    /// active box is supplied by the small dynamic pass instead of forcing the
    /// desktop-wide backing layer to redraw it every frame.
    /// </summary>
    internal void RenderStaticOnIconLayer(Graphics graphics, RectangleF clipBounds)
    {
        if (IsDisposed || _resourcesDisposed)
        {
            return;
        }

        EnsureGeometry();
        if (!_staticRenderDiagnosticWritten)
        {
            _staticRenderDiagnosticWritten = true;
            DiagnosticLog.Info(
                $"Box static icon-layer render monitor={_monitor.Id} boxes={_boxes.Count} " +
                $"clip={clipBounds} scale={_scale:0.###}");
        }
        var transformId = (_movingBox ?? _resizingBox)?.Id;
        var dragSourceId = _dragStarted ? _pressedBoxId : null;
        var previewId = _dropPreview?.BoxId;
        var selectionId = _selectionBox?.Id;
        var hasDynamicVisual = HasDynamicVisual;
        var animatedBoxIds = hasDynamicVisual
            ? _heightAnimations.Keys.ToHashSet()
            : new HashSet<Guid>();
        if (IsScrollAnimationActive && _scrollAnimationKey is { } scrollKey)
        {
            animatedBoxIds.Add(scrollKey.BoxId);
        }
        foreach (var box in _boxes.Where(box =>
                     box.Bounds.IntersectsWith(clipBounds) &&
                     (!hasDynamicVisual ||
                       (box.Box.Id != transformId &&
                        box.Box.Id != dragSourceId &&
                        box.Box.Id != previewId &&
                       !animatedBoxIds.Contains(box.Box.Id)))))
        {
            var isSelectionBox = box.Box.Id == selectionId;
            DrawBox(
                graphics,
                box,
                clipBounds,
                includeDropPreview: !hasDynamicVisual,
                selectedItemKeys: isSelectionBox ? _selectionBase : null,
                includeSelectionRectangle: !isSelectionBox,
                suppressedHoverItemKeys: isSelectionBox ? _marqueeSelectionKeys : null,
                includeItemHoverFeedback: _itemHoverOverlayUnavailable);
        }
    }

    /// <summary>
    /// Paints only the mutable part of an icon-layer drag frame: the box being
    /// moved/resized or the one currently receiving a desktop-item preview.
    /// </summary>
    internal void RenderDragOnIconLayer(Graphics graphics, RectangleF clipBounds)
        => RenderDragOnIconLayerForMonitor(graphics, clipBounds, _monitor.Id);

    internal void RenderDragOnIconLayerForMonitor(
        Graphics graphics,
        RectangleF clipBounds,
        string monitorId)
    {
        if (IsDisposed || _resourcesDisposed)
        {
            return;
        }

        EnsureGeometry();
        var transformBox = _movingBox ?? _resizingBox;
        var renderedDynamicBoxIds = new HashSet<Guid>();
        if (_selectionBox is { } selectionBox)
        {
            var geometry = _boxes.FirstOrDefault(box => box.Box.Id == selectionBox.Id);
            if (geometry is not null)
            {
                DrawMarqueeSelectionOverlay(graphics, geometry, clipBounds);
            }
        }

        if (transformBox is not null)
        {
            if (!string.Equals(transformBox.MonitorId, monitorId, StringComparison.OrdinalIgnoreCase))
            {
                transformBox = null;
            }
        }

        if (transformBox is not null)
        {
            // During a cross-monitor move the shared model changes
            // MonitorId before the source form is rebuilt. Its local geometry
            // is therefore temporarily absent; create the target-monitor
            // geometry directly from the live box so the target icon surface
            // can keep rendering the drag frame.
            var geometry = _boxes.FirstOrDefault(box => box.Box.Id == transformBox.Id);
            if (!string.Equals(transformBox.MonitorId, _monitor.Id, StringComparison.OrdinalIgnoreCase))
            {
                var height = (float)GetVisualBoxHeight(transformBox);
                geometry = CreateBoxGeometry(
                    transformBox,
                    height,
                    IsEffectivelyCollapsed(transformBox));
            }
            if (geometry is not null)
            {
                var transformGeometry = string.Equals(transformBox.MonitorId, _monitor.Id, StringComparison.OrdinalIgnoreCase)
                    ? GetTransformGeometry(geometry)
                    : geometry;
                if (_movingBox is null ||
                    !string.Equals(transformBox.MonitorId, _monitor.Id, StringComparison.OrdinalIgnoreCase) ||
                    !DrawMovingBoxVisualCache(graphics, transformGeometry, clipBounds))
                {
                    DrawBox(
                        graphics,
                        transformGeometry,
                        clipBounds,
                        includeItemHoverFeedback: false,
                        includeCompositedHeaderActions: true);
                }
                renderedDynamicBoxIds.Add(transformBox.Id);
            }
        }

        // An item drag originating in this box keeps the source box itself
        // parent-owned. Render it in this dynamic pass so the settled layer
        // does not also contribute the same translucent pixels.
        if (_dragStarted && _pressedBoxId is { } dragSourceId &&
            dragSourceId != transformBox?.Id)
        {
            var geometry = _boxes.FirstOrDefault(box => box.Box.Id == dragSourceId);
            if (geometry is not null)
            {
                DrawBox(
                    graphics,
                    geometry,
                    clipBounds,
                    includeItemHoverFeedback: false);
                renderedDynamicBoxIds.Add(dragSourceId);
            }
        }

        foreach (var animatedBoxId in _heightAnimations.Keys)
        {
            if (!renderedDynamicBoxIds.Add(animatedBoxId))
            {
                continue;
            }
            var geometry = _boxes.FirstOrDefault(box => box.Box.Id == animatedBoxId);
            if (geometry is not null)
            {
                if (!DrawHeightAnimationVisualCache(graphics, geometry, clipBounds))
                {
                    DrawBox(
                        graphics,
                        geometry,
                        clipBounds,
                        includeItemHoverFeedback: false);
                }
            }
        }

        if (IsScrollAnimationActive &&
            _scrollAnimationKey is { } scrollKey &&
            renderedDynamicBoxIds.Add(scrollKey.BoxId))
        {
            var geometry = _boxes.FirstOrDefault(box => box.Box.Id == scrollKey.BoxId);
            if (geometry is not null)
            {
                DrawBox(
                    graphics,
                    geometry,
                    clipBounds,
                    includeItemHoverFeedback: false);
            }
        }

        if (_dropPreview is { } preview &&
            ShouldRenderDropPreviewSeparately(
                preview.BoxId,
                transformBox?.Id,
                _heightAnimations.Keys.ToHashSet()) &&
            renderedDynamicBoxIds.Add(preview.BoxId))
        {
            var geometry = _boxes.FirstOrDefault(box => box.Box.Id == preview.BoxId);
            if (geometry is not null)
            {
                DrawBox(
                    graphics,
                    geometry,
                    clipBounds,
                    includeItemHoverFeedback: false);
            }
        }
    }

    /// <summary>
    /// The desktop icon replacement is a per-pixel-alpha layered child. Draw
    /// boxes through the same compositor path so normal WinForms painting
    /// cannot be visually bypassed by that sibling surface.
    /// </summary>
    private bool PresentLayer()
    {
        if (!_desktopAttached)
        {
            return false;
        }
        if (_presentingLayer)
        {
            return _lastPresentSucceeded;
        }
        if (IsDisposed || !IsHandleCreated || ClientSize.Width <= 0 || ClientSize.Height <= 0)
        {
            _lastPresentSucceeded = false;
            _lastPresentDiagnostic = "The desktop box surface has no valid handle or size.";
            return false;
        }

        _presentingLayer = true;
        try
        {
            _paintCount++;
            if (!_isCompositedByIconSurface)
            {
                RebuildGeometry();
            }
            if (_isCompositedByIconSurface)
            {
                EnsureHitMaskBitmap();
                if (ShouldPresentHitMask(_hitMaskPresented, !_lastPresentSucceeded))
                {
                    using (var graphics = Graphics.FromImage(_hitMaskBitmap!))
                    {
                        // The native rounded window region defines the precise
                        // interactive shape. A resident one-alpha surface keeps
                        // every future region click-capable without uploading a
                        // monitor-sized hit mask after each hover transition.
                        graphics.Clear(Color.FromArgb(1, Color.Black));
                    }

                    _lastPresentSucceeded = LayeredWindowPresenter.TryPresent(
                        Handle,
                        _hitMaskBitmap!,
                        PointToScreen(Point.Empty),
                        out _lastPresentDiagnostic);
                    _hitMaskPresented = _lastPresentSucceeded;
                }
                if (_lastPresentSucceeded)
                {
                    _iconLayerRenderRequest?.Invoke();
                }
                return _lastPresentSucceeded;
            }
            if (!UsesLayeredPresentation)
            {
                _lastPresentSucceeded = true;
                _lastPresentDiagnostic = "regular child presentation";
                if (IsHandleCreated && !IsDisposed)
                {
                    Invalidate();
                }
                return true;
            }
            using var bitmap = DesktopLayerBitmapFactory.Create(
                ClientSize.Width,
                ClientSize.Height);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Transparent);
                graphics.CompositingMode = CompositingMode.SourceOver;
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                // Box surfaces sit over wallpaper and can be rendered at fractional
                // monitor scales. Use the same grid-fitted text path as the desktop
                // icon surface so configured fonts remain crisp and consistent.
                graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                graphics.TextContrast = 4;
                graphics.ScaleTransform((float)_scale, (float)_scale);
                // UpdateLayeredWindow replaces the entire bitmap, so redraw every
                // visible box rather than just the invalidated paint rectangle.
                var clipBounds = new RectangleF(
                    0,
                    0,
                    (float)(ClientSize.Width / _scale),
                    (float)(ClientSize.Height / _scale));
                clipBounds.Inflate(8, 8);
                foreach (var box in _boxes.Where(box => box.Bounds.IntersectsWith(clipBounds)))
                {
                    DrawBox(graphics, box, clipBounds);
                }
                graphics.ResetTransform();
            }

            var previousDiagnostic = _lastPresentDiagnostic;
            if (_acrylicFramePresenter is not null)
            {
                // The native child supplies input only; foreground and blur are
                // committed together by DesktopAcrylicHost.
                EnsureHitMaskBitmap();
                if (!_hitMaskPresented)
                {
                    using (var maskGraphics = Graphics.FromImage(_hitMaskBitmap!))
                        maskGraphics.Clear(Color.FromArgb(1, Color.Black));
                    _hitMaskPresented = LayeredWindowPresenter.TryPresent(Handle, _hitMaskBitmap!,
                        PointToScreen(Point.Empty), out _lastPresentDiagnostic);
                    if (!_hitMaskPresented) return _lastPresentSucceeded = false;
                }
                _acrylicFramePresenter(bitmap, PointToScreen(Point.Empty), GetAcrylicRegions().ToArray());
                _lastPresentDiagnostic = "unified acrylic composition";
                return _lastPresentSucceeded = true;
            }
            _lastPresentSucceeded = LayeredWindowPresenter.TryPresent(
                Handle,
                bitmap,
                PointToScreen(Point.Empty),
                out _lastPresentDiagnostic);
            if (!string.Equals(previousDiagnostic, _lastPresentDiagnostic, StringComparison.Ordinal) &&
                !string.Equals(_lastLoggedPresentDiagnostic, _lastPresentDiagnostic, StringComparison.Ordinal))
            {
                _lastLoggedPresentDiagnostic = _lastPresentDiagnostic;
                var bounds = string.Join(
                    ";",
                    _boxes.Select(box =>
                        $"{box.Box.Id:N}@{box.Bounds.X:0},{box.Bounds.Y:0},{box.Bounds.Width:0},{box.Bounds.Height:0}"));
                DiagnosticLog.Info(
                    $"Desktop box layer monitor={_monitor.Id} present={_lastPresentSucceeded} " +
                    $"paint={_paintCount} boxes={_boxes.Count} bounds={bounds} {_lastPresentDiagnostic}");
            }
            if (!_lastPresentSucceeded)
            {
                DiagnosticLog.Error(
                    $"Desktop box layered presentation failed monitor={_monitor.Id}: {_lastPresentDiagnostic}",
                    new InvalidOperationException(_lastPresentDiagnostic));
            }
            return _lastPresentSucceeded;
        }
        catch (Exception exception)
        {
            _lastPresentSucceeded = false;
            _lastPresentDiagnostic = $"Desktop box rendering failed: {exception.Message}";
            DiagnosticLog.Error(
                $"Desktop box surface render failed monitor={_monitor.Id}",
                exception);
            return false;
        }
        finally
        {
            _presentingLayer = false;
        }
    }

    private void EnsureHitMaskBitmap()
    {
        if (_hitMaskBitmap is null ||
            _hitMaskBitmap.Width != ClientSize.Width ||
            _hitMaskBitmap.Height != ClientSize.Height)
        {
            _hitMaskBitmap?.Dispose();
            _hitMaskBitmap = DesktopLayerBitmapFactory.Create(
                ClientSize.Width,
                ClientSize.Height);
            _hitMaskPresented = false;
        }
    }

    internal void RequestRender()
    {
        PresentLayer();
        if (IsHandleCreated)
        {
            var ex = DesktopWindowTools.GetSurfaceExtendedStyle(Handle);
            DiagnosticLog.Info(
                $"Surface exstyle=0x{ex:X} " +
                $"layered={(ex & 0x80000) != 0} transparent={(ex & 0x20) != 0}");
        }
    }

    internal int PaintCount => _paintCount;

    internal bool IsLayerReady => _lastPresentSucceeded;

    internal string LayerDiagnostic => _lastPresentDiagnostic;

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_resourcesDisposed)
        {
            _resourcesDisposed = true;
            _iconLoadCancellation.Cancel();
            _animationFrameClock.Dispose();
            _hoverTimer.Stop();
            _hoverTimer.Dispose();
            _scrollHoverResumeTimer.Stop();
            _scrollHoverResumeTimer.Dispose();
            CancelPendingDragRender();
            _dragRenderTimer.Dispose();
            _itemHoverOverlay.Dispose();
            _headerActionOverlay.Dispose();
            _renameEditor?.Dispose();
            _renameEditor = null;
            _renamingBoxId = null;
            _renamingItemKey = null;
            ReleaseMovingBoxVisualCache();
            ClearHeightAnimationVisualCaches();
            _hitMaskBitmap?.Dispose();
            _hitMaskBitmap = null;
            _hitMaskPresented = false;
            LayeredWindowPresenter.Release(Handle);
            ClearIconCache();
            _iconLoadCancellation.Dispose();
            _shellContextMenu?.Dispose();
            _shellContextMenu = null;
            _titleEditorFont?.Dispose();
            _titleEditorFont = null;
            _editingBox = null;
            _titleEditorWindow.Dispose();
            DisposeBoxSearch();
            _headerToolTip.Dispose();
            Region?.Dispose();
        }
        base.Dispose(disposing);
    }

    internal int ClearIconCache()
    {
        ClearHeightAnimationVisualCaches();
        _iconCacheVersion++;
        _pendingIconLoads.Clear();
        _iconLoadRetries.Clear();
        var count = _iconCache.Count;
        foreach (var bitmap in _iconCache.Values)
        {
            bitmap?.Dispose();
        }
        _iconCache.Clear();
        Invalidate();
        QueueBoxIconPreload();
        return count;
    }

    internal void ClearSelection()
    {
        // The range anchor belongs to this surface's selection and dies with it,
        // even when another surface already took the selection over.
        _selectionAnchorKey = null;
        _selectionAnchorBoxId = null;
        if (_selection.Count == 0)
        {
            return;
        }
        _selection.Clear();
        Invalidate();
    }

    internal bool HasSelection => _selection.Count > 0;

    internal bool IsTitleEditing => _editingBox is not null || _titleEditorWindow.Visible;

    internal bool HasActiveInlineRename =>
        _renamingBoxId is not null &&
        _renamingItemKey is not null &&
        _renameEditor?.IsActive == true;

    internal bool CommitActiveInlineRename() =>
        _renameEditor?.CommitExternally() == true;

    internal void ReconcileHoverAfterDesktopPointerInteraction() =>
        QueueHoverReconcile();

    internal IReadOnlyList<DesktopItemRef> GetSelectedFileSystemItems(bool includeReadOnly = false)
    {
        var selected = new List<DesktopItemRef>();
        foreach (var box in DesktopBoxes)
        {
            if (!includeReadOnly && box.MappedFolder?.IsReadOnly == true)
            {
                continue;
            }

            selected.AddRange(GetCachedItemsForBox(box.Id).Where(item =>
                _selection.Contains(item.Key.ToString()) && item.FileSystemPath is not null));
        }
        return selected;
    }

    internal IReadOnlyList<DesktopItemRef> GetSelectedItems() => DesktopBoxes
        .SelectMany(box => GetCachedItemsForBox(box.Id).Where(item =>
            _selection.Contains(item.Key.ToString())))
        .ToArray();

    internal bool CanPasteSelectedOrHoveredBox(Point screenPoint) =>
        TryGetPasteTargetBox(screenPoint, out var box) && _runtime.CanPasteIntoBox(box);

    internal async Task<bool> PasteIntoSelectedOrHoveredBoxAsync(Point screenPoint)
    {
        if (!TryGetPasteTargetBox(screenPoint, out var box) || !_runtime.CanPasteIntoBox(box))
        {
            return false;
        }

        await PasteIntoBoxAsync(box);
        return true;
    }

    internal bool CanSelectAllSelectedOrHoveredItems(Point screenPoint) =>
        GetSingleSelectedBox() is not null || TryGetBoxAtScreenPoint(screenPoint, out _);

    internal bool SelectAllSelectedOrHoveredItems(Point screenPoint)
    {
        var selectedBox = GetSingleSelectedBox();
        if (selectedBox is null && !TryGetBoxAtScreenPoint(screenPoint, out selectedBox))
        {
            return false;
        }

        RebuildGeometry();
        var geometry = _boxes.LastOrDefault(box => box.Box.Id == selectedBox.Id);
        if (geometry is null)
        {
            return false;
        }

        _selection.Clear();
        foreach (var item in GetVisibleItemsForBox(geometry))
        {
            _selection.Add(item.Key.ToString());
        }
        _pressedItem = null;
        _pressedBoxId = null;
        Invalidate();
        return true;
    }

    internal int RenameSelectionCount => GetRenameTargets().Count;

    internal bool BeginRenameSelectedItem()
    {
        var targets = GetRenameTargets();
        if (targets.Count != 1)
        {
            return false;
        }

        _ = RenameItemAsync(targets[0].Box, targets[0].Item);
        return true;
    }

    private List<(DesktopBox Box, DesktopItemRef Item)> GetRenameTargets()
    {
        var targets = new List<(DesktopBox Box, DesktopItemRef Item)>();
        foreach (var box in DesktopBoxes)
        {
            if (box.MappedFolder?.IsReadOnly == true)
            {
                continue;
            }

            targets.AddRange(GetCachedItemsForBox(box.Id)
                .Where(item => _selection.Contains(item.Key.ToString()) && item.FileSystemPath is not null)
                .Select(item => (box, item)));
        }
        return targets;
    }

    private bool TryGetPasteTargetBox(Point screenPoint, out DesktopBox box)
    {
        box = GetSingleSelectedBox()!;
        if (box is not null)
        {
            return true;
        }
        return TryGetBoxAtScreenPoint(screenPoint, out box);
    }

    private DesktopBox? GetSingleSelectedBox()
    {
        var selectedBoxes = DesktopBoxes
            .Where(box => GetCachedItemsForBox(box.Id)
                .Any(item => _selection.Contains(item.Key.ToString())))
            .Take(2)
            .ToArray();
        return selectedBoxes.Length == 1 ? selectedBoxes[0] : null;
    }

    private bool TryGetBoxAtScreenPoint(Point screenPoint, out DesktopBox box)
    {
        RebuildGeometry();
        var point = ToDip(PointToClient(screenPoint));
        var geometry = _boxes.LastOrDefault(candidate => candidate.Bounds.Contains(point));
        if (geometry is null)
        {
            box = null!;
            return false;
        }

        box = geometry.Box;
        return true;
    }

    /// <summary>
    /// Receives a rendered desktop-icon drag without exposing a FileDrop
    /// payload to Explorer. Screen coordinates are used because the icon and
    /// box surfaces are sibling desktop children.
    /// </summary>
    internal bool TryDropDesktopItemsIntoBox(
        Point screenPoint,
        IReadOnlyList<string> itemKeys)
    {
        if (IsDisposed || itemKeys.Count == 0)
        {
            return false;
        }

        var point = ToDip(PointToClient(screenPoint));
        EnsureGeometry();
        var box = _boxes.LastOrDefault(candidate => candidate.Bounds.Contains(point));
        if (box is null || box.Box.IsMappedFolder || box.Box.MappedFolder?.IsReadOnly == true)
        {
            return false;
        }

        return AssignDesktopItemsAtDrop(box, point, itemKeys) > 0;
    }

    /// <summary>
    /// Updates only the visual target for a rendered desktop-icon drag. The
    /// item assignment remains deferred to TryDropDesktopItemsIntoBox so a
    /// pointer pass never changes a box or its manual ordering.
    /// </summary>
    internal bool UpdateDesktopItemDropPreview(
        Point screenPoint,
        IReadOnlyList<string> itemKeys,
        out bool pointerOverBox)
    {
        pointerOverBox = false;
        if (IsDisposed || itemKeys.Count == 0)
        {
            return false;
        }

        var point = ToDip(PointToClient(screenPoint));
        // This path runs for every captured desktop mouse move. Geometry is
        // already invalidated by workspace and box changes, so only rebuild
        // when that state is actually dirty.
        EnsureGeometry();
        var box = _boxes.LastOrDefault(candidate => candidate.Bounds.Contains(point));
        if (box is null)
        {
            ClearDropPreview();
            return false;
        }

        pointerOverBox = true;
        var manualTabIndex = GetManualBoxTabIndex(box, point);
        var acceptsDrop = !box.Box.IsMappedFolder && box.Box.MappedFolder?.IsReadOnly != true;
        // The visible feedback is the target outline and optional manual-tab
        // highlight. Repaint only when that visible target state changes.
        var renderNeeded = HasDesktopDropTargetVisualChanged(
            _dropPreview?.BoxId,
            _dropPreview?.AcceptsDrop,
            _dropPreview?.TargetManualTabIndex,
            box.Box.Id,
            acceptsDrop,
            manualTabIndex);
        SetDropPreview(
            new DropPreviewState(
                box.Box.Id,
                point,
                itemKeys.ToArray(),
                itemKeys.Count,
                acceptsDrop,
                manualTabIndex,
                FloatingCard: false),
            renderNeeded);
        return acceptsDrop;
    }

    internal void ClearDesktopItemDropPreview() => ClearDropPreview();

    private DesktopBox? GetBoxAtScreenPoint(Point screenPoint)
    {
        if (IsDisposed || _resourcesDisposed)
        {
            return null;
        }

        // Pure model-space hit test so the low-level input hook can query it
        // without touching WinForms controls from its callback thread. The
        // settled height is used on purpose: reading the animated height also
        // prunes finished animations and releases their cached bitmaps, which
        // belongs to the UI thread alone.
        var localX = (float)((screenPoint.X - _monitor.PixelBounds.X) / _scale);
        var localY = (float)((screenPoint.Y - _monitor.PixelBounds.Y) / _scale);
        return DesktopBoxes.LastOrDefault(box =>
        {
            var bounds = new RectangleF(
                (float)box.Bounds.X,
                (float)box.Bounds.Y,
                (float)box.Bounds.Width,
                (float)GetSettledBoxHeight(box));
            return bounds.Contains(localX, localY);
        });
    }

    internal bool IsPointOverBox(Point screenPoint) =>
        GetBoxAtScreenPoint(screenPoint) is not null;

    /// <summary>
    /// Scales the icons of the box under the pointer. Returns false when the
    /// pointer is not inside any box so the caller can fall back to the
    /// unassigned-icon zoom path.
    /// </summary>
    internal bool TryZoomBoxIconsAt(Point screenPoint, int delta)
    {
        var box = GetBoxAtScreenPoint(screenPoint);
        if (box is null)
        {
            return false;
        }

        var notches = Math.Max(1, Math.Abs(delta) / 120) * (delta > 0 ? 1 : -1);
        var targetSize = Math.Clamp(box.Appearance.IconSize + notches * 4, 24, 96);
        _runtime.SetBoxIconSize(box.Id, targetSize);
        DiagnosticLog.Info(
            $"Box icon zoom box={box.Id:N} size={box.Appearance.IconSize:0.##} delta={delta}");
        return true;
    }

    private bool UpdateWindowRegion()
    {
        var desktopBoxes = DesktopBoxes.ToArray();
        var currentRectangles = desktopBoxes.Select(box => new LayoutRect(
            box.Bounds.X,
            box.Bounds.Y,
            box.Bounds.Width,
            GetInteractionBoxHeight(box))).ToArray();
        _lastWindowRegionRectangles = currentRectangles;

        // Desktop child windows are composed as siblings beneath
        // SHELLDLL_DefView. A transparent full-monitor layered child can
        // still occlude the full-monitor icon child below it on some Explorer
        // compositions. Keep an actual native region around only the boxes so
        // icon pixels outside them remain visible and receive their own input.
        //
        // The drag path presents a complete UpdateLayeredWindow frame before
        // and after this region change. Requesting no native redraw here is
        // important: a redraw lets WinForms paint its rectangular backing
        // surface for one frame and was the source of the white rectangle seen
        // while moving a box.
        if (IsHandleCreated && !DesktopWindowTools.ApplyRoundedRegion(
                Handle,
                currentRectangles,
                _scale,
                _runtime.State.Settings.Appearance.CornerRadius,
                out var regionDiagnostic,
                redraw: !UsesLayeredPresentation))
        {
            HandleRegionFailure(regionDiagnostic);
            return false;
        }

        var diagnostic = $"{desktopBoxes.Length}:{_runtime.AreDesktopItemsHidden}";
        if (!string.Equals(diagnostic, _lastRegionDiagnostic, StringComparison.Ordinal))
        {
            _lastRegionDiagnostic = diagnostic;
            DiagnosticLog.Info(
                $"Surface region monitor={_monitor.Id} boxes={desktopBoxes.Length} hidden={_runtime.AreDesktopItemsHidden}");
        }
        return true;
    }

    private void HandleRegionFailure(string diagnostic)
    {
        if (_regionFailureHandled || IsDisposed)
        {
            return;
        }

        _regionFailureHandled = true;
        _animationFrameClock.StopWhenIdle(hasActiveAnimation: false);
        _hoverTimer.Stop();
        CancelPendingDragRender();
        try
        {
            Hide();
        }
        catch
        {
        }
        DiagnosticLog.Error(
            $"Desktop surface region verification failed monitor={_monitor.Id}: {diagnostic}",
            new InvalidOperationException(diagnostic));
    }

    private static RectangleF RectangleFromPoints(PointF first, PointF second) => new(
        Math.Min(first.X, second.X),
        Math.Min(first.Y, second.Y),
        Math.Abs(second.X - first.X),
        Math.Abs(second.Y - first.Y));

    private PointF ToDip(Point point) => new((float)(point.X / _scale), (float)(point.Y / _scale));

    private int ToPixel(double value) =>
        (int)Math.Round(value * _scale, MidpointRounding.AwayFromZero);

    private double SnapDipToPixel(double value) => ToPixel(value) / _scale;

    private static Color ParseOpaqueColor(string value)
    {
        try
        {
            var hex = value.TrimStart('#');
            var offset = hex.Length == 8 ? 2 : 0;
            return Color.FromArgb(255,
                Convert.ToByte(hex.Substring(offset, 2), 16),
                Convert.ToByte(hex.Substring(offset + 2, 2), 16),
                Convert.ToByte(hex.Substring(offset + 4, 2), 16));
        }
        catch
        {
            return Color.FromArgb(40, 44, 50);
        }
    }

    private static Color ResolveTitleColor(string value, Color boxBackground)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveAutoTextColor(boxBackground);
        }
        return ParseOpaqueColor(value);
    }

    internal static Color ResolveAccentColor(Color background, Color accent)
    {
        const double minimumContrast = 2.6d;
        if (ContrastRatio(background, accent) >= minimumContrast)
        {
            return Color.FromArgb(255, accent.R, accent.G, accent.B);
        }

        var target = UsesLightText(background)
            ? Color.White
            : Color.FromArgb(31, 35, 41);
        for (var step = 1; step <= 10; step++)
        {
            var amount = step / 10d;
            var adjusted = Color.FromArgb(
                255,
                (int)Math.Round(accent.R + (target.R - accent.R) * amount),
                (int)Math.Round(accent.G + (target.G - accent.G) * amount),
                (int)Math.Round(accent.B + (target.B - accent.B) * amount));
            if (ContrastRatio(background, adjusted) >= minimumContrast)
            {
                return adjusted;
            }
        }

        return target;
    }

    private static Color ResolveAutoTextColor(Color background) => UsesLightText(background)
        ? Color.White
        : Color.FromArgb(31, 35, 41);

    private static bool UsesLightText(Color background)
    {
        return ContrastRatio(background, Color.White) >=
            ContrastRatio(background, Color.FromArgb(31, 35, 41));
    }

    private static double ContrastRatio(Color first, Color second)
    {
        var firstLuminance = RelativeLuminance(first);
        var secondLuminance = RelativeLuminance(second);
        return (Math.Max(firstLuminance, secondLuminance) + 0.05) /
            (Math.Min(firstLuminance, secondLuminance) + 0.05);
    }

    private static double RelativeLuminance(Color color)
    {
        static double Linearize(byte channel)
        {
            var normalized = channel / 255d;
            return normalized <= 0.04045d
                ? normalized / 12.92d
                : Math.Pow((normalized + 0.055d) / 1.055d, 2.4d);
        }

        return 0.2126d * Linearize(color.R) +
            0.7152d * Linearize(color.G) +
            0.0722d * Linearize(color.B);
    }

    private static Color ApplyOpacity(Color color, double opacity) =>
        Color.FromArgb((int)Math.Round(255 * Math.Clamp(opacity, 0, 1)), color.R, color.G, color.B);


    private static GraphicsPath RoundedRectangle(RectangleF rectangle, float radius)
    {
        radius = Math.Min(Math.Max(0, radius), Math.Min(rectangle.Width, rectangle.Height) / 2);
        if (radius <= 0.1f)
        {
            var rectanglePath = new GraphicsPath();
            rectanglePath.AddRectangle(rectangle);
            return rectanglePath;
        }
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private void TryAction(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            DesktopConfirmationDialog.ShowMessage(
                this,
                _runtime.IsDarkTheme,
                "CrabDesk",
                exception.Message,
                DesktopDialogKind.Error);
        }
    }

    private sealed record BoxGeometry(
        DesktopBox Box,
        bool IsCollapsed,
        RectangleF Bounds,
        RectangleF Header,
        RectangleF TabBar,
        IReadOnlyList<MappedFolderTab> CategoryTabs,
        MappedFolderItemCategory ActiveMappedFolderCategory,
        IReadOnlyList<ManualBoxTab> ManualTabs,
        Guid? ActiveManualTabId,
        RectangleF Body,
        RectangleF Search,
        RectangleF AutoExpand,
        RectangleF Menu,
        RectangleF Resize);

    [Flags]
    private enum ResizeEdges
    {
        None = 0,
        Left = 1,
        Right = 2,
        Top = 4,
        Bottom = 8,
        TopLeft = Top | Left,
        TopRight = Top | Right,
        BottomLeft = Bottom | Left,
        BottomRight = Bottom | Right
    }

    private sealed record ItemGeometry(DesktopBox Box, DesktopItemRef Item, RectangleF Bounds);

    private sealed record MappedFolderTab(
        MappedFolderItemCategory Category,
        string Label,
        int Count);

    private sealed record ManualBoxTab(
        Guid? Id,
        string Label,
        int Count);

    private sealed record DropPreviewState(
        Guid BoxId,
        PointF Pointer,
        IReadOnlyList<string> ItemKeys,
        int ItemCount,
        bool AcceptsDrop,
        int? TargetManualTabIndex,
        bool FloatingCard);

    private sealed class DragImage(Bitmap bitmap, Point cursorOffset) : IDisposable
    {
        public Bitmap Bitmap { get; } = bitmap;
        public Point CursorOffset { get; } = cursorOffset;

        public void Dispose() => Bitmap.Dispose();
    }

    private readonly record struct ItemViewKey(
        Guid BoxId,
        string TabKey);

    internal sealed class InternalDragSession
    {
        public bool HandledByBox { get; set; }
        public bool HandledByDesktop { get; set; }
    }

    private readonly record struct IconBitmapKey(
        string ParsingName,
        int PixelSize,
        long ModifiedTicks,
        long Length);

    private sealed record BoxHeightAnimation(
        double FromHeight,
        double ToHeight,
        long StartedTimestamp,
        TimeSpan Duration);

    private sealed record BoxHeightVisualCache(
        Bitmap Bitmap,
        RectangleF Bounds);

    private readonly record struct IconLoadRetry(int Attempt, DateTimeOffset RetryAfter);
}
