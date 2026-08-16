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
    private const int HoverExpansionDelayMilliseconds = 120;
    private const int HoverCollapseDelayMilliseconds = 180;
    private const int HoverPollingIntervalMilliseconds = 25;
    private const int ScrollAnimationIntervalMilliseconds = 8;
    private const double ScrollAnimationDurationMilliseconds = 190;
    private const double ScrollEaseExponent = 2.2;
    private const int BoxHeightAnimationMilliseconds = 220;
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
    private readonly CrabDeskRuntime _runtime;
    private readonly MonitorLayout _monitor;
    private readonly double _scale;
    private readonly Dictionary<IconBitmapKey, Bitmap?> _iconCache = [];
    private readonly HashSet<IconBitmapKey> _pendingIconLoads = [];
    private readonly Dictionary<IconBitmapKey, IconLoadRetry> _iconLoadRetries = [];
    private readonly CancellationTokenSource _iconLoadCancellation = new();
    private readonly SemaphoreSlim _iconLoadGate = new(2, 2);
    private readonly HashSet<string> _selection = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Guid> _hoverExpandedBoxes = [];
    private readonly HoverExpansionController _hoverExpansion = new(
        TimeSpan.FromMilliseconds(HoverExpansionDelayMilliseconds),
        TimeSpan.FromMilliseconds(HoverCollapseDelayMilliseconds));
    private readonly HashSet<string> _selectionBase = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ItemViewKey, double> _scrollOffsets = [];
    private readonly Dictionary<Guid, MappedFolderItemCategory> _activeMappedFolderCategories = [];
    private readonly Dictionary<Guid, Guid?> _activeManualTabIds = [];
    private readonly Dictionary<Guid, IReadOnlyList<DesktopItemRef>> _boxItems = [];
    private readonly Dictionary<Guid, BoxHeightAnimation> _heightAnimations = [];
    private readonly List<BoxGeometry> _boxes = [];
    private readonly List<ItemGeometry> _items = [];
    private readonly List<ItemGeometry> _marqueeSelectionItems = [];
    private readonly HashSet<string> _marqueeSelectionKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(Guid BoxId, string ItemKey), RectangleF> _expandedItemHitBounds = [];
    private bool _geometryDirty = true;
    private IReadOnlyList<LayoutRect> _lastWindowRegionRectangles = [];
    private readonly Forms.Timer _animationTimer;
    private readonly Forms.Timer _hoverTimer;
    private readonly Forms.Timer _dragRenderTimer;
    private readonly Forms.Timer _scrollAnimationTimer;
    private ItemViewKey? _scrollAnimationKey;
    private double _scrollAnimationFrom;
    private double _scrollAnimationTo;
    private DateTime _scrollAnimationStartedUtc;
    private readonly DesktopHoverOverlay _itemHoverOverlay;
    private DesktopRenameEditor? _renameEditor;
    private DateTime _lastDragRenderUtc = DateTime.MinValue;
    private bool _dragRenderPending;
    private bool _hoverReconcilePending;
    private bool _confirmationInProgress;
    private Bitmap? _hitMaskBitmap;
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
    private DesktopBox? _selectionBox;
    private BoxGeometry? _selectionGeometry;
    private Guid? _pressedBoxId;
    private LayoutRect _startBounds;
    private PointF _pressPoint;
    private PointF _selectionStart;
    private RectangleF _selectionRectangle;
    private bool _dragStarted;
    private bool _dragDropCommitted;
    private bool _dragCancelled;
    private bool _showVirtualDesktopDropCursor;
    private DropPreviewState? _dropPreview;
    private string? _lastDesktopDropTargetKey;
    private string? _hoveredItemKey;
    private RectangleF? _lastItemHoverOverlayBounds;
    private bool _itemHoverOverlayUnavailable;
    private Guid? _hoveredAutoExpandBoxId;
    private LayoutRect? _transformDirtyBounds;
    private string? _lastRegionDiagnostic;
    private bool _lastPresentSucceeded;
    private string _lastPresentDiagnostic = string.Empty;
    private string _lastLoggedPresentDiagnostic = string.Empty;
    private bool _presentingLayer;
    private bool _isCompositedByIconSurface;
    private Action? _iconLayerRenderRequest;
    private Action<PointF, IReadOnlyList<string>?, IReadOnlyList<string>?>? _iconDragStateForward;
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

        _animationTimer = new Forms.Timer { Interval = 15 };
        _animationTimer.Tick += OnAnimationTick;
        _hoverTimer = new Forms.Timer { Interval = HoverPollingIntervalMilliseconds };
        _hoverTimer.Tick += OnHoverTimer;
        _hoverTimer.Start();
        _scrollAnimationTimer = new Forms.Timer { Interval = ScrollAnimationIntervalMilliseconds };
        _scrollAnimationTimer.Tick += OnScrollAnimationTick;
        _dragRenderTimer = new Forms.Timer { Interval = DragRenderCoalesceMilliseconds };
        _dragRenderTimer.Tick += OnDragRenderTimerTick;
        _itemHoverOverlay = new DesktopHoverOverlay();
        Controls.Add(_itemHoverOverlay);
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
            parameters.ExStyle |= WsExLayered;
            return parameters;
        }
    }

    private bool UsesLayeredPresentation =>
        IsHandleCreated &&
        (DesktopWindowTools.GetSurfaceExtendedStyle(Handle) & WsExLayered) != 0;

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

    private IReadOnlyList<DesktopBox> DesktopBoxes =>
        BoxStacking.OrderBackToFront(_runtime.State.Boxes, _monitor.Id);

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
        return PresentLayer();
    }

    internal bool UpdateInteractionRegion()
    {
        if (!UpdateWindowRegion())
        {
            return false;
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

    internal void PrepareIconLayerComposition() =>
        _isCompositedByIconSurface = true;

    internal bool IsTransformActive => _movingBox is not null || _resizingBox is not null;

    internal bool HasDynamicVisual =>
        IsTransformActive || _dragStarted || _dropPreview is not null || _selectionBox is not null ||
        _heightAnimations.Count > 0;

    /// <summary>
    /// True when the only dynamic visual on this surface is the hover
    /// expand/collapse height animation. Such frames are rendered through the
    /// single-window icon-layer channel so the box never has to be handed
    /// between the settled layer and the drag overlay at the animation
    /// boundaries (which flashes for one compositor frame).
    /// </summary>
    internal bool IsHeightAnimationOnly =>
        _heightAnimations.Count > 0 &&
        !IsTransformActive &&
        !_dragStarted &&
        _dropPreview is null &&
        _selectionBox is null;

    internal bool IsMarqueeSelectionActive => _selectionBox is not null;

    internal int DynamicVisualVersion => _dynamicVisualVersion;

    internal RectangleF? GetDynamicVisualBounds()
    {
        if (IsDisposed || _resourcesDisposed || !HasDynamicVisual)
        {
            return null;
        }

        EnsureGeometry();
        RectangleF? bounds = null;
        var transformBox = _movingBox ?? _resizingBox;
        if (transformBox is not null)
        {
            var geometry = _boxes.FirstOrDefault(box => box.Box.Id == transformBox.Id);
            if (geometry is not null)
            {
                bounds = GetTransformGeometry(geometry).Bounds;
            }
        }

        if (_dropPreview is { } preview && preview.BoxId != transformBox?.Id)
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

        foreach (var animatedBoxId in _heightAnimations.Keys)
        {
            var geometry = _boxes.FirstOrDefault(box => box.Box.Id == animatedBoxId);
            if (geometry is not null)
            {
                bounds = bounds is { } existing
                    ? RectangleF.Union(existing, geometry.Bounds)
                    : geometry.Bounds;
            }
        }

        return bounds;
    }

    internal void SetIconLayerRenderRequest(Action renderRequest) =>
        _iconLayerRenderRequest = renderRequest;

    // The icon surface draws every drag ghost. While the OLE drag route is
    // owned by this (visually transparent) box window, forward the pointer
    // and payload so the ghost keeps following the mouse. Null payloads mean
    // the drag left this surface or ended.
    internal void SetIconDragStateForward(
        Action<PointF, IReadOnlyList<string>?, IReadOnlyList<string>?> forward) =>
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
        var transformId = (_movingBox ?? _resizingBox)?.Id;
        var previewId = _dropPreview?.BoxId;
        var selectionId = _selectionBox?.Id;
        var hasDynamicVisual = HasDynamicVisual;
        var animatedBoxIds = hasDynamicVisual
            ? _heightAnimations.Keys.ToHashSet()
            : new HashSet<Guid>();
        foreach (var box in _boxes.Where(box =>
                     box.Bounds.IntersectsWith(clipBounds) &&
                     (!hasDynamicVisual ||
                      (box.Box.Id != transformId &&
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
    {
        if (IsDisposed || _resourcesDisposed)
        {
            return;
        }

        EnsureGeometry();
        var transformBox = _movingBox ?? _resizingBox;
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
            var geometry = _boxes.FirstOrDefault(box => box.Box.Id == transformBox.Id);
            if (geometry is not null)
            {
                DrawBox(
                    graphics,
                    GetTransformGeometry(geometry),
                    clipBounds,
                    includeItemHoverFeedback: false);
            }
        }

        foreach (var animatedBoxId in _heightAnimations.Keys)
        {
            if (animatedBoxId == transformBox?.Id)
            {
                continue;
            }
            var geometry = _boxes.FirstOrDefault(box => box.Box.Id == animatedBoxId);
            if (geometry is not null)
            {
                DrawBox(
                    graphics,
                    geometry,
                    clipBounds,
                    includeItemHoverFeedback: false);
            }
        }

        if (_dropPreview is { } preview && preview.BoxId != transformBox?.Id)
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
                using (var graphics = Graphics.FromImage(_hitMaskBitmap!))
                {
                    graphics.Clear(Color.Transparent);
                    graphics.ScaleTransform((float)_scale, (float)_scale);
                    // Per-pixel alpha zero is click-through for a layered
                    // child. Keep a one-alpha hit mask only inside the native
                    // box region; it is visually imperceptible but preserves
                    // the existing box mouse and drag handlers.
                    using var hitMask = new SolidBrush(Color.FromArgb(1, Color.Black));
                    // Hit masking only needs the current model bounds. Avoid a
                    // full geometry rebuild on every coalesced drag frame.
                    foreach (var box in DesktopBoxes)
                    {
                        var hitHeight = (float)GetVisualBoxHeight(box);
                        graphics.FillRectangle(
                            hitMask,
                            (float)box.Bounds.X,
                            (float)box.Bounds.Y,
                            (float)box.Bounds.Width,
                            hitHeight);
                    }
                    graphics.ResetTransform();
                }

                _lastPresentSucceeded = LayeredWindowPresenter.TryPresent(
                    Handle,
                    _hitMaskBitmap!,
                    PointToScreen(Point.Empty),
                    out _lastPresentDiagnostic);
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
            _animationTimer.Stop();
            _animationTimer.Dispose();
            _hoverTimer.Stop();
            _hoverTimer.Dispose();
            _scrollAnimationTimer.Stop();
            _scrollAnimationTimer.Dispose();
            CancelPendingDragRender();
            _dragRenderTimer.Dispose();
            _itemHoverOverlay.Dispose();
            _renameEditor?.Dispose();
            _renameEditor = null;
            _hitMaskBitmap?.Dispose();
            _hitMaskBitmap = null;
            LayeredWindowPresenter.Release(Handle);
            ClearIconCache();
            _iconLoadCancellation.Dispose();
            _shellContextMenu?.Dispose();
            _shellContextMenu = null;
            _titleEditorFont?.Dispose();
            _titleEditorFont = null;
            _editingBox = null;
            _titleEditorWindow.Dispose();
            _headerToolTip.Dispose();
            Region?.Dispose();
        }
        base.Dispose(disposing);
    }

    internal int ClearIconCache()
    {
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
        return count;
    }

    internal void ClearSelection()
    {
        if (_selection.Count == 0)
        {
            return;
        }
        _selection.Clear();
        Invalidate();
    }

    internal bool HasSelection => _selection.Count > 0;

    internal bool IsTitleEditing => _editingBox is not null || _titleEditorWindow.Visible;

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
        // The preview is a cell-level insertion marker, so it only needs a
        // repaint when the pointer crosses into or out of an item. Throttling
        // keeps a hovered desktop drag from repainting the whole monitor
        // layer on every mouse move.
        var targetKey = _items.LastOrDefault(candidate =>
                candidate.Box.Id == box.Box.Id &&
                candidate.Bounds.Contains(point) &&
                !itemKeys.Contains(candidate.Item.Key.ToString(), StringComparer.OrdinalIgnoreCase))
            ?.Item.Key.ToString();
        var renderNeeded = !string.Equals(
            _lastDesktopDropTargetKey,
            targetKey,
            StringComparison.OrdinalIgnoreCase);
        _lastDesktopDropTargetKey = targetKey;
        SetDropPreview(
            new DropPreviewState(
                box.Box.Id,
                point,
                itemKeys.ToArray(),
                itemKeys.Count,
                acceptsDrop,
                DropPreviewKind.DesktopAssign,
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
        // without touching WinForms controls from its callback thread.
        var localX = (float)((screenPoint.X - _monitor.PixelBounds.X) / _scale);
        var localY = (float)((screenPoint.Y - _monitor.PixelBounds.Y) / _scale);
        return DesktopBoxes.LastOrDefault(box =>
        {
            var bounds = new RectangleF(
                (float)box.Bounds.X,
                (float)box.Bounds.Y,
                (float)box.Bounds.Width,
                (float)GetVisualBoxHeight(box));
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
            GetVisualBoxHeight(box))).ToArray();
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
        _animationTimer.Stop();
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

    private static void TryAction(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            Forms.MessageBox.Show(exception.Message, "CrabDesk", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Error);
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

    private enum DropPreviewKind
    {
        Assign,
        DesktopAssign,
        Reorder
    }

    private sealed record DropPreviewState(
        Guid BoxId,
        PointF Pointer,
        IReadOnlyList<string> ItemKeys,
        int ItemCount,
        bool AcceptsDrop,
        DropPreviewKind Kind,
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
        DateTimeOffset StartedAt,
        TimeSpan Duration);

    private readonly record struct IconLoadRetry(int Attempt, DateTimeOffset RetryAfter);
}
