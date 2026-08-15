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

internal sealed class DesktopBoxForm : Forms.Form
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
    private const int SelectedItemFillAlpha = 112;
    private const int BoxHoverFillAlpha = 156;
    private const int BoxHoverBorderAlpha = 232;
    private const float HoverBrightness = 0.30f;
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

    private void PaintRegularSurface(Graphics graphics)
    {
        if (IsDisposed || ClientSize.Width <= 0 || ClientSize.Height <= 0)
        {
            return;
        }

        RebuildGeometry();
        graphics.CompositingMode = CompositingMode.SourceOver;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        graphics.TextContrast = 4;
        graphics.ScaleTransform((float)_scale, (float)_scale);
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

    private int AssignDesktopItemsAtDrop(
        BoxGeometry target,
        PointF point,
        IReadOnlyList<string> itemKeys)
    {
        var incoming = itemKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (incoming.Length == 0)
        {
            return 0;
        }
        var incomingKeys = incoming.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var beforeKey = ResolveInsertBeforeKey(target, point, incomingKeys);
        var assigned = _runtime.AssignItems(incoming, target.Box.Id);
        if (assigned > 0 && beforeKey is not null)
        {
            // A desktop drop is an insertion, not an append. ReorderItems also
            // promotes a sorted box to manual mode so the chosen position is
            // retained after the next refresh.
            _runtime.ReorderBoxItems(target.Box.Id, incoming, beforeKey);
        }

        var manualTab = GetManualBoxTabAtPoint(target, point);
        if (assigned > 0 && manualTab is not null)
        {
            _runtime.MoveItemsToManualTab(target.Box.Id, incoming, manualTab.Id);
        }
        return assigned;
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

    private void RebuildGeometry()
    {
        _boxes.Clear();
        _items.Clear();
        _expandedItemHitBounds.Clear();
        foreach (var box in DesktopBoxes)
        {
            var titleBarHeight = (float)box.Appearance.TitleBarHeight;
            var height = (float)GetVisualBoxHeight(box);
            var isCollapsed = IsEffectivelyCollapsed(box);
            var bounds = new RectangleF((float)box.Bounds.X, (float)box.Bounds.Y, (float)box.Bounds.Width, (float)height);
            var manualTabs = isCollapsed ? [] : GetManualTabs(box);
            var categoryTabs = manualTabs.Count == 0 && !isCollapsed ? GetMappedFolderTabs(box) : [];
            var tabCount = categoryTabs.Count + manualTabs.Count;
            var tabBar = tabCount == 0
                ? RectangleF.Empty
                : new RectangleF(
                    bounds.X + 8,
                    bounds.Y + titleBarHeight,
                    Math.Max(0, bounds.Width - 16),
                    MappedFolderTabBarHeight);
            var bodyTop = tabBar.IsEmpty ? titleBarHeight + 8 : titleBarHeight + tabBar.Height + 8;
            var geometry = new BoxGeometry(
                box,
                isCollapsed,
                bounds,
                new RectangleF(bounds.X, bounds.Y, bounds.Width, titleBarHeight),
                tabBar,
                categoryTabs,
                GetActiveMappedFolderCategory(box.Id, categoryTabs),
                manualTabs,
                GetActiveManualTabId(box.Id, manualTabs),
                new RectangleF(bounds.X + 8, bounds.Y + bodyTop, bounds.Width - 16, Math.Max(0, bounds.Height - bodyTop - 8)),
                new RectangleF(bounds.Right - 62, bounds.Y + (titleBarHeight - 28) / 2, 26, 28),
                new RectangleF(bounds.Right - 32, bounds.Y + (titleBarHeight - 28) / 2, 26, 28),
                new RectangleF(bounds.Right - 18, bounds.Bottom - 18, 18, 18));
            _boxes.Add(geometry);
            if (!isCollapsed)
            {
                BuildItemGeometry(geometry);
            }
        }
        _geometryDirty = false;
    }

    private void EnsureGeometry()
    {
        if (_geometryDirty || _boxes.Count == 0)
        {
            RebuildGeometry();
        }
    }

    private BoxGeometry GetTransformGeometry(BoxGeometry geometry)
    {
        if (_movingBox is null || geometry.Box.Id != _movingBox.Id)
        {
            return geometry;
        }

        var offsetX = (float)(_movingBox.Bounds.X - _startBounds.X);
        var offsetY = (float)(_movingBox.Bounds.Y - _startBounds.Y);
        if (Math.Abs(offsetX) < float.Epsilon && Math.Abs(offsetY) < float.Epsilon)
        {
            return geometry;
        }

        return geometry with
        {
            Bounds = OffsetBounds(geometry.Bounds, offsetX, offsetY),
            Header = OffsetBounds(geometry.Header, offsetX, offsetY),
            TabBar = OffsetBounds(geometry.TabBar, offsetX, offsetY),
            Body = OffsetBounds(geometry.Body, offsetX, offsetY),
            AutoExpand = OffsetBounds(geometry.AutoExpand, offsetX, offsetY),
            Menu = OffsetBounds(geometry.Menu, offsetX, offsetY),
            Resize = OffsetBounds(geometry.Resize, offsetX, offsetY)
        };
    }

    private static RectangleF OffsetBounds(RectangleF bounds, float offsetX, float offsetY) => new(
        bounds.X + offsetX,
        bounds.Y + offsetY,
        bounds.Width,
        bounds.Height);

    private void BuildItemGeometry(BoxGeometry geometry)
    {
        if (_runtime.AreDesktopItemsHidden)
        {
            return;
        }
        var items = GetVisibleItemsForBox(geometry);
        var appearance = _runtime.State.Settings.Appearance;
        var layout = DesktopItemLayoutEngine.CalculateVisible(
            geometry.Box.ViewMode,
            new LayoutRect(geometry.Body.X, geometry.Body.Y, geometry.Body.Width, geometry.Body.Height),
            items.Count,
            geometry.Box.Appearance.IconSize,
            DesktopItemLayoutEngine.ScaleIconSpacing(appearance.IconHorizontalSpacing, geometry.Box.Appearance.IconSize),
            DesktopItemLayoutEngine.ScaleIconSpacing(appearance.IconVerticalSpacing, geometry.Box.Appearance.IconSize),
            _scrollOffsets.GetValueOrDefault(GetItemViewKey(geometry)));
        _scrollOffsets[GetItemViewKey(geometry)] = layout.ScrollOffset;
        foreach (var entry in layout.Items)
        {
            var itemBounds = entry.Bounds;
            var bounds = new RectangleF(
                (float)itemBounds.X,
                (float)itemBounds.Y,
                (float)itemBounds.Width,
                (float)itemBounds.Height);
            if (bounds.Bottom >= geometry.Body.Top && bounds.Top <= geometry.Body.Bottom)
            {
                _items.Add(new ItemGeometry(geometry.Box, items[entry.Index], bounds));
            }
        }
    }

    private IReadOnlyList<DesktopItemRef> GetVisibleItemsForBox(BoxGeometry geometry)
    {
        var items = GetCachedItemsForBox(geometry.Box.Id);
        if (geometry.ManualTabs.Count > 0)
        {
            return geometry.ActiveManualTabId is not { } tabId
                ? items
                : items.Where(item =>
                        geometry.Box.ItemTabAssignments.TryGetValue(item.Key.ToString(), out var assignedTabId) &&
                        assignedTabId == tabId)
                    .ToArray();
        }
        return geometry.ActiveMappedFolderCategory == MappedFolderItemCategory.All
            ? items
            : items.Where(item => MappedFolderItemCategoryClassifier.Matches(
                    geometry.ActiveMappedFolderCategory,
                    item))
                .ToArray();
    }

    private IReadOnlyList<MappedFolderTab> GetMappedFolderTabs(DesktopBox box)
    {
        if (box.MappedFolder?.EnableCategoryTabs != true)
        {
            return [];
        }

        var items = GetCachedItemsForBox(box.Id);
        var counts = items
            .GroupBy(MappedFolderItemCategoryClassifier.GetCategory)
            .ToDictionary(group => group.Key, group => group.Count());
        if (counts.Count < 2)
        {
            return [];
        }

        var tabs = new List<MappedFolderTab>
        {
            new(MappedFolderItemCategory.All, "全部", items.Count)
        };
        foreach (var category in MappedFolderTabOrder)
        {
            if (counts.TryGetValue(category, out var count))
            {
                tabs.Add(new MappedFolderTab(category, GetMappedFolderCategoryLabel(category), count));
            }
        }

        return tabs;
    }

    private IReadOnlyList<ManualBoxTab> GetManualTabs(DesktopBox box)
    {
        if (box.IsMappedFolder || box.ManualTabs.Count == 0)
        {
            return [];
        }

        var items = GetCachedItemsForBox(box.Id);
        var tabs = new List<ManualBoxTab>
        {
            new(null, "全部", items.Count)
        };
        foreach (var tab in box.ManualTabs)
        {
            var count = items.Count(item =>
                box.ItemTabAssignments.TryGetValue(item.Key.ToString(), out var assignedTabId) &&
                assignedTabId == tab.Id);
            tabs.Add(new ManualBoxTab(tab.Id, tab.Title, count));
        }
        return tabs;
    }

    private MappedFolderItemCategory GetActiveMappedFolderCategory(
        Guid boxId,
        IReadOnlyList<MappedFolderTab> tabs)
    {
        if (tabs.Count == 0)
        {
            _activeMappedFolderCategories[boxId] = MappedFolderItemCategory.All;
            return MappedFolderItemCategory.All;
        }

        var category = _activeMappedFolderCategories.GetValueOrDefault(boxId);
        if (tabs.Any(tab => tab.Category == category))
        {
            return category;
        }

        _activeMappedFolderCategories[boxId] = MappedFolderItemCategory.All;
        return MappedFolderItemCategory.All;
    }

    private Guid? GetActiveManualTabId(Guid boxId, IReadOnlyList<ManualBoxTab> tabs)
    {
        if (tabs.Count == 0)
        {
            _activeManualTabIds.Remove(boxId);
            return null;
        }

        if (_activeManualTabIds.TryGetValue(boxId, out var activeTabId) &&
            tabs.Any(tab => tab.Id == activeTabId))
        {
            return activeTabId;
        }

        _activeManualTabIds[boxId] = null;
        return null;
    }

    private static ItemViewKey GetItemViewKey(BoxGeometry geometry) =>
        new(
            geometry.Box.Id,
            geometry.ManualTabs.Count > 0
                ? $"manual:{geometry.ActiveManualTabId?.ToString("N") ?? "all"}"
                : $"mapped:{geometry.ActiveMappedFolderCategory}");

    private static string GetMappedFolderCategoryLabel(MappedFolderItemCategory category) => category switch
    {
        MappedFolderItemCategory.Folder => "目录",
        MappedFolderItemCategory.Image => "图片",
        MappedFolderItemCategory.Document => "文档",
        MappedFolderItemCategory.Archive => "压缩",
        MappedFolderItemCategory.Other => "其它",
        _ => "全部"
    };

    private ItemGeometry? GetItemAtPoint(BoxGeometry? box, PointF point)
    {
        // A scrolled item's layout rectangle can extend above the content
        // viewport. Painting clips that portion to Body; hit testing must use
        // the same boundary so header buttons always receive their clicks.
        if (box is null || !box.Body.Contains(point))
        {
            return null;
        }

        return _items.LastOrDefault(candidate =>
            candidate.Box.Id == box.Box.Id && GetItemHitBounds(candidate, box.Body).Contains(point));
    }

    private RectangleF GetItemHitBounds(ItemGeometry item, RectangleF contentBounds)
    {
        var key = (item.Box.Id, item.Item.Key.ToString());
        return _expandedItemHitBounds.TryGetValue(key, out var expandedBounds)
            ? RectangleF.Intersect(expandedBounds, contentBounds)
            : item.Bounds;
    }

    private bool TrySelectBoxTab(BoxGeometry box, PointF point)
    {
        var manualTab = GetManualBoxTabAtPoint(box, point);
        if (manualTab is not null)
        {
            if (manualTab.Id == box.ActiveManualTabId)
            {
                return true;
            }

            _activeManualTabIds[box.Box.Id] = manualTab.Id;
            _scrollOffsets.Remove(new ItemViewKey(
                box.Box.Id,
                $"manual:{manualTab.Id?.ToString("N") ?? "all"}"));
            ClearBoxItemSelection(box.Box.Id);
            _geometryDirty = true;
            InvalidateDip(box.TabBar);
            InvalidateDip(box.Body);
            return true;
        }

        var tab = GetMappedFolderTabAtPoint(box, point);
        if (tab is null)
        {
            return false;
        }

        if (tab.Category == box.ActiveMappedFolderCategory)
        {
            return true;
        }

        _activeMappedFolderCategories[box.Box.Id] = tab.Category;
        _scrollOffsets.Remove(new ItemViewKey(box.Box.Id, $"mapped:{tab.Category}"));
        ClearBoxItemSelection(box.Box.Id);
        _geometryDirty = true;
        InvalidateDip(box.TabBar);
        InvalidateDip(box.Body);
        return true;
    }

    private void ClearBoxItemSelection(Guid boxId)
    {
        var boxItemKeys = GetCachedItemsForBox(boxId)
            .Select(item => item.Key.ToString())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _selection.RemoveWhere(boxItemKeys.Contains);
        _pressedItem = null;
        _pressedBoxId = null;
    }

    private static MappedFolderTab? GetMappedFolderTabAtPoint(BoxGeometry box, PointF point)
    {
        if (box.CategoryTabs.Count == 0 || !box.TabBar.Contains(point))
        {
            return null;
        }

        for (var index = 0; index < box.CategoryTabs.Count; index++)
        {
            if (GetBoxTabBounds(box, index, box.CategoryTabs.Count).Contains(point))
            {
                return box.CategoryTabs[index];
            }
        }

        return null;
    }

    private static ManualBoxTab? GetManualBoxTabAtPoint(BoxGeometry box, PointF point)
    {
        if (box.ManualTabs.Count == 0 || !box.TabBar.Contains(point))
        {
            return null;
        }

        for (var index = 0; index < box.ManualTabs.Count; index++)
        {
            if (GetBoxTabBounds(box, index, box.ManualTabs.Count).Contains(point))
            {
                return box.ManualTabs[index];
            }
        }

        return null;
    }

    private static RectangleF GetBoxTabBounds(BoxGeometry box, int index, int tabCount)
    {
        var tabWidth = box.TabBar.Width / tabCount;
        return new RectangleF(
            box.TabBar.X + tabWidth * index,
            box.TabBar.Y,
            index == tabCount - 1
                ? box.TabBar.Right - (box.TabBar.X + tabWidth * index)
                : tabWidth,
            box.TabBar.Height);
    }

    private static int? GetManualBoxTabIndex(BoxGeometry box, PointF point)
    {
        if (box.ManualTabs.Count == 0 || !box.TabBar.Contains(point))
        {
            return null;
        }

        for (var index = 0; index < box.ManualTabs.Count; index++)
        {
            if (GetBoxTabBounds(box, index, box.ManualTabs.Count).Contains(point))
            {
                return index;
            }
        }

        return null;
    }

    private void SetDropPreview(DropPreviewState? preview, bool requestRender = true)
    {
        if (_dropPreview == preview)
        {
            return;
        }

        if (_dropPreview?.BoxId != preview?.BoxId)
        {
            _dynamicVisualVersion++;
        }
        _dropPreview = preview;
        if (requestRender)
        {
            RequestDragRender();
        }
    }

    private void ClearDropPreview()
    {
        _lastDesktopDropTargetKey = null;
        if (_dropPreview is null)
        {
            return;
        }

        _dropPreview = null;
        _dynamicVisualVersion++;
        RequestDragRender();
    }

    private void InvalidateDropPreview(Guid? boxId)
    {
        if (boxId is not { } id)
        {
            return;
        }

        var box = _boxes.FirstOrDefault(candidate => candidate.Box.Id == id);
        if (box is not null)
        {
            InvalidateDip(box.Bounds);
        }
    }

    private void DrawDropTargetFeedback(
        Graphics graphics,
        BoxGeometry geometry,
        RectangleF clipBounds)
    {
        var preview = _dropPreview;
        if (preview is null || preview.BoxId != geometry.Box.Id ||
            !geometry.Bounds.IntersectsWith(clipBounds))
        {
            return;
        }

        var accent = ParseOpaqueColor(geometry.Box.Appearance.Accent);
        var outline = RectangleF.Inflate(geometry.Bounds, -1.5f, -1.5f);
        var alpha = preview.AcceptsDrop ? 220 : 132;
        using var border = new Pen(Color.FromArgb(alpha, accent), preview.AcceptsDrop ? 2 : 1)
        {
            DashStyle = preview.AcceptsDrop ? DashStyle.Solid : DashStyle.Dash
        };
        using var path = RoundedRectangle(
            outline,
            Math.Max(2, (float)_runtime.State.Settings.Appearance.CornerRadius - 1));
        graphics.DrawPath(border, path);

        if (preview.TargetManualTabIndex is not { } tabIndex ||
            geometry.ManualTabs.Count == 0 || geometry.TabBar.IsEmpty)
        {
            return;
        }

        var tabBounds = RectangleF.Inflate(
            GetBoxTabBounds(geometry, tabIndex, geometry.ManualTabs.Count),
            -3,
            -3);
        using var tabFill = new SolidBrush(Color.FromArgb(preview.AcceptsDrop ? 44 : 26, accent));
        using var tabBorder = new Pen(Color.FromArgb(preview.AcceptsDrop ? 210 : 118, accent), 1);
        using var tabPath = RoundedRectangle(tabBounds, 3);
        graphics.FillPath(tabFill, tabPath);
        graphics.DrawPath(tabBorder, tabPath);
    }

    private void DrawDropInsertionFeedback(
        Graphics graphics,
        BoxGeometry geometry,
        RectangleF clipBounds)
    {
        var preview = _dropPreview;
        if (preview is null || preview.BoxId != geometry.Box.Id ||
            !preview.AcceptsDrop || !geometry.Body.IntersectsWith(clipBounds))
        {
            return;
        }

        var accent = ParseOpaqueColor(geometry.Box.Appearance.Accent);
        if (preview.Kind == DropPreviewKind.Reorder)
        {
            DrawReorderDropPreview(graphics, geometry, preview, accent);
            DrawBoxItemFloatingPreview(graphics, geometry, preview, accent);
            return;
        }

        if (preview.Kind == DropPreviewKind.DesktopAssign)
        {
            DrawDesktopItemDropPreview(graphics, geometry, preview, accent);
            return;
        }

        // External file drags and desktop-icon drags already carry their own
        // mouse-following ghost, so only box-item drags (which have no shell
        // drag image) draw the shared card here.
        if (preview.FloatingCard)
        {
            DrawBoxItemFloatingPreview(graphics, geometry, preview, accent);
        }
    }

    private void DrawBoxItemFloatingPreview(
        Graphics graphics,
        BoxGeometry geometry,
        DropPreviewState preview,
        Color accent)
    {
        // Normal boxes use a private virtual drag payload, so Explorer does
        // not reliably supply a shell drag image. The box draws the shared
        // ghost card while the pointer is inside a box; the desktop surface
        // takes over with the same card once the pointer leaves it.
        var previewItem = preview.ItemKeys
            .Select(key => _runtime.FindItemByKey(key))
            .FirstOrDefault(item => item is not null);
        // Ask for the box's own grid icon size: the grid caches those sizes
        // synchronously, so the ghost gets the real icon immediately instead
        // of falling back to the placeholder while an odd size loads async.
        var iconSize = Math.Clamp(
            (float)geometry.Box.Appearance.IconSize * 1.05f,
            32f,
            64f);
        var previewIcon = previewItem is null
            ? ShellIconProvider.GetGenericFileIcon()
            : GetIconBitmap(previewItem, iconSize) ?? ShellIconProvider.GetGenericFileIcon();
        using var font = CreateFont(
            geometry.Box.Appearance.LabelFontFamily,
            9f,
            FontStyle.Regular,
            GraphicsUnit.Point);
        DragGhostRenderer.Draw(
            graphics,
            preview.Pointer,
            previewIcon,
            previewItem?.DisplayName ?? preview.ItemKeys.FirstOrDefault() ?? string.Empty,
            preview.ItemCount,
            font);
    }

    private void DrawReorderDropPreview(
        Graphics graphics,
        BoxGeometry geometry,
        DropPreviewState preview,
        Color accent)
    {
        var previewKeys = preview.ItemKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (previewKeys.Count == 0)
        {
            return;
        }

        var currentItems = GetCachedItemsForBox(geometry.Box.Id);
        var currentKeys = currentItems.Select(item => item.Key.ToString()).ToArray();
        var projectedKeys = LayoutCoordinator.ProjectReorderedKeys(
            geometry.Box,
            currentKeys,
            previewKeys,
            GetReorderBeforeKey(geometry, preview.Pointer));
        var itemsByKey = currentItems.ToDictionary(
            item => item.Key.ToString(),
            StringComparer.OrdinalIgnoreCase);
        var visibleKeys = GetVisibleItemsForBox(geometry)
            .Select(item => item.Key.ToString())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var projectedItems = projectedKeys
            .Where(key => visibleKeys.Contains(key) && itemsByKey.ContainsKey(key))
            .Select(key => itemsByKey[key])
            .ToArray();
        if (projectedItems.Length == 0)
        {
            return;
        }

        var appearance = _runtime.State.Settings.Appearance;
        var layout = DesktopItemLayoutEngine.CalculateVisible(
            geometry.Box.ViewMode,
            new LayoutRect(geometry.Body.X, geometry.Body.Y, geometry.Body.Width, geometry.Body.Height),
            projectedItems.Length,
            geometry.Box.Appearance.IconSize,
            DesktopItemLayoutEngine.ScaleIconSpacing(appearance.IconHorizontalSpacing, geometry.Box.Appearance.IconSize),
            DesktopItemLayoutEngine.ScaleIconSpacing(appearance.IconVerticalSpacing, geometry.Box.Appearance.IconSize),
            _scrollOffsets.GetValueOrDefault(GetItemViewKey(geometry)));
        foreach (var entry in layout.Items)
        {
            var item = projectedItems[entry.Index];
            if (!previewKeys.Contains(item.Key.ToString()))
            {
                continue;
            }

            var bounds = entry.Bounds;
            var itemGeometry = new ItemGeometry(
                geometry.Box,
                item,
                new RectangleF((float)bounds.X, (float)bounds.Y, (float)bounds.Width, (float)bounds.Height));
            DrawDropPreviewFrame(graphics, GetItemIconBounds(itemGeometry), accent);
        }
    }

    private void DrawDesktopItemDropPreview(
        Graphics graphics,
        BoxGeometry geometry,
        DropPreviewState preview,
        Color accent)
    {
        var previewItemKeys = _runtime.Items
            .Where(item => preview.ItemKeys.Contains(item.Key.ToString(), StringComparer.OrdinalIgnoreCase))
            .Select(item => item.Key.ToString())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (previewItemKeys.Count == 0)
        {
            return;
        }

        var projectedItems = _runtime.GetItemsForBoxAfterAssigning(
            geometry.Box.Id,
            preview.ItemKeys);
        var beforeKey = ResolveInsertBeforeKey(geometry, preview.Pointer, previewItemKeys);
        if (beforeKey is not null)
        {
            projectedItems = InsertProjectedItemsBefore(
                projectedItems,
                previewItemKeys,
                beforeKey);
        }
        var visibleItems = GetVisibleDesktopDropPreviewItems(
            geometry,
            preview,
            projectedItems,
            previewItemKeys);
        if (visibleItems.Count == 0)
        {
            return;
        }

        var appearance = _runtime.State.Settings.Appearance;
        var layout = DesktopItemLayoutEngine.CalculateVisible(
            geometry.Box.ViewMode,
            new LayoutRect(geometry.Body.X, geometry.Body.Y, geometry.Body.Width, geometry.Body.Height),
            visibleItems.Count,
            geometry.Box.Appearance.IconSize,
            DesktopItemLayoutEngine.ScaleIconSpacing(appearance.IconHorizontalSpacing, geometry.Box.Appearance.IconSize),
            DesktopItemLayoutEngine.ScaleIconSpacing(appearance.IconVerticalSpacing, geometry.Box.Appearance.IconSize),
            _scrollOffsets.GetValueOrDefault(GetItemViewKey(geometry)));
        foreach (var entry in layout.Items)
        {
            var item = visibleItems[entry.Index];
            if (!previewItemKeys.Contains(item.Key.ToString()))
            {
                continue;
            }

            var itemBounds = entry.Bounds;
            var iconBounds = GetItemIconBounds(new ItemGeometry(
                geometry.Box,
                item,
                new RectangleF(
                    (float)itemBounds.X,
                    (float)itemBounds.Y,
                    (float)itemBounds.Width,
                    (float)itemBounds.Height)));
            DrawDropPreviewFrame(graphics, iconBounds, accent);
        }
    }

    private static void DrawDropPreviewFrame(Graphics graphics, RectangleF iconBounds, Color accent)
    {
        var previewBounds = RectangleF.Inflate(iconBounds, 4, 4);
        using var fill = new SolidBrush(Color.FromArgb(78, accent));
        using var border = new Pen(Color.FromArgb(238, accent), 2f);
        using var path = RoundedRectangle(previewBounds, 5);
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);
    }

    private static IReadOnlyList<DesktopItemRef> InsertProjectedItemsBefore(
        IReadOnlyList<DesktopItemRef> projectedItems,
        IReadOnlySet<string> incomingKeys,
        string targetItemKey)
    {
        var incoming = projectedItems
            .Where(item => incomingKeys.Contains(item.Key.ToString()))
            .ToArray();
        if (incoming.Length == 0)
        {
            return projectedItems;
        }

        var remaining = projectedItems
            .Where(item => !incomingKeys.Contains(item.Key.ToString()))
            .ToList();
        var targetIndex = remaining.FindIndex(item =>
            string.Equals(item.Key.ToString(), targetItemKey, StringComparison.OrdinalIgnoreCase));
        if (targetIndex < 0)
        {
            remaining.AddRange(incoming);
        }
        else
        {
            remaining.InsertRange(targetIndex, incoming);
        }
        return remaining;
    }

    private static IReadOnlyList<DesktopItemRef> GetVisibleDesktopDropPreviewItems(
        BoxGeometry geometry,
        DropPreviewState preview,
        IReadOnlyList<DesktopItemRef> projectedItems,
        IReadOnlySet<string> previewItemKeys)
    {
        if (geometry.ManualTabs.Count == 0 || geometry.ActiveManualTabId is not { } activeTabId)
        {
            return projectedItems;
        }

        var targetTabId = preview.TargetManualTabIndex is { } tabIndex &&
                          tabIndex >= 0 && tabIndex < geometry.ManualTabs.Count
            ? geometry.ManualTabs[tabIndex].Id
            : null;
        return projectedItems.Where(item =>
        {
            var itemKey = item.Key.ToString();
            if (previewItemKeys.Contains(itemKey))
            {
                return targetTabId == activeTabId;
            }

            return geometry.Box.ItemTabAssignments.TryGetValue(itemKey, out var itemTabId) &&
                   itemTabId == activeTabId;
        }).ToArray();
    }

    private DragImage? CreateDragImage(
        IReadOnlyList<DesktopItemRef> selected,
        DesktopItemRef? pressedItem)
    {
        if (selected.Count == 0)
        {
            return null;
        }

        var sourceBox = _pressedBoxId is { } boxId
            ? _runtime.State.Boxes.FirstOrDefault(box => box.Id == boxId)
            : null;
        var iconSize = Math.Clamp(
            (int)Math.Round((sourceBox?.Appearance.IconSize ?? 40) * _scale),
            24,
            64);
        const int padding = 8;
        var stackCount = Math.Min(3, selected.Count);
        var offset = (stackCount - 1) * 4;
        var badgeDiameter = selected.Count > 1 ? 20 : 0;
        var width = iconSize + offset + padding * 2 + badgeDiameter / 2;
        var height = iconSize + offset + padding * 2;
        var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.Clear(Color.Transparent);
            var accent = sourceBox is null
                ? ParseOpaqueColor(_runtime.State.Settings.Appearance.SelectionColor)
                : ParseOpaqueColor(sourceBox.Appearance.Accent);
            for (var index = stackCount - 1; index >= 0; index--)
            {
                var tileOffset = index * 4;
                var tile = new RectangleF(
                    padding + tileOffset - 2,
                    padding + tileOffset - 2,
                    iconSize + 4,
                    iconSize + 4);
                using var tileFill = new SolidBrush(Color.FromArgb(32 + index * 8, accent));
                using var tileBorder = new Pen(Color.FromArgb(150, accent), 1);
                using var tilePath = RoundedRectangle(tile, 5);
                graphics.FillPath(tileFill, tilePath);
                graphics.DrawPath(tileBorder, tilePath);
            }

            var primary = pressedItem is null
                ? selected[0]
                : selected.FirstOrDefault(item => item.Key == pressedItem.Key) ?? selected[0];
            var icon = GetIconBitmap(primary, (float)(sourceBox?.Appearance.IconSize ?? 40)) ??
                       ShellIconProvider.GetGenericFileIcon();
            if (icon is not null)
            {
                graphics.DrawImage(icon, new Rectangle(padding, padding, iconSize, iconSize));
            }

            if (selected.Count > 1)
            {
                var badge = new RectangleF(width - badgeDiameter - 2, height - badgeDiameter - 2, badgeDiameter, badgeDiameter);
                using var badgeFill = new SolidBrush(Color.FromArgb(245, accent));
                using var badgePath = RoundedRectangle(badge, badgeDiameter / 2f);
                using var badgeText = new SolidBrush(Color.White);
                using var badgeFont = new Font("Segoe UI", 8, FontStyle.Bold, GraphicsUnit.Point);
                using var format = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center
                };
                graphics.FillPath(badgeFill, badgePath);
                graphics.DrawString(selected.Count.ToString(), badgeFont, badgeText, badge, format);
            }

            var sourceGeometry = pressedItem is null
                ? null
                : _items.LastOrDefault(item => item.Item.Key == pressedItem.Key);
            var sourceIconBounds = sourceGeometry is null
                ? RectangleF.Empty
                : GetItemIconBounds(sourceGeometry);
            var relativeCursorX = sourceIconBounds.IsEmpty
                ? 0.5f
                : Math.Clamp((_pressPoint.X - sourceIconBounds.X) / sourceIconBounds.Width, 0f, 1f);
            var relativeCursorY = sourceIconBounds.IsEmpty
                ? 0.5f
                : Math.Clamp((_pressPoint.Y - sourceIconBounds.Y) / sourceIconBounds.Height, 0f, 1f);
            var cursorOffset = sourceIconBounds.IsEmpty
                ? new Point(padding + iconSize / 2, padding + iconSize / 2)
                : new Point(
                    padding + (int)Math.Round(relativeCursorX * Math.Max(0, iconSize - 1)),
                    padding + (int)Math.Round(relativeCursorY * Math.Max(0, iconSize - 1)));
            cursorOffset = new Point(
                Math.Clamp(cursorOffset.X, 0, width - 1),
                Math.Clamp(cursorOffset.Y, 0, height - 1));
            return new DragImage(bitmap, cursorOffset);
        }
        catch
        {
            bitmap.Dispose();
            return null;
        }
    }

    private void DrawBox(
        Graphics graphics,
        BoxGeometry geometry,
        RectangleF clipBounds,
        bool includeDropPreview = true,
        IReadOnlySet<string>? selectedItemKeys = null,
        bool includeSelectionRectangle = true,
        IReadOnlySet<string>? suppressedHoverItemKeys = null,
        bool includeItemHoverFeedback = true)
    {
        var baseColor = ParseOpaqueColor(geometry.Box.Appearance.Background);
        var opacity = Math.Clamp(geometry.Box.Appearance.Opacity, 0.35, 1);
        var boxColor = ApplyOpacity(baseColor, opacity);
        var textColor = ResolveAutoTextColor(baseColor);
        var isDarkSurface = UsesLightText(baseColor);
        var paintedBounds = RectangleF.Inflate(geometry.Bounds, -0.5f, -0.5f);
        using var path = RoundedRectangle(
            paintedBounds,
            (float)_runtime.State.Settings.Appearance.CornerRadius);
        using var fill = new SolidBrush(boxColor);
        graphics.FillPath(fill, path);

        using var accentPath = RoundedRectangle(
            new RectangleF(geometry.Header.X + 8, geometry.Header.Y + 9, 4, geometry.Header.Height - 18),
            2);
        using var accent = new SolidBrush(ParseOpaqueColor(geometry.Box.Appearance.Accent));
        graphics.FillPath(accent, accentPath);
        using var titleFont = CreateFont(
            geometry.Box.Appearance.TitleFontFamily,
            (float)geometry.Box.Appearance.TitleFontSize,
            geometry.Box.Appearance.TitleFontBold ? FontStyle.Bold : FontStyle.Regular,
            GraphicsUnit.Point);
        using var titleBrush = new SolidBrush(ResolveTitleColor(geometry.Box.Appearance.TitleColor, baseColor));
        using var titleFormat = new StringFormat
        {
            Alignment = geometry.Box.Appearance.TitleAlignment == BoxTitleAlignment.Center
                ? StringAlignment.Center
                : StringAlignment.Near,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };
        var titleRightPadding = GetTitleRightPadding(geometry.Box);
        if (_editingBox?.Id != geometry.Box.Id)
        {
            graphics.DrawString(geometry.Box.Title, titleFont, titleBrush,
                new RectangleF(geometry.Header.X + 20, geometry.Header.Y, geometry.Header.Width - titleRightPadding, geometry.Header.Height), titleFormat);
        }
        DrawAutoExpandButton(
            graphics,
            geometry.AutoExpand,
            geometry.Box.ExpandOnHover,
            _hoveredAutoExpandBoxId == geometry.Box.Id,
            ParseOpaqueColor(geometry.Box.Appearance.Accent),
            textColor,
            isDarkSurface);
        DrawMenuIcon(graphics, geometry.Menu, textColor);
        DrawBoxTabs(
            graphics,
            geometry,
            ParseOpaqueColor(geometry.Box.Appearance.Accent),
            textColor,
            isDarkSurface);

        if (geometry.IsCollapsed)
        {
            if (includeDropPreview)
            {
                DrawDropTargetFeedback(graphics, geometry, clipBounds);
            }
            return;
        }

        var state = graphics.Save();
        graphics.SetClip(geometry.Body);
        using var itemFont = geometry.Box.Appearance.ShowItemLabels
            ? CreateFont(
                geometry.Box.Appearance.LabelFontFamily,
                (float)geometry.Box.Appearance.LabelFontSize,
                FontStyle.Regular,
                GraphicsUnit.Point)
            : null;
        using var itemBrush = geometry.Box.Appearance.ShowItemLabels
            ? new SolidBrush(textColor)
            : null;
        using var itemFormat = geometry.Box.Appearance.ShowItemLabels
            ? CreateItemTextFormat(geometry.Box.ViewMode)
            : null;
        using var selectedGridItemFormat = geometry.Box.Appearance.ShowItemLabels &&
                                           geometry.Box.ViewMode == BoxViewMode.Grid
            ? CreateSelectedGridItemTextFormat()
            : null;
        var effectiveSelection = selectedItemKeys ?? _selection;
        var visibleItems = GetRenderedItemsForBox(geometry, clipBounds, includeDropPreview)
            // Raised labels are drawn last so the complete selected filename
            // remains readable when it overlaps a neighbouring item.
            .OrderBy(item => IsRaisedVisual(
                item,
                effectiveSelection,
                suppressedHoverItemKeys,
                includeItemHoverFeedback))
            .ToArray();
        foreach (var item in visibleItems)
        {
            DrawItem(
                graphics,
                item,
                itemFont,
                itemBrush,
                itemFormat,
                selectedGridItemFormat,
                geometry.Body,
                effectiveSelection,
                suppressedHoverItemKeys,
                includeItemHoverFeedback);
        }
        if (includeDropPreview)
        {
            DrawDropInsertionFeedback(graphics, geometry, clipBounds);
        }
        if (!_runtime.AreDesktopItemsHidden && geometry.Box.IsMappedFolder &&
            !_items.Any(item => item.Box.Id == geometry.Box.Id))
        {
            DrawMappedFolderState(graphics, geometry, textColor);
        }
        if (includeSelectionRectangle &&
            _selectionBox?.Id == geometry.Box.Id &&
            !_selectionRectangle.IsEmpty)
        {
            var selectionColor = ParseOpaqueColor(_runtime.State.Settings.Appearance.SelectionColor);
            using var selectionFill = new SolidBrush(Color.FromArgb(42, selectionColor));
            using var selectionBorder = new Pen(Color.FromArgb(190, selectionColor), 1)
            {
                DashStyle = DashStyle.Dash
            };
            graphics.FillRectangle(selectionFill, _selectionRectangle);
            graphics.DrawRectangle(
                selectionBorder,
                _selectionRectangle.X,
                _selectionRectangle.Y,
                _selectionRectangle.Width,
                _selectionRectangle.Height);
        }
        graphics.Restore(state);

        if (includeDropPreview)
        {
            DrawDropTargetFeedback(graphics, geometry, clipBounds);
        }

        if (_runtime.State.Settings.Appearance.ShowResizeGrip)
        {
            using var grip = new Pen(Color.FromArgb(130, textColor), 1);
            graphics.DrawLine(grip, geometry.Resize.Right - 10, geometry.Resize.Bottom - 3, geometry.Resize.Right - 3, geometry.Resize.Bottom - 10);
            graphics.DrawLine(grip, geometry.Resize.Right - 6, geometry.Resize.Bottom - 3, geometry.Resize.Right - 3, geometry.Resize.Bottom - 6);
        }
    }

    private void DrawMarqueeSelectionOverlay(
        Graphics graphics,
        BoxGeometry geometry,
        RectangleF clipBounds)
    {
        if (_selectionRectangle.IsEmpty && _marqueeSelectionItems.Count == 0)
        {
            return;
        }

        var state = graphics.Save();
        graphics.SetClip(geometry.Body, CombineMode.Intersect);
        var textColor = ResolveAutoTextColor(ParseOpaqueColor(geometry.Box.Appearance.Background));
        using var itemFont = geometry.Box.Appearance.ShowItemLabels
            ? CreateFont(
                geometry.Box.Appearance.LabelFontFamily,
                (float)geometry.Box.Appearance.LabelFontSize,
                FontStyle.Regular,
                GraphicsUnit.Point)
            : null;
        using var itemBrush = geometry.Box.Appearance.ShowItemLabels
            ? new SolidBrush(textColor)
            : null;
        using var itemFormat = geometry.Box.Appearance.ShowItemLabels
            ? CreateItemTextFormat(geometry.Box.ViewMode)
            : null;
        using var selectedGridItemFormat = geometry.Box.Appearance.ShowItemLabels &&
                                           geometry.Box.ViewMode == BoxViewMode.Grid
            ? CreateSelectedGridItemTextFormat()
            : null;

        foreach (var item in _marqueeSelectionItems
                     .Where(item => item.Box.Id == geometry.Box.Id &&
                                    GetMarqueeItemOverlayBounds(item, geometry.Body).IntersectsWith(clipBounds))
                     .OrderBy(item => IsRaisedVisual(item, _selection, includeItemHoverFeedback: false)))
        {
            DrawItem(
                graphics,
                item,
                itemFont,
                itemBrush,
                itemFormat,
                selectedGridItemFormat,
                geometry.Body,
                _selection,
                includeItemHoverFeedback: false);
        }

        if (!_selectionRectangle.IsEmpty)
        {
            var selectionColor = ParseOpaqueColor(_runtime.State.Settings.Appearance.SelectionColor);
            using var selectionFill = new SolidBrush(Color.FromArgb(42, selectionColor));
            using var selectionBorder = new Pen(Color.FromArgb(190, selectionColor), 1)
            {
                DashStyle = DashStyle.Dash
            };
            graphics.FillRectangle(selectionFill, _selectionRectangle);
            graphics.DrawRectangle(
                selectionBorder,
                _selectionRectangle.X,
                _selectionRectangle.Y,
                _selectionRectangle.Width,
                _selectionRectangle.Height);
        }

        graphics.Restore(state);
    }

    private RectangleF? GetMarqueeSelectionOverlayBounds(BoxGeometry geometry)
    {
        RectangleF? bounds = null;
        if (!_selectionRectangle.IsEmpty)
        {
            bounds = RectangleF.Inflate(_selectionRectangle, 4, 4);
        }

        foreach (var item in _marqueeSelectionItems)
        {
            if (item.Box.Id != geometry.Box.Id)
            {
                continue;
            }

            var itemBounds = GetMarqueeItemOverlayBounds(item, geometry.Body);
            bounds = bounds is { } existing
                ? RectangleF.Union(existing, itemBounds)
                : itemBounds;
        }

        return bounds;
    }

    private static RectangleF GetMarqueeItemOverlayBounds(
        ItemGeometry item,
        RectangleF contentBounds)
    {
        var bounds = RectangleF.Inflate(item.Bounds, 4, 4);
        if (item.Box.ViewMode != BoxViewMode.Grid || !item.Box.Appearance.ShowItemLabels)
        {
            return bounds;
        }

        // A selected grid label can grow to the bottom of the box. Include
        // that possible visual extent without measuring text on every move.
        var labelBounds = new RectangleF(
            item.Bounds.X - 4,
            item.Bounds.Y - 4,
            item.Bounds.Width + 8,
            Math.Max(item.Bounds.Height + 8, contentBounds.Bottom - item.Bounds.Y + 4));
        return RectangleF.Union(bounds, labelBounds);
    }

    private IReadOnlyList<ItemGeometry> GetRenderedItemsForBox(
        BoxGeometry geometry,
        RectangleF clipBounds,
        bool includeDropPreview = true)
    {
        if (_runtime.AreDesktopItemsHidden)
        {
            return [];
        }

        // During a box marquee the geometry built for hit testing already is
        // the visible layout. Reusing it avoids recalculating the layout,
        // tab projection and scroll state for every overlay frame.
        if (!includeDropPreview && !_dragStarted)
        {
            return _items
                .Where(item => item.Box.Id == geometry.Box.Id &&
                               item.Bounds.IntersectsWith(clipBounds))
                .ToArray();
        }

        var preview = includeDropPreview ? _dropPreview : null;
        IReadOnlyList<DesktopItemRef>? projectedItems = null;
        var hiddenKeySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_dragStarted && _pressedBoxId == geometry.Box.Id)
        {
            hiddenKeySet.UnionWith(
                GetCachedItemsForBox(geometry.Box.Id)
                    .Where(item => _selection.Contains(item.Key.ToString()))
                    .Select(item => item.Key.ToString()));
        }
        IReadOnlySet<string> hiddenKeys = hiddenKeySet;
        IReadOnlySet<string>? visibleKeyFilter = null;

        if (preview is { BoxId: var previewBoxId, AcceptsDrop: true } &&
            previewBoxId == geometry.Box.Id && preview.ItemKeys.Count > 0)
        {
            var previewKeys = preview.ItemKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (preview.Kind == DropPreviewKind.Reorder)
            {
                var currentItems = GetCachedItemsForBox(geometry.Box.Id);
                var currentKeys = currentItems.Select(item => item.Key.ToString()).ToArray();
                var beforeKey = GetReorderBeforeKey(geometry, preview.Pointer);
                var projectedKeys = LayoutCoordinator.ProjectReorderedKeys(
                    geometry.Box,
                    currentKeys,
                    previewKeys,
                    beforeKey);
                var itemsByKey = currentItems.ToDictionary(
                    item => item.Key.ToString(),
                    StringComparer.OrdinalIgnoreCase);
                projectedItems = projectedKeys
                    .Where(itemsByKey.ContainsKey)
                    .Select(key => itemsByKey[key])
                    .ToArray();
                hiddenKeySet.UnionWith(previewKeys);
                visibleKeyFilter = GetVisibleItemsForBox(geometry)
                    .Select(item => item.Key.ToString())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            else if (preview.Kind == DropPreviewKind.DesktopAssign)
            {
                projectedItems = GetProjectedDesktopAssignmentItems(geometry, preview);
                hiddenKeySet.UnionWith(previewKeys);
            }
        }

        var layoutItems = projectedItems is null
            ? GetVisibleItemsForBox(geometry)
            : visibleKeyFilter is null
                ? GetVisibleDesktopDropPreviewItems(
                    geometry,
                    preview!,
                    projectedItems,
                    hiddenKeys)
                : projectedItems.Where(item => visibleKeyFilter.Contains(item.Key.ToString())).ToArray();

        var appearance = _runtime.State.Settings.Appearance;
        var layout = DesktopItemLayoutEngine.CalculateVisible(
            geometry.Box.ViewMode,
            new LayoutRect(geometry.Body.X, geometry.Body.Y, geometry.Body.Width, geometry.Body.Height),
            layoutItems.Count,
            geometry.Box.Appearance.IconSize,
            DesktopItemLayoutEngine.ScaleIconSpacing(appearance.IconHorizontalSpacing, geometry.Box.Appearance.IconSize),
            DesktopItemLayoutEngine.ScaleIconSpacing(appearance.IconVerticalSpacing, geometry.Box.Appearance.IconSize),
            _scrollOffsets.GetValueOrDefault(GetItemViewKey(geometry)));
        return layout.Items
            .Select(entry =>
            {
                var bounds = entry.Bounds;
                return new ItemGeometry(
                    geometry.Box,
                    layoutItems[entry.Index],
                    new RectangleF(
                        (float)bounds.X,
                        (float)bounds.Y,
                        (float)bounds.Width,
                        (float)bounds.Height));
            })
            .Where(item => !hiddenKeys.Contains(item.Item.Key.ToString()))
            .Where(item => item.Bounds.IntersectsWith(clipBounds))
            .ToArray();
    }

    // Resolves the insertion point for an incoming group: the upper half of
    // an item inserts before it, the lower half inserts after it (and after
    // the last item means append at the end). Explorer-style label-edit
    // placement, so dropping "onto the last item" lands at the end.
    private string? ResolveInsertBeforeKey(
        BoxGeometry geometry,
        PointF pointer,
        IReadOnlySet<string>? incomingKeys = null)
    {
        var targetItem = _items.LastOrDefault(item =>
                item.Box.Id == geometry.Box.Id &&
                item.Bounds.Contains(pointer) &&
                (incomingKeys is null || !incomingKeys.Contains(item.Item.Key.ToString())))
            ?.Item;
        if (targetItem is null)
        {
            return null;
        }

        var bounds = _items.First(candidate =>
            string.Equals(candidate.Item.Key.ToString(), targetItem.Key.ToString(), StringComparison.OrdinalIgnoreCase)).Bounds;
        var insertAfter = pointer.Y >= bounds.Y + bounds.Height / 2;
        if (!insertAfter)
        {
            return targetItem.Key.ToString();
        }

        var orderedKeys = GetCachedItemsForBox(geometry.Box.Id)
            .Select(item => item.Key.ToString())
            .ToArray();
        var index = Array.FindIndex(
            orderedKeys,
            key => string.Equals(key, targetItem.Key.ToString(), StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < orderedKeys.Length
            ? orderedKeys[index + 1]
            : null;
    }

    private string? GetReorderBeforeKey(
        BoxGeometry geometry,
        PointF pointer) =>
        ResolveInsertBeforeKey(geometry, pointer);

    private IReadOnlyList<DesktopItemRef> GetProjectedDesktopAssignmentItems(
        BoxGeometry geometry,
        DropPreviewState preview)
    {
        var incomingKeys = preview.ItemKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var projectedItems = _runtime.GetItemsForBoxAfterAssigning(
            geometry.Box.Id,
            preview.ItemKeys);
        var beforeKey = ResolveInsertBeforeKey(geometry, preview.Pointer, incomingKeys);
        return beforeKey is null
            ? projectedItems
            : InsertProjectedItemsBefore(projectedItems, incomingKeys, beforeKey);
    }


    private static void DrawBoxTabs(
        Graphics graphics,
        BoxGeometry geometry,
        Color accent,
        Color textColor,
        bool isDarkSurface)
    {
        var tabCount = geometry.ManualTabs.Count > 0
            ? geometry.ManualTabs.Count
            : geometry.CategoryTabs.Count;
        if (tabCount == 0 || geometry.TabBar.IsEmpty)
        {
            return;
        }

        using var divider = new Pen(Color.FromArgb(isDarkSurface ? 64 : 54, textColor), 1);
        graphics.DrawLine(divider, geometry.TabBar.Left, geometry.TabBar.Bottom - 1, geometry.TabBar.Right, geometry.TabBar.Bottom - 1);
        var tabFontSize = Math.Clamp(
            (float)geometry.Box.Appearance.LabelFontSize + 0.5f,
            9.5f,
            11f);
        using var activeFont = CreateFont(
            geometry.Box.Appearance.LabelFontFamily,
            tabFontSize,
            FontStyle.Bold,
            GraphicsUnit.Point);
        using var inactiveFont = CreateFont(
            geometry.Box.Appearance.LabelFontFamily,
            tabFontSize,
            FontStyle.Regular,
            GraphicsUnit.Point);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };
        for (var index = 0; index < tabCount; index++)
        {
            var (label, active) = geometry.ManualTabs.Count > 0
                ? (
                    geometry.ManualTabs[index].Label,
                    geometry.ManualTabs[index].Id == geometry.ActiveManualTabId)
                : (
                    geometry.CategoryTabs[index].Label,
                    geometry.CategoryTabs[index].Category == geometry.ActiveMappedFolderCategory);
            var bounds = GetBoxTabBounds(geometry, index, tabCount);
            using var labelBrush = new SolidBrush(active
                ? accent
                : Color.FromArgb(isDarkSurface ? 196 : 184, textColor));
            graphics.DrawString(
                label,
                active ? activeFont : inactiveFont,
                labelBrush,
                RectangleF.Inflate(bounds, -2, 0),
                format);
            if (active)
            {
                using var underline = new Pen(accent, 2);
                graphics.DrawLine(
                    underline,
                    bounds.Left + 7,
                    geometry.TabBar.Bottom - 1,
                    bounds.Right - 7,
                    geometry.TabBar.Bottom - 1);
            }
        }
    }

    private void DrawMappedFolderState(Graphics graphics, BoxGeometry geometry, Color textColor)
    {
        var snapshot = _runtime.GetMappedFolderSnapshot(geometry.Box.Id);
        var message = snapshot?.Availability switch
        {
            MappedFolderAvailability.Available => "此文件夹为空",
            MappedFolderAvailability.Missing => "文件夹不存在",
            MappedFolderAvailability.Offline => "磁盘或网络位置不可用",
            MappedFolderAvailability.AccessDenied => "没有访问此文件夹的权限",
            MappedFolderAvailability.Error => snapshot.Message ?? "无法读取此文件夹",
            _ => "正在读取文件夹"
        };
        using var font = CreateFont(
            geometry.Box.Appearance.LabelFontFamily,
            Math.Clamp((float)geometry.Box.Appearance.LabelFontSize + 0.5f, 9.5f, 12f),
            FontStyle.Regular,
            GraphicsUnit.Point);
        using var brush = new SolidBrush(Color.FromArgb(210, textColor));
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter
        };
        graphics.DrawString(message, font, brush, geometry.Body, format);
    }

    private void DrawItem(
        Graphics graphics,
        ItemGeometry item,
        Font? labelFont,
        Brush? labelBrush,
        StringFormat? labelFormat,
        StringFormat? selectedGridItemFormat,
        RectangleF contentBounds,
        IReadOnlySet<string>? selectedItemKeys = null,
        IReadOnlySet<string>? suppressedHoverItemKeys = null,
        bool includeItemHoverFeedback = true)
    {
        var itemKey = item.Item.Key.ToString();
        var isSelected = (selectedItemKeys ?? _selection).Contains(itemKey);
        var isHovered =
            includeItemHoverFeedback &&
            _runtime.State.Settings.Appearance.HoverFeedback &&
            !(suppressedHoverItemKeys?.Contains(itemKey) ?? false) &&
            string.Equals(_hoveredItemKey, itemKey, StringComparison.OrdinalIgnoreCase);
        var iconSize = (float)item.Box.Appearance.IconSize;
        var iconBounds = GetItemIconBounds(item);
        var showsFullLabel = DesktopIconLabelDisplayPolicy.ShowsFullLabel(isSelected, isHovered);
        var textBounds = item.Box.Appearance.ShowItemLabels
            ? GetItemTextBounds(graphics, item, iconBounds, labelFont!, showsFullLabel, contentBounds)
            : RectangleF.Empty;
        var visualBounds = textBounds.IsEmpty
            ? item.Bounds
            : RectangleF.Union(item.Bounds, textBounds);
        var visualKey = (item.Box.Id, itemKey);
        if (isSelected || isHovered)
        {
            _expandedItemHitBounds[visualKey] = RectangleF.Intersect(visualBounds, contentBounds);
        }
        else
        {
            _expandedItemHitBounds.Remove(visualKey);
        }
        if (isHovered)
        {
            // Hover remains visible while the item is selected and is kept
            // brighter than the settled selection treatment.
            var configuredSelection = ParseOpaqueColor(_runtime.State.Settings.Appearance.SelectionColor);
            var hoverColor = BrightenColor(configuredSelection, HoverBrightness);
            using var hovered = new SolidBrush(Color.FromArgb(BoxHoverFillAlpha, hoverColor));
            using var hoverBorder = new Pen(Color.FromArgb(BoxHoverBorderAlpha, hoverColor), 1);
            using var hoveredPath = RoundedRectangle(RectangleF.Inflate(visualBounds, -2, -2), 4);
            graphics.FillPath(hovered, hoveredPath);
            graphics.DrawPath(hoverBorder, hoveredPath);
        }
        else if (isSelected)
        {
            var configuredSelection = ParseOpaqueColor(_runtime.State.Settings.Appearance.SelectionColor);
            using var selected = new SolidBrush(Color.FromArgb(SelectedItemFillAlpha, configuredSelection));
            using var selectedPath = RoundedRectangle(RectangleF.Inflate(visualBounds, -2, -2), 4);
            graphics.FillPath(selected, selectedPath);
        }

        var bitmap = GetIconBitmap(item.Item, iconSize) ?? ShellIconProvider.GetGenericFileIcon();
        if (bitmap is not null)
        {
            graphics.DrawImage(bitmap, iconBounds);
        }
        if (!item.Box.Appearance.ShowItemLabels)
        {
            return;
        }
        graphics.DrawString(
            item.Item.DisplayName,
            labelFont!,
            labelBrush!,
            textBounds,
            showsFullLabel && item.Box.ViewMode == BoxViewMode.Grid
                ? selectedGridItemFormat!
                : labelFormat!);
    }

    private bool IsRaisedVisual(
        ItemGeometry item,
        IReadOnlySet<string>? selectedItemKeys = null,
        IReadOnlySet<string>? suppressedHoverItemKeys = null,
        bool includeItemHoverFeedback = true)
    {
        var itemKey = item.Item.Key.ToString();
        return (selectedItemKeys ?? _selection).Contains(itemKey) ||
            (includeItemHoverFeedback &&
             _runtime.State.Settings.Appearance.HoverFeedback &&
             !(suppressedHoverItemKeys?.Contains(itemKey) ?? false) &&
             string.Equals(_hoveredItemKey, itemKey, StringComparison.OrdinalIgnoreCase));
    }

    private static RectangleF GetItemIconBounds(ItemGeometry item)
    {
        var iconSize = (float)item.Box.Appearance.IconSize;
        return item.Box.ViewMode == BoxViewMode.List
            ? new RectangleF(
                item.Bounds.X + 8,
                item.Bounds.Y + (item.Bounds.Height - iconSize) / 2,
                iconSize,
                iconSize)
            : new RectangleF(
                item.Bounds.X + (item.Bounds.Width - iconSize) / 2,
                item.Bounds.Y + 5,
                iconSize,
                iconSize);
    }

    private static RectangleF GetItemTextBounds(
        Graphics graphics,
        ItemGeometry item,
        RectangleF iconBounds,
        Font labelFont,
        bool isSelected,
        RectangleF contentBounds)
    {
        if (item.Box.ViewMode == BoxViewMode.List)
        {
            return new RectangleF(
                iconBounds.Right + 10,
                item.Bounds.Y,
                Math.Max(0, item.Bounds.Right - iconBounds.Right - 18),
                item.Bounds.Height);
        }

        var textTop = iconBounds.Bottom + 3;
        var textWidth = Math.Max(0, item.Bounds.Width - 4);
        var compactHeight = Math.Max(
            0,
            Math.Min(
                item.Bounds.Bottom - textTop - 3,
                labelFont.GetHeight(graphics) * CompactGridLabelLineCount + 2));
        var visibleHeight = Math.Max(0, contentBounds.Bottom - textTop - 3);
        var textHeight = isSelected
            ? Math.Min(visibleHeight, MeasureFullGridLabelHeight(graphics, item.Item.DisplayName, labelFont, textWidth))
            : compactHeight;
        return new RectangleF(
            item.Bounds.X + 2,
            textTop,
            textWidth,
            textHeight);
    }

    private static float MeasureFullGridLabelHeight(
        Graphics graphics,
        string displayName,
        Font labelFont,
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
        // Measure the whole filename first; the caller then caps it at the
        // visible bottom edge of the box rather than at an arbitrary line count.
        return graphics.MeasureString(displayName, labelFont, new SizeF(width, 100_000), format).Height + 2;
    }

    private static StringFormat CreateItemTextFormat(BoxViewMode viewMode) => new()
    {
        Alignment = viewMode == BoxViewMode.List ? StringAlignment.Near : StringAlignment.Center,
        LineAlignment = viewMode == BoxViewMode.List ? StringAlignment.Center : StringAlignment.Near,
        Trimming = StringTrimming.EllipsisCharacter,
        // An idle grid item gets exactly two complete label lines. Any
        // remaining filename text is represented by the standard ellipsis.
        FormatFlags = viewMode == BoxViewMode.List
            ? StringFormatFlags.NoWrap
            : StringFormatFlags.LineLimit
    };

    private static StringFormat CreateSelectedGridItemTextFormat() => new()
    {
        Alignment = StringAlignment.Center,
        LineAlignment = StringAlignment.Near,
        Trimming = StringTrimming.EllipsisCharacter,
        // Selection removes the fixed line limit. The text rectangle grows to
        // the box's visible bottom edge; an ellipsis is only needed if even
        // that available area cannot contain the complete filename.
        FormatFlags = StringFormatFlags.LineLimit
    };

    private Bitmap? GetIconBitmap(DesktopItemRef item, float iconSize)
    {
        var key = CreateIconBitmapKey(item, iconSize);
        if (_iconCache.TryGetValue(key, out var bitmap))
        {
            return bitmap;
        }
        // A zoom notch changes the requested pixel size. Keep rendering from
        // the nearest cached size while the new size loads asynchronously;
        // DrawImage scales the bitmap to the current icon bounds.
        var nearest = _iconCache
            .Where(pair =>
                pair.Key.ParsingName == key.ParsingName &&
                pair.Key.ModifiedTicks == key.ModifiedTicks &&
                pair.Key.Length == key.Length)
            .OrderBy(pair => Math.Abs(pair.Key.PixelSize - key.PixelSize))
            .Select(pair => pair.Value)
            .FirstOrDefault();
        if (nearest is not null)
        {
            if (_pendingIconLoads.Add(key))
            {
                _ = LoadIconBitmapAsync(key, _iconCacheVersion);
            }
            return nearest;
        }
        if (_iconLoadRetries.TryGetValue(key, out var retry) &&
            DateTimeOffset.UtcNow < retry.RetryAfter)
        {
            return null;
        }
        if (_pendingIconLoads.Add(key))
        {
            _ = LoadIconBitmapAsync(key, _iconCacheVersion);
        }
        return null;
    }

    private async Task LoadIconBitmapAsync(IconBitmapKey key, int cacheVersion)
    {
        Bitmap? bitmap = null;
        var token = _iconLoadCancellation.Token;
        try
        {
            await _iconLoadGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var source = _runtime.IconProvider.GetIcon(key.ParsingName, key.PixelSize);
                if (source is not null)
                {
                    bitmap = new Bitmap(source);
                }
                else if (!_iconLoadRetries.ContainsKey(key))
                {
                    DiagnosticLog.Info(
                        $"Icon load returned no image parsingName={key.ParsingName} pixelSize={key.PixelSize}");
                }
            }
            finally
            {
                _iconLoadGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            bitmap?.Dispose();
            return;
        }
        catch
        {
            bitmap?.Dispose();
            bitmap = null;
        }

        if (token.IsCancellationRequested || IsDisposed || !IsHandleCreated)
        {
            bitmap?.Dispose();
            return;
        }

        try
        {
            BeginInvoke((Action)(() =>
            {
                _pendingIconLoads.Remove(key);
                if (IsDisposed || cacheVersion != _iconCacheVersion)
                {
                    bitmap?.Dispose();
                    return;
                }
                if (_iconCache.ContainsKey(key))
                {
                    bitmap?.Dispose();
                    return;
                }
                if (bitmap is null)
                {
                    ScheduleIconLoadRetry(key);
                    return;
                }
                _iconLoadRetries.Remove(key);
                _iconCache[key] = bitmap;
                InvalidateIcon(key);
            }));
        }
        catch (InvalidOperationException)
        {
            bitmap?.Dispose();
        }
    }

    private void ScheduleIconLoadRetry(IconBitmapKey key)
    {
        var attempt = _iconLoadRetries.GetValueOrDefault(key).Attempt + 1;
        var delay = TimeSpan.FromMilliseconds(Math.Min(30000, 500 * Math.Pow(2, Math.Min(attempt - 1, 6))));
        _iconLoadRetries[key] = new IconLoadRetry(attempt, DateTimeOffset.UtcNow + delay);
        _ = RetryIconLoadAsync(key, delay, _iconCacheVersion);
    }

    private async Task RetryIconLoadAsync(IconBitmapKey key, TimeSpan delay, int cacheVersion)
    {
        try
        {
            await Task.Delay(delay, _iconLoadCancellation.Token).ConfigureAwait(false);
            if (_iconLoadCancellation.IsCancellationRequested || IsDisposed || !IsHandleCreated)
            {
                return;
            }
            BeginInvoke((Action)(() =>
            {
                if (!IsDisposed && cacheVersion == _iconCacheVersion && !_iconCache.ContainsKey(key))
                {
                    if (_iconLoadRetries.TryGetValue(key, out var retry))
                    {
                        _iconLoadRetries[key] = retry with { RetryAfter = DateTimeOffset.MinValue };
                    }
                    InvalidateIcon(key);
                }
            }));
        }
        catch (OperationCanceledException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void PruneIconCache()
    {
        var activeKeys = DesktopBoxes
            .SelectMany(box => GetCachedItemsForBox(box.Id)
                .SelectMany(item => CreateNeighborIconBitmapKeys(item, (float)box.Appearance.IconSize)))
            .ToHashSet();
        foreach (var key in _iconCache.Keys.Where(key => !activeKeys.Contains(key)).ToArray())
        {
            _iconCache[key]?.Dispose();
            _iconCache.Remove(key);
        }
        foreach (var key in _iconLoadRetries.Keys.Where(key => !activeKeys.Contains(key)).ToArray())
        {
            _iconLoadRetries.Remove(key);
        }
    }

    private IEnumerable<IconBitmapKey> CreateNeighborIconBitmapKeys(
        DesktopItemRef item,
        float iconSize)
    {
        var center = QuantizeIconPixelSize((int)Math.Round(iconSize * _scale));
        foreach (var offset in new[] { -32, -16, 0, 16, 32 })
        {
            yield return new IconBitmapKey(
                item.ParsingName,
                Math.Clamp(center + offset, 16, 256),
                item.ModifiedAt?.UtcDateTime.Ticks ?? 0,
                0);
        }
    }

    private static int QuantizeIconPixelSize(int pixelSize)
    {
        pixelSize = Math.Clamp(pixelSize, 16, 256);
        return (int)(16 * Math.Round(pixelSize / 16.0, MidpointRounding.AwayFromZero));
    }

    private IconBitmapKey CreateIconBitmapKey(DesktopItemRef item, float iconSize)
    {
        return new IconBitmapKey(
            item.ParsingName,
            QuantizeIconPixelSize((int)Math.Round(iconSize * _scale)),
            item.ModifiedAt?.UtcDateTime.Ticks ?? 0,
            0);
    }

    private void InvalidateIcon(IconBitmapKey key)
    {
        // This is a full-surface layered window, so a completed icon load
        // always needs the same complete presentation regardless of how many
        // items use the bitmap.  Do not enumerate _items here: presenting a
        // layer rebuilds that list synchronously, which used to invalidate a
        // Where() enumerator between its first and second matching item.
        RequestVisualLayerRender();
    }

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

    private void DrawItemHoverOverlay(Graphics graphics, RectangleF overlayBounds)
    {
        var item = FindHoveredItem();
        var geometry = item is null
            ? null
            : _boxes.LastOrDefault(box => box.Box.Id == item.Box.Id);
        if (item is null || geometry is null)
        {
            return;
        }

        graphics.CompositingQuality = CompositingQuality.HighSpeed;
        graphics.SmoothingMode = SmoothingMode.HighSpeed;
        graphics.InterpolationMode = InterpolationMode.Low;
        graphics.PixelOffsetMode = PixelOffsetMode.HighSpeed;
        graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        graphics.TextContrast = 4;
        graphics.Transform = new Matrix(
            (float)_scale,
            0,
            0,
            (float)_scale,
            -(float)(overlayBounds.X * _scale),
            -(float)(overlayBounds.Y * _scale));
        graphics.SetClip(geometry.Body, CombineMode.Intersect);

        var baseColor = ParseOpaqueColor(geometry.Box.Appearance.Background);
        var textColor = ResolveAutoTextColor(baseColor);
        using var itemFont = geometry.Box.Appearance.ShowItemLabels
            ? CreateFont(
                geometry.Box.Appearance.LabelFontFamily,
                (float)geometry.Box.Appearance.LabelFontSize,
                FontStyle.Regular,
                GraphicsUnit.Point)
            : null;
        using var itemBrush = geometry.Box.Appearance.ShowItemLabels
            ? new SolidBrush(textColor)
            : null;
        using var itemFormat = geometry.Box.Appearance.ShowItemLabels
            ? CreateItemTextFormat(geometry.Box.ViewMode)
            : null;
        using var selectedGridItemFormat = geometry.Box.Appearance.ShowItemLabels &&
                                           geometry.Box.ViewMode == BoxViewMode.Grid
            ? CreateSelectedGridItemTextFormat()
            : null;
        DrawItem(
            graphics,
            item,
            itemFont,
            itemBrush,
            itemFormat,
            selectedGridItemFormat,
            geometry.Body,
            _selection,
            includeItemHoverFeedback: true);
        graphics.ResetTransform();
    }

    private RectangleF GetItemHoverVisualBounds(
        Graphics graphics,
        ItemGeometry item,
        RectangleF contentBounds)
    {
        var iconBounds = GetItemIconBounds(item);
        if (!item.Box.Appearance.ShowItemLabels)
        {
            return item.Bounds;
        }

        var isSelected = _selection.Contains(item.Item.Key.ToString());
        using var labelFont = CreateFont(
            item.Box.Appearance.LabelFontFamily,
            (float)item.Box.Appearance.LabelFontSize,
            FontStyle.Regular,
            GraphicsUnit.Point);
        var textBounds = GetItemTextBounds(
            graphics,
            item,
            iconBounds,
            labelFont,
            DesktopIconLabelDisplayPolicy.ShowsFullLabel(isSelected, isHovered: true),
            contentBounds);
        return textBounds.IsEmpty
            ? item.Bounds
            : RectangleF.Union(item.Bounds, textBounds);
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

    // The icon compositor can render a frame after several mouse messages
    // have been coalesced. Reconcile the captured selection against the real
    // cursor immediately before that frame is painted.
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
        if (_runtime.IsDesktopIconPointerInteractionActive ||
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
            UpdateHoverState(ToDip(clientPoint));
            return;
        }

        Cursor = Forms.Cursors.Default;
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
        var autoExpandBoxId = _boxes.LastOrDefault(box => box.AutoExpand.Contains(point))?.Box.Id;
        if (_hoveredAutoExpandBoxId != autoExpandBoxId)
        {
            var previous = _hoveredAutoExpandBoxId;
            _hoveredAutoExpandBoxId = autoExpandBoxId;
            _headerToolTip.SetToolTip(this, null);
            InvalidateHeaderButton(previous, box => box.AutoExpand);
            InvalidateHeaderButton(autoExpandBoxId, box => box.AutoExpand);
            if (autoExpandBoxId is not null)
            {
                var enabled = _boxes.FirstOrDefault(box => box.Box.Id == autoExpandBoxId)?.Box.ExpandOnHover == true;
                _headerToolTip.SetToolTip(
                    this,
                    enabled ? "切换为固定展开" : "切换为悬停自动展开");
            }
        }
        var resizeEdges = ResizeEdges.None;
        if (_runtime.State.Settings.Appearance.ShowResizeGrip &&
            _boxes.LastOrDefault(box => !box.IsCollapsed && GetResizeEdges(box, point) != ResizeEdges.None) is { } resizeBox)
        {
            resizeEdges = GetResizeEdges(resizeBox, point);
        }
        var isHeaderButton = _boxes.LastOrDefault(box =>
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
            // A desktop marquee owns the pointer capture. Do not let the
            // 25 ms box-hover poll mutate box geometry or enqueue another
            // layered presentation while that gesture is in progress.
            if (_runtime.IsDesktopIconPointerInteractionActive)
            {
                return;
            }
            if (_movingBox is not null || _resizingBox is not null)
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
        HideItemHoverOverlay();
        var previousHoveredItem = _hoveredItemKey is null
            ? null
            : _items.LastOrDefault(candidate => string.Equals(
                candidate.Item.Key.ToString(),
                _hoveredItemKey,
                StringComparison.OrdinalIgnoreCase));
        var expandedBoxIds = _hoverExpandedBoxes.ToArray();
        _hoveredItemKey = null;
        ClearAutoExpandHover();
        var expandedBoxId = _hoverExpansion.Reset();
        if (expandedBoxId is { } id)
        {
            CollapseHoverExpandedBox(id);
        }
        else
        {
            _hoverExpandedBoxes.Clear();
            _geometryDirty = true;
        }
        InvalidateItem(previousHoveredItem);
        if (expandedBoxIds.Length > 0)
        {
            UpdateWindowRegion();
            foreach (var boxId in expandedBoxIds)
            {
                InvalidateBoxVisualArea(boxId);
            }
        }
    }

    private void UpdateHoverState(PointF point, bool updateItemHover = true)
    {
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
            var hoveredBox = _boxes.LastOrDefault(candidate => candidate.Bounds.Contains(point));
            hoveredItem = GetItemAtPoint(hoveredBox, point);
            var itemKey = hoveredItem?.Item.Key.ToString();
            hoverChanged = !string.Equals(_hoveredItemKey, itemKey, StringComparison.OrdinalIgnoreCase);
            _hoveredItemKey = itemKey;
        }

        var structureChanged = false;
        var collapsedHeaderBoxId = _boxes.LastOrDefault(box =>
            box.Box.ExpandOnHover &&
            box.Header.Contains(point) &&
            !box.AutoExpand.Contains(point) &&
            !box.Menu.Contains(point))?.Box.Id;
        var pointerInsideExpandedBox = _hoverExpansion.ExpandedBoxId is { } expandedBoxId &&
            _boxes.LastOrDefault(box => box.Box.Id == expandedBoxId)?.Bounds.Contains(point) == true;
        var autoExpandEnabled = _hoverExpansion.ExpandedBoxId is not null ||
            collapsedHeaderBoxId is not null;
        var transition = autoExpandEnabled &&
            _movingBox is null && _resizingBox is null
            ? _hoverExpansion.Update(collapsedHeaderBoxId, pointerInsideExpandedBox, DateTimeOffset.UtcNow)
            : new HoverExpansionTransition(null, _hoverExpansion.Reset());
        if (transition.CollapsedBoxId is { } collapsedBoxId)
        {
            CollapseHoverExpandedBox(collapsedBoxId);
            structureChanged = true;
        }
        if (transition.ExpandedBoxId is { } boxId)
        {
            ExpandHoveredBox(boxId);
            structureChanged = true;
        }
        if (structureChanged)
        {
            HideItemHoverOverlay();
            InvalidateBoxVisualArea(transition.CollapsedBoxId);
            InvalidateBoxVisualArea(transition.ExpandedBoxId);
        }
        else if (hoverChanged)
        {
            // The shared icon layer contains only settled box pixels. Keep
            // pointer feedback in a small child layer so crossing items does
            // not upload the entire monitor-sized bitmap.
            RequestItemHoverVisualUpdate();
        }
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
        DiagnosticLog.Info(
            $"Surface mouse up monitor={_monitor.Id} button={eventArgs.Button} moving={_movingBox is not null} resizing={_resizingBox is not null} selecting={_selectionBox is not null}");
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
        if (!_isCompositedByIconSurface)
        {
            FlushTransformTrail();
        }
        if (movingBox is not null)
        {
            _runtime.BoxChanged(movingBox, true, bringToFront: true);
        }
        else if (resizingBox is not null)
        {
            _runtime.BoxChanged(resizingBox, true, bringToFront: true);
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
        _transformDirtyBounds = null;
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
        var itemCount = GetCachedItemsForBox(box.Box.Id).Count;
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
        var current = _scrollAnimationKey == scrollKey && _scrollAnimationTimer.Enabled
            ? GetAnimatedScrollOffset()
            : _scrollOffsets.GetValueOrDefault(scrollKey);
        // A standard notch is 120 units; high-resolution touchpads deliver
        // smaller deltas more often, so scale the step proportionally.
        var step = GetScrollStep(box) * (Math.Abs(eventArgs.Delta) / 120d);
        step = Math.Max(step, 4d);
        var target = Math.Clamp(current - Math.Sign(eventArgs.Delta) * step, 0, extent);
        if (Math.Abs(target - current) < 0.5)
        {
            return;
        }

        // The wheel moves the content under the cursor, so the previously
        // highlighted item no longer matches the pointer. Clear it here;
        // the next MouseMove re-establishes hover for whatever is under the
        // cursor after the scroll settles.
        ClearItemHover();
        StartScrollAnimation(scrollKey, current, target);
    }

    private double GetScrollStep(BoxGeometry box)
    {
        var lines = Forms.SystemInformation.MouseWheelScrollLines;
        if (lines <= 0)
        {
            lines = 3;
        }
        var step = box.Box.ViewMode == BoxViewMode.List
            ? Math.Max(48, box.Box.Appearance.IconSize + 12)
            : DesktopItemLayoutEngine.GetGridCellHeight(
                box.Box.Appearance.IconSize,
                DesktopItemLayoutEngine.ScaleIconSpacing(
                    _runtime.State.Settings.Appearance.IconVerticalSpacing,
                    box.Box.Appearance.IconSize));
        return Math.Max(1, step * lines);
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
        _scrollAnimationKey = key;
        _scrollAnimationFrom = from;
        _scrollAnimationTo = to;
        _scrollAnimationStartedUtc = DateTime.UtcNow;
        _scrollAnimationTimer.Stop();
        _scrollAnimationTimer.Start();
        ApplyScrollOffset(key, from);
    }

    private void OnScrollAnimationTick(object? sender, EventArgs eventArgs)
    {
        if (_scrollAnimationKey is not { } key)
        {
            _scrollAnimationTimer.Stop();
            return;
        }

        var progress = Math.Min(
            1,
            (DateTime.UtcNow - _scrollAnimationStartedUtc).TotalMilliseconds /
            ScrollAnimationDurationMilliseconds);
        var eased = 1 - Math.Pow(1 - progress, ScrollEaseExponent);
        var offset = _scrollAnimationFrom + (_scrollAnimationTo - _scrollAnimationFrom) * eased;
        if (progress >= 1)
        {
            offset = _scrollAnimationTo;
            _scrollAnimationKey = null;
            _scrollAnimationTimer.Stop();
        }
        ApplyScrollOffset(key, offset);
    }

    private void ApplyScrollOffset(ItemViewKey key, double offset)
    {
        _scrollOffsets[key] = offset;
        // Only the item rectangles depend on the scroll offset; the box
        // chrome (header, tabs, body) stays untouched. Skipping the full
        // geometry rebuild keeps each animation frame cheap enough to render
        // without dropping the input pipeline.
        if (_geometryDirty)
        {
            // A full rebuild is already queued; it picks up the offset.
            RequestLayerRender();
            return;
        }
        _items.Clear();
        foreach (var box in _boxes.Where(box => !box.IsCollapsed))
        {
            BuildItemGeometry(box);
        }
        RequestLayerRender();
    }

    private void OnDragOver(object? sender, Forms.DragEventArgs eventArgs)
    {
        var point = ToDip(PointToClient(new Point(eventArgs.X, eventArgs.Y)));
        ForwardDragStateToIconSurface(eventArgs, point);
        // Box geometry is static during an OLE item drag; the shared compositor
        // already rebuilt it on the previous frame. Rebuilding per DragOver
        // event stalls the drag loop on fast mice.
        EnsureGeometry();
        var targetGeometry = _boxes.LastOrDefault(candidate => candidate.Bounds.Contains(point));
        if (targetGeometry is null)
        {
            ClearDropPreview();
            eventArgs.Effect = Forms.DragDropEffects.None;
            return;
        }
        var target = targetGeometry.Box;

        if (eventArgs.Data?.GetDataPresent(DesktopIconSurface.DesktopIconDragSessionFormat) == true &&
            eventArgs.Data.GetData(DesktopIconSurface.DesktopIconDragSessionFormat) is DesktopIconSurfaceDragSession desktopDrag)
        {
            // This private payload only represents a CrabDesk desktop item.
            // Do not mark it handled until DragDrop: a pointer may pass over a
            // box and then return to the desktop before the button is released.
            var acceptsDrop = !target!.IsMappedFolder && target.MappedFolder?.IsReadOnly != true;
            UpdateOleDropPreview(
                targetGeometry,
                point,
                desktopDrag.ItemKeys,
                desktopDrag.ItemKeys.Count,
                acceptsDrop,
                // Desktop drags use the same projected placement marker as
                // the in-process pointer drag. The dragged icons already
                // follow the pointer, so no floating card is drawn here.
                DropPreviewKind.DesktopAssign,
                floatingCard: false);
            eventArgs.Effect = acceptsDrop ? Forms.DragDropEffects.Copy : Forms.DragDropEffects.None;
            return;
        }

        // Legacy pointer-only desktop drags still render their preview from
        // DesktopIconSurface. Keep that path out of the generic thumbnail
        // renderer while an OLE session is not present.
        if (_runtime.IsDesktopIconPointerInteractionActive)
        {
            var acceptsDesktopDrop = !targetGeometry.Box.IsMappedFolder &&
                                     targetGeometry.Box.MappedFolder?.IsReadOnly != true;
            eventArgs.Effect = acceptsDesktopDrop
                ? Forms.DragDropEffects.Copy
                : Forms.DragDropEffects.None;
            return;
        }

        var desktopVirtualAssignment = IsDesktopVirtualAssignment(eventArgs, target);
        if (target!.MappedFolder?.IsReadOnly == true)
        {
            UpdateOleDropPreview(
                targetGeometry,
                point,
                GetDragItemKeys(eventArgs),
                GetDragItemCount(eventArgs),
                false,
                desktopVirtualAssignment ? DropPreviewKind.DesktopAssign : DropPreviewKind.Assign,
                floatingCard: false);
            eventArgs.Effect = Forms.DragDropEffects.None;
            return;
        }
        var effect = ResolveTransferEffect(eventArgs, target);
        if (effect == BoxTransferEffect.VirtualMove && targetGeometry is not null &&
            GetMappedFolderTabAtPoint(targetGeometry, point) is not null)
        {
            // File-type tabs are filtered views, not drop destinations.
            UpdateOleDropPreview(
                targetGeometry,
                point,
                GetDragItemKeys(eventArgs),
                GetDragItemCount(eventArgs),
                false,
                desktopVirtualAssignment ? DropPreviewKind.DesktopAssign : DropPreviewKind.Assign,
                floatingCard: false);
            eventArgs.Effect = Forms.DragDropEffects.None;
            return;
        }
        // A desktop file dropped into a normal box is a virtual assignment,
        // not a filesystem move. Advertising Move makes Explorer dim the
        // source icon as a cut operation until its delayed shell refresh.
        eventArgs.Effect = desktopVirtualAssignment
            ? Forms.DragDropEffects.Copy
            : ToDragDropEffects(effect);
        var previewKind = desktopVirtualAssignment
            ? DropPreviewKind.DesktopAssign
            : effect == BoxTransferEffect.VirtualMove &&
                          eventArgs.Data?.GetDataPresent(ItemKeysFormat) == true &&
                          eventArgs.Data.GetData(SourceBoxFormat) is string sourceValue &&
                          Guid.TryParse(sourceValue, out var sourceBoxId) &&
                          sourceBoxId == target.Id &&
                          GetManualBoxTabAtPoint(targetGeometry!, point) is null
            ? DropPreviewKind.Reorder
            : DropPreviewKind.Assign;
        UpdateOleDropPreview(
            targetGeometry!,
            point,
            GetDragItemKeys(eventArgs),
            GetDragItemCount(eventArgs),
            eventArgs.Effect != Forms.DragDropEffects.None,
            previewKind,
            // Box-item drags carry no shell drag image, so the box draws the
            // shared ghost card itself. External file and desktop-icon drags
            // already have a following ghost and only need slot feedback.
            floatingCard: eventArgs.Data?.GetDataPresent(ItemKeysFormat) == true);
    }

    private void ForwardDragStateToIconSurface(
        Forms.DragEventArgs eventArgs,
        PointF point)
    {
        var forward = _iconDragStateForward;
        if (forward is null || eventArgs.Data is null)
        {
            return;
        }

        try
        {
            // A CrabDesk desktop-icon drag carries FileDrop paths too. Treat
            // it as a desktop drag: the dragged icons are the ghost.
            if (eventArgs.Data.GetDataPresent(DesktopIconSurface.DesktopIconDragSessionFormat) &&
                eventArgs.Data.GetData(DesktopIconSurface.DesktopIconDragSessionFormat) is
                    DesktopIconSurfaceDragSession desktopDrag)
            {
                forward(point, null, desktopDrag.ItemKeys);
                return;
            }

            // Box-item drags draw their own ghost card on this surface; the
            // surface must not paint a second external card (and leave a
            // stale one behind after the drop).
            if (eventArgs.Data.GetDataPresent(ItemKeysFormat))
            {
                forward(point, null, null);
                return;
            }

            IReadOnlyList<string>? externalPaths = null;
            if (eventArgs.Data.GetDataPresent(Forms.DataFormats.FileDrop) &&
                eventArgs.Data.GetData(Forms.DataFormats.FileDrop) is string[] paths)
            {
                externalPaths = paths;
            }
            forward(point, externalPaths, null);
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("Box drag state forward failed", exception);
        }
    }

    private void OnDragLeave(object? sender, EventArgs eventArgs)
    {
        ClearDropPreview();
        _iconDragStateForward?.Invoke(PointF.Empty, null, null);
    }

    private void UpdateOleDropPreview(
        BoxGeometry target,
        PointF point,
        IReadOnlyList<string> itemKeys,
        int itemCount,
        bool acceptsDrop,
        DropPreviewKind kind,
        bool floatingCard = false)
    {
        var manualTabIndex = GetManualBoxTabIndex(target, point);
        SetDropPreview(new DropPreviewState(
            target.Box.Id,
            point,
            itemKeys,
            itemCount,
            acceptsDrop,
            kind,
            manualTabIndex,
            floatingCard));
    }

    private static int GetDragItemCount(Forms.DragEventArgs eventArgs)
    {
        if (eventArgs.Data?.GetDataPresent(ItemKeysFormat) == true &&
            eventArgs.Data.GetData(ItemKeysFormat) is string[] keys)
        {
            return Math.Max(1, keys.Length);
        }
        if (eventArgs.Data?.GetDataPresent(Forms.DataFormats.FileDrop) == true &&
            eventArgs.Data.GetData(Forms.DataFormats.FileDrop) is string[] paths)
        {
            return Math.Max(1, paths.Length);
        }
        return 1;
    }

    private IReadOnlyList<string> GetDragItemKeys(Forms.DragEventArgs eventArgs)
    {
        if (eventArgs.Data?.GetDataPresent(ItemKeysFormat) == true &&
            eventArgs.Data.GetData(ItemKeysFormat) is string[] keys)
        {
            return keys;
        }

        // Explorer's desktop drag exposes only FileDrop paths. Resolve those
        // paths back to the stable runtime keys so DesktopAssign can project
        // the exact destination slot before the drop is committed.
        if (eventArgs.Data?.GetDataPresent(Forms.DataFormats.FileDrop) == true &&
            eventArgs.Data.GetData(Forms.DataFormats.FileDrop) is string[] paths)
        {
            var desktopItemsByPath = _runtime.Items
                .Where(item => item.FileSystemPath is not null)
                .ToDictionary(
                    item => Path.GetFullPath(item.FileSystemPath!),
                    item => item.Key.ToString(),
                    StringComparer.OrdinalIgnoreCase);
            return paths
                .Select(path => Path.GetFullPath(path))
                .Where(desktopItemsByPath.ContainsKey)
                .Select(path => desktopItemsByPath[path])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        return [];
    }

    private bool IsDesktopVirtualAssignment(Forms.DragEventArgs eventArgs, DesktopBox target)
    {
        if (target.IsMappedFolder || eventArgs.Data is null ||
            eventArgs.Data.GetDataPresent(ItemKeysFormat) ||
            !eventArgs.Data.GetDataPresent(Forms.DataFormats.FileDrop) ||
            eventArgs.Data.GetData(Forms.DataFormats.FileDrop) is not string[] paths)
        {
            return false;
        }

        var desktopPaths = _runtime.Items
            .Where(item => item.FileSystemPath is not null)
            .Select(item => Path.GetFullPath(item.FileSystemPath!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return paths.Length > 0 && paths.All(path => desktopPaths.Contains(Path.GetFullPath(path)));
    }

    private async void OnDragDrop(object? sender, Forms.DragEventArgs eventArgs)
    {
        DiagnosticLog.Info($"Surface drag drop monitor={_monitor.Id} effects={eventArgs.AllowedEffect}");
        // The OLE drag ends with the drop. WinForms does not reliably raise
        // DragLeave afterwards, so clear the icon surface's ghost state here
        // or a stale card can stay frozen on screen after the drop.
        _iconDragStateForward?.Invoke(PointF.Empty, null, null);
        try
        {
            if (eventArgs.Data is null)
            {
                return;
            }
            var point = ToDip(PointToClient(new Point(eventArgs.X, eventArgs.Y)));
            RebuildGeometry();
            var box = _boxes.LastOrDefault(candidate => candidate.Bounds.Contains(point));
            if (box is null)
            {
                return;
            }
            var manualTargetTab = GetManualBoxTabAtPoint(box, point);
            var mappedTargetTab = GetMappedFolderTabAtPoint(box, point);
            if (eventArgs.Data.GetDataPresent(DesktopIconSurface.DesktopIconDragSessionFormat) &&
                eventArgs.Data.GetData(DesktopIconSurface.DesktopIconDragSessionFormat) is DesktopIconSurfaceDragSession desktopDrag)
            {
                desktopDrag.HandledByBox = true;
                if (box.Box.IsMappedFolder || box.Box.MappedFolder?.IsReadOnly == true)
                {
                    return;
                }

                AssignDesktopItemsAtDrop(box, point, desktopDrag.ItemKeys);
                return;
            }
            var transferEffect = ResolveTransferEffect(eventArgs, box.Box);
            DiagnosticLog.Info($"Surface drag drop resolved monitor={_monitor.Id} effect={transferEffect}");
            if (transferEffect == BoxTransferEffect.None)
            {
                return;
            }
            if (eventArgs.Data.GetDataPresent(ItemKeysFormat) &&
                eventArgs.Data.GetData(ItemKeysFormat) is string[] keys &&
                eventArgs.Data.GetDataPresent(SourceBoxFormat) &&
                eventArgs.Data.GetData(SourceBoxFormat) is string sourceValue &&
                Guid.TryParse(sourceValue, out var sourceBoxId))
            {
                if (eventArgs.Data.GetDataPresent(DragSessionFormat) &&
                    eventArgs.Data.GetData(DragSessionFormat) is InternalDragSession dragSession)
                {
                    dragSession.HandledByBox = true;
                }
                if (sourceBoxId == box.Box.Id)
                {
                    if (manualTargetTab is not null)
                    {
                        _runtime.MoveItemsToManualTab(box.Box.Id, keys, manualTargetTab.Id);
                        return;
                    }
                    if (mappedTargetTab is not null)
                    {
                        return;
                    }
                    var beforeKey = GetReorderBeforeKey(box, point);
                    _runtime.ReorderBoxItems(box.Box.Id, keys, beforeKey);
                    return;
                }
                try
                {
                    var imported = await _runtime.TransferBoxItemsAsync(
                        sourceBoxId,
                        keys,
                        box.Box.Id,
                        transferEffect == BoxTransferEffect.MoveFiles);
                    ShowImportFailures(imported);
                    if (manualTargetTab is not null)
                    {
                        _runtime.MoveItemsToManualTab(box.Box.Id, keys, manualTargetTab.Id);
                    }
                }
                catch (Exception exception)
                {
                    Forms.MessageBox.Show(exception.Message, "CrabDesk", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Error);
                }
                return;
            }
            if (!eventArgs.Data.GetDataPresent(Forms.DataFormats.FileDrop) || eventArgs.Data.GetData(Forms.DataFormats.FileDrop) is not string[] paths)
            {
                return;
            }

            if (box.Box.IsMappedFolder)
            {
                try
                {
                    var imported = await _runtime.ImportFilesToBoxAsync(
                        paths,
                        box.Box.Id,
                        transferEffect == BoxTransferEffect.MoveFiles);
                    ShowImportFailures(imported);
                }
                catch (Exception exception)
                {
                    Forms.MessageBox.Show(exception.Message, "CrabDesk", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Error);
                }
                return;
            }

            var desktopPaths = _runtime.Items
                .Where(item => item.FileSystemPath is not null)
                .ToDictionary(item => Path.GetFullPath(item.FileSystemPath!), StringComparer.OrdinalIgnoreCase);
            var assignedKeys = new List<string>();
            var external = new List<string>();
            foreach (var path in paths)
            {
                var fullPath = Path.GetFullPath(path);
                if (desktopPaths.TryGetValue(fullPath, out var item))
                {
                    assignedKeys.Add(item.Key.ToString());
                }
                else
                {
                    external.Add(path);
                }
            }
            AssignDesktopItemsAtDrop(box, point, assignedKeys);
            if (external.Count > 0)
            {
                var imported = await _runtime.ImportFilesAsync(
                    external,
                    box.Box.Id,
                    transferEffect == BoxTransferEffect.MoveFiles);
                ShowImportFailures(imported);
            }
            // Assigned desktop icons are parked outside the visible work area by
            // the runtime; no per-drop Explorer move is needed here.
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("Desktop box drag-drop failed.", exception);
            Forms.MessageBox.Show(
                this,
                exception.Message,
                "导入失败",
                Forms.MessageBoxButtons.OK,
                Forms.MessageBoxIcon.Error);
        }
        finally
        {
            ClearDropPreview();
        }
    }

    private BoxTransferEffect ResolveTransferEffect(Forms.DragEventArgs eventArgs, DesktopBox target)
    {
        if (target.MappedFolder?.IsReadOnly == true || eventArgs.Data is null)
        {
            return BoxTransferEffect.None;
        }
        var internalItems = eventArgs.Data.GetDataPresent(ItemKeysFormat);
        Guid? sourceId = null;
        var sourceMapped = false;
        var sourceMappedReadOnly = false;
        if (eventArgs.Data.GetDataPresent(SourceBoxFormat) &&
            eventArgs.Data.GetData(SourceBoxFormat) is string sourceValue &&
            Guid.TryParse(sourceValue, out var parsedSourceId))
        {
            sourceId = parsedSourceId;
            var source = _runtime.State.Boxes.FirstOrDefault(box => box.Id == parsedSourceId);
            sourceMapped = source?.IsMappedFolder == true;
            sourceMappedReadOnly = source?.MappedFolder?.IsReadOnly == true;
        }
        if (sourceId == target.Id)
        {
            return BoxTransferEffect.VirtualMove;
        }
        const int shiftKeyState = 4;
        const int controlKeyState = 8;
        return BoxTransferPolicy.Resolve(
            internalItems,
            sourceMapped,
            target.IsMappedFolder,
            (eventArgs.KeyState & shiftKeyState) != 0,
            (eventArgs.KeyState & controlKeyState) != 0,
            sourceMappedReadOnly);
    }

    private static Forms.DragDropEffects ToDragDropEffects(BoxTransferEffect effect) => effect switch
    {
        BoxTransferEffect.VirtualMove or BoxTransferEffect.MoveFiles => Forms.DragDropEffects.Move,
        BoxTransferEffect.CopyFiles => Forms.DragDropEffects.Copy,
        _ => Forms.DragDropEffects.None
    };

    private Forms.ContextMenuStrip BuildBoxMenu(DesktopBox box)
    {
        var menu = CreateContextMenu();
        if (box.IsMappedFolder)
        {
            menu.Items.Add("打开映射文件夹", null, (_, _) =>
                TryAction(() => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(box.MappedFolder!.Path)
                {
                    UseShellExecute = true
                })));
            menu.Items.Add(new Forms.ToolStripSeparator());
        }
        var paste = new Forms.ToolStripMenuItem("粘贴")
        {
            Enabled = _runtime.CanPasteIntoBox(box)
        };
        paste.Click += async (_, _) => await PasteIntoBoxAsync(box);
        menu.Items.Add(paste);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("重命名", null, (_, _) =>
        {
            BeginInvoke((Action)(() => BeginTitleEdit(box)));
        });
        var displayModeMenu = new Forms.ToolStripMenuItem("显示模式");
        AddMenuChoice(
            displayModeMenu,
            "固定展开",
            !box.ExpandOnHover,
            () => SetBoxDisplayMode(box, expandOnHover: false));
        AddMenuChoice(
            displayModeMenu,
            "悬停自动展开",
            box.ExpandOnHover,
            () => SetBoxDisplayMode(box, expandOnHover: true));
        menu.Items.Add(displayModeMenu);
        if (!box.IsMappedFolder)
        {
            AddManualTabMenu(menu, box);
        }
        var accentMenu = new Forms.ToolStripMenuItem("颜色条颜色");
        var stackMenu = new Forms.ToolStripMenuItem("层级");
        stackMenu.DropDownItems.Add("置于顶层", null, (_, _) =>
            _runtime.MoveBoxInStack(box.Id, BoxStackMove.ToFront));
        stackMenu.DropDownItems.Add("上移一层", null, (_, _) =>
            _runtime.MoveBoxInStack(box.Id, BoxStackMove.Forward));
        stackMenu.DropDownItems.Add("下移一层", null, (_, _) =>
            _runtime.MoveBoxInStack(box.Id, BoxStackMove.Backward));
        stackMenu.DropDownItems.Add("置于底层", null, (_, _) =>
            _runtime.MoveBoxInStack(box.Id, BoxStackMove.ToBack));
        menu.Items.Add(stackMenu);

        foreach (var (name, hex) in AccentPalette)
        {
            AddMenuChoice(
                accentMenu,
                name,
                string.Equals(box.Appearance.Accent, hex, StringComparison.OrdinalIgnoreCase),
                () => _runtime.SetBoxAccent(box.Id, hex));
        }
        accentMenu.DropDownItems.Add(new Forms.ToolStripSeparator());
        accentMenu.DropDownItems.Add("自定义颜色…", null, (_, _) => ShowAccentColorDialog(box));
        menu.Items.Add(accentMenu);

        var viewMenu = new Forms.ToolStripMenuItem("视图");
        AddMenuChoice(viewMenu, "图标", box.ViewMode == BoxViewMode.Grid,
            () => _runtime.SetBoxViewMode(box.Id, BoxViewMode.Grid));
        AddMenuChoice(viewMenu, "列表", box.ViewMode == BoxViewMode.List,
            () => _runtime.SetBoxViewMode(box.Id, BoxViewMode.List));
        menu.Items.Add(viewMenu);

        var sortMenu = new Forms.ToolStripMenuItem("排序方式");
        AddMenuChoice(sortMenu, "手动", box.SortMode == BoxSortMode.Manual,
            () => _runtime.SetBoxSortMode(box.Id, BoxSortMode.Manual));
        AddMenuChoice(sortMenu, "名称", box.SortMode == BoxSortMode.Name,
            () => _runtime.SetBoxSortMode(box.Id, BoxSortMode.Name));
        AddMenuChoice(sortMenu, "类型", box.SortMode == BoxSortMode.Type,
            () => _runtime.SetBoxSortMode(box.Id, BoxSortMode.Type));
        AddMenuChoice(sortMenu, "修改时间", box.SortMode == BoxSortMode.Modified,
            () => _runtime.SetBoxSortMode(box.Id, BoxSortMode.Modified));
        menu.Items.Add(sortMenu);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("设置", null, (_, _) => _runtime.RequestShowSettings("appearance"));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("删除盒子", null, async (_, _) =>
        {
            try
            {
                var detail = box.IsMappedFolder
                    ? "不会删除映射文件夹或其中的文件。"
                    : "盒子中的文件仍保留在桌面。";
                if (!_runtime.State.Settings.ConfirmDeleteBox ||
                    await ConfirmDesktopAsync(
                        $"删除“{box.Title}”？",
                        detail,
                        "删除盒子"))
                {
                    _runtime.DeleteBox(box);
                }
            }
            catch (Exception exception)
            {
                DiagnosticLog.Error($"Delete box failed: {exception}", exception);
            }
        });
        return menu;
    }

    private void AddManualTabMenu(Forms.ContextMenuStrip menu, DesktopBox box)
    {
        var tabMenu = new Forms.ToolStripMenuItem("子标签");
        tabMenu.DropDownItems.Add("新建子标签…", null, (_, _) =>
            BeginInvoke((Action)(() => CreateManualTab(box))));

        var activeTabId = _activeManualTabIds.GetValueOrDefault(box.Id);
        var activeTab = activeTabId is { } id
            ? box.ManualTabs.FirstOrDefault(tab => tab.Id == id)
            : null;
        if (activeTab is not null)
        {
            tabMenu.DropDownItems.Add(new Forms.ToolStripSeparator());
            tabMenu.DropDownItems.Add("重命名当前标签…", null, (_, _) =>
                BeginInvoke((Action)(() => RenameManualTab(box, activeTab))));
            tabMenu.DropDownItems.Add("删除当前标签", null, (_, _) =>
                BeginInvoke((Action)(async () => await DeleteManualTab(box, activeTab))));
        }

        var selectedKeys = GetSelectedItemKeys(box.Id);
        if (box.ManualTabs.Count > 0 && selectedKeys.Length > 0)
        {
            var moveMenu = new Forms.ToolStripMenuItem("将选中图标移到");
            moveMenu.DropDownItems.Add("全部（移出子标签）", null, (_, _) =>
                MoveSelectedItemsToManualTab(box, selectedKeys, null));
            foreach (var tab in box.ManualTabs)
            {
                var targetTab = tab;
                moveMenu.DropDownItems.Add(targetTab.Title, null, (_, _) =>
                    MoveSelectedItemsToManualTab(box, selectedKeys, targetTab.Id));
            }
            tabMenu.DropDownItems.Add(new Forms.ToolStripSeparator());
            tabMenu.DropDownItems.Add(moveMenu);
        }

        menu.Items.Add(tabMenu);
    }

    private string[] GetSelectedItemKeys(Guid boxId) => GetCachedItemsForBox(boxId)
        .Select(item => item.Key.ToString())
        .Where(_selection.Contains)
        .ToArray();

    private void CreateManualTab(DesktopBox box)
    {
        var title = PromptForManualTabTitle("新建子标签", "标签名称", "新标签");
        if (title is null)
        {
            return;
        }

        var tab = _runtime.CreateManualTab(box.Id, title);
        _activeManualTabIds[box.Id] = tab.Id;
        ClearBoxItemSelection(box.Id);
        Invalidate();
    }

    private void RenameManualTab(DesktopBox box, DesktopBoxTab tab)
    {
        var title = PromptForManualTabTitle("重命名子标签", "标签名称", tab.Title);
        if (title is not null)
        {
            _runtime.RenameManualTab(box.Id, tab.Id, title);
        }
    }

    private async Task DeleteManualTab(DesktopBox box, DesktopBoxTab tab)
    {
        if (!await ConfirmDesktopAsync(
                $"删除“{tab.Title}”标签？",
                "该标签中的图标会保留在盒子里，并回到“全部”。",
                "删除标签"))
        {
            return;
        }

        if (_runtime.DeleteManualTab(box.Id, tab.Id))
        {
            _activeManualTabIds[box.Id] = null;
            ClearBoxItemSelection(box.Id);
            Invalidate();
        }
    }
    private async Task<bool> ConfirmDesktopAsync(string title, string message, string primaryText)
    {
        if (_confirmationInProgress)
        {
            return false;
        }
        var handler = _runtime.DesktopConfirmationHandler;
        if (handler is null)
        {
            return DesktopConfirmationDialog.Show(this, _runtime.IsDarkTheme, title, message, primaryText);
        }
        _confirmationInProgress = true;
        try
        {
            return await handler(new DesktopConfirmationRequest(Handle, title, message, primaryText));
        }
        finally
        {
            _confirmationInProgress = false;
        }
    }

    private async Task PasteIntoBoxAsync(DesktopBox box)
    {
        if (!_runtime.CanPasteIntoBox(box))
        {
            return;
        }

        try
        {
            var result = await _runtime.PasteIntoBoxAsync(box.Id);
            ShowImportFailures(result.ImportResult);
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error($"Failed to paste files into box '{box.Title}'.", exception);
            Forms.MessageBox.Show(
                this,
                exception.Message,
                "粘贴失败",
                Forms.MessageBoxButtons.OK,
                Forms.MessageBoxIcon.Error);
        }
    }

    private void ShowImportFailures(FileImportBatchResult result)
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
            details += Environment.NewLine + $"另有 {result.FailedCount - 3} 项未导入。";
        }

        Forms.MessageBox.Show(
            this,
            $"已导入 {result.SucceededCount} 项，{result.FailedCount} 项未导入。{Environment.NewLine}{Environment.NewLine}{details}",
            "导入未完成",
            Forms.MessageBoxButtons.OK,
            Forms.MessageBoxIcon.Warning);
    }

    private void MoveSelectedItemsToManualTab(DesktopBox box, IEnumerable<string> itemKeys, Guid? tabId)
    {
        if (_runtime.MoveItemsToManualTab(box.Id, itemKeys, tabId) > 0)
        {
            ClearBoxItemSelection(box.Id);
            Invalidate();
        }
    }

    private string? PromptForManualTabTitle(string title, string label, string initialValue)
    {
        var isDark = _runtime.IsDarkTheme;
        using var dialog = new Forms.Form
        {
            Text = title,
            AccessibleName = title,
            AutoScaleMode = Forms.AutoScaleMode.Dpi,
            BackColor = isDark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(250, 250, 250),
            ClientSize = new Size(360, 160),
            FormBorderStyle = Forms.FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            StartPosition = Forms.FormStartPosition.CenterParent,
            Font = CreateFont("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point)
        };
        var foreground = isDark ? Color.FromArgb(245, 245, 245) : Color.FromArgb(31, 31, 31);
        var input = new Forms.TextBox
        {
            AccessibleName = label,
            Font = CreateFont("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point),
            Location = new Point(20, 54),
            Size = new Size(320, 28),
            Text = initialValue
        };
        var labelControl = new Forms.Label
        {
            AutoSize = true,
            ForeColor = foreground,
            Location = new Point(20, 24),
            Text = label
        };
        var cancel = new Forms.Button
        {
            DialogResult = Forms.DialogResult.Cancel,
            Location = new Point(174, 108),
            Size = new Size(78, 30),
            Text = "取消"
        };
        var confirm = new Forms.Button
        {
            DialogResult = Forms.DialogResult.OK,
            Location = new Point(262, 108),
            Size = new Size(78, 30),
            Text = "确定"
        };
        dialog.Controls.AddRange([labelControl, input, cancel, confirm]);
        dialog.AcceptButton = confirm;
        dialog.CancelButton = cancel;
        dialog.Shown += (_, _) =>
        {
            input.SelectAll();
            input.Focus();
        };
        return dialog.ShowDialog(this) == Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(input.Text)
            ? input.Text.Trim()
            : null;
    }

    private void ShowAccentColorDialog(DesktopBox box)
    {
        using var dialog = new Forms.ColorDialog
        {
            Color = ParseOpaqueColor(box.Appearance.Accent),
            AnyColor = true,
            FullOpen = true,
            SolidColorOnly = true
        };
        if (dialog.ShowDialog(this) != Forms.DialogResult.OK)
        {
            return;
        }

        var color = dialog.Color;
        _runtime.SetBoxAccent(box.Id, $"#FF{color.R:X2}{color.G:X2}{color.B:X2}");
    }

    private void ShowItemContextMenu(DesktopBox box, DesktopItemRef item, Point location)
    {
        var selectedItems = GetCachedItemsForBox(box.Id)
            .Where(candidate => _selection.Contains(candidate.Key.ToString()))
            .ToArray();
        if (selectedItems.Length == 0)
        {
            selectedItems = [item];
        }
        if (item.FileSystemPath is { } clickedPath)
        {
            var clickedParent = Path.GetDirectoryName(Path.GetFullPath(clickedPath));
            selectedItems = selectedItems
                .Where(candidate => candidate.FileSystemPath is { } candidatePath &&
                    string.Equals(
                        Path.GetDirectoryName(Path.GetFullPath(candidatePath)),
                        clickedParent,
                        StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        else
        {
            selectedItems = [item];
        }
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
        var canRename = box.MappedFolder?.IsReadOnly != true &&
            selectedItems.Length == 1 && selectedItems[0].FileSystemPath is not null;
        var command = ShellContextMenuCommand.None;
        _shellContextMenu = session;
        try
        {
            var screenPoint = PointToScreen(location);
            command = session.Show(
                Handle,
                screenPoint.X,
                screenPoint.Y,
                canRename,
                box.MappedFolder?.IsReadOnly == true
                    ? ShellContextMenuRestrictions.BlockFileMutations
                    : ShellContextMenuRestrictions.None);
        }
        finally
        {
            _shellContextMenu = null;
            session.Dispose();
        }

        if (command == ShellContextMenuCommand.Rename && canRename)
        {
            _ = RenameItemAsync(box, selectedItems[0]);
        }
    }

    private async Task RenameItemAsync(DesktopBox box, DesktopItemRef item)
    {
        if (box.MappedFolder?.IsReadOnly == true)
        {
            return;
        }

        var newName = await ShowInlineRenameAsync(box, item);
        if (newName is null ||
            string.Equals(newName, item.DisplayName, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            await _runtime.RenameItemAsync(item, newName, box.Id);
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error($"Failed to rename box item '{item.DisplayName}'.", exception);
            Forms.MessageBox.Show(
                this,
                exception.Message,
                "重命名失败",
                Forms.MessageBoxButtons.OK,
                Forms.MessageBoxIcon.Error);
        }
    }

    private async Task<string?> ShowInlineRenameAsync(DesktopBox box, DesktopItemRef item)
    {
        var geometry = _items.LastOrDefault(candidate =>
            candidate.Box.Id == box.Id &&
            string.Equals(
                candidate.Item.Key.ToString(),
                item.Key.ToString(),
                StringComparison.OrdinalIgnoreCase));
        if (geometry is null)
        {
            return null;
        }

        _renameEditor ??= new DesktopRenameEditor();
        var labelBounds = GetItemLabelEditBounds(geometry);
        var scale = (float)Math.Max(_scale, 0.01d);
        var screenLocation = PointToScreen(new Point(
            (int)Math.Round(labelBounds.X * scale),
            (int)Math.Round(labelBounds.Y * scale)));
        var selectStem = item.Kind == DesktopItemKind.File ||
            item.Kind == DesktopItemKind.Shortcut;
        using var labelFont = CreateFont(
            geometry.Box.Appearance.LabelFontFamily,
            (float)geometry.Box.Appearance.LabelFontSize,
            FontStyle.Regular,
            GraphicsUnit.Point);
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

    private RectangleF GetItemLabelEditBounds(ItemGeometry item)
    {
        var iconBounds = GetItemIconBounds(item);
        using var measureBitmap = DesktopLayerBitmapFactory.Create(1, 1);
        using var measureGraphics = Graphics.FromImage(measureBitmap);
        using var labelFont = CreateFont(
            item.Box.Appearance.LabelFontFamily,
            (float)item.Box.Appearance.LabelFontSize,
            FontStyle.Regular,
            GraphicsUnit.Point);

        if (item.Box.ViewMode == BoxViewMode.List)
        {
            // The list row keeps the full text column as the edit area, like
            // Explorer's list-view rename box.
            return new RectangleF(
                iconBounds.Right + 10,
                item.Bounds.Y + 1,
                Math.Max(40, item.Bounds.Right - iconBounds.Right - 18),
                Math.Max(20, item.Bounds.Height - 2));
        }

        var textTop = iconBounds.Bottom + 3;
        var textWidth = Math.Max(0, item.Bounds.Width - 4);
        var layout = new RectangleF(
            item.Bounds.X + 2,
            textTop,
            textWidth,
            Math.Max(
                0,
                Math.Min(
                    item.Bounds.Bottom - textTop - 3,
                    labelFont.GetHeight(measureGraphics) * CompactGridLabelLineCount + 2)));
        var hit = MeasureLabelFootprint(measureGraphics, item.Item.DisplayName, layout, labelFont);
        var lineHeight = Math.Max(1, labelFont.GetHeight(measureGraphics));
        var maxWidth = Math.Max(0, item.Bounds.Width - 6);
        var width = hit.IsEmpty
            ? maxWidth
            : Math.Min(maxWidth, Math.Max(48, hit.Width + 10));
        var centerX = hit.IsEmpty
            ? layout.X + layout.Width / 2
            : hit.X + hit.Width / 2;
        var left = Math.Max(
            item.Bounds.X + 1,
            Math.Min(centerX - width / 2, item.Bounds.Right - width - 1));
        // Match the measured label height so wrapped names get a two-line
        // editor with the full name centered (the multiline input).
        var labelHeight = hit.IsEmpty
            ? lineHeight
            : Math.Max(lineHeight, hit.Height);
        return new RectangleF(left, Math.Max(1, textTop - 3), width, labelHeight + 8);
    }

    private static RectangleF MeasureLabelFootprint(
        Graphics graphics,
        string displayName,
        RectangleF textBounds,
        Font font)
    {
        if (textBounds.Width <= 0 || textBounds.Height <= 0 || string.IsNullOrWhiteSpace(displayName))
        {
            return RectangleF.Empty;
        }

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

    private Forms.ContextMenuStrip CreateContextMenu()
    {
        var menu = new FluentContextMenuStrip();
        menu.Opening += (_, _) => _runtime.ApplyContextMenuTheme(menu);
        menu.Opened += (_, _) => _runtime.ApplyContextMenuTheme(menu);
        // ContextMenuStrip is still referenced by ToolStripManager while the
        // Closed event is running. Disposing it synchronously here leaves a
        // disposed active drop-down behind and crashes on the next mouse press.
        menu.Closed += (_, _) =>
        {
            if (IsDisposed || !IsHandleCreated)
            {
                return;
            }
            BeginInvoke((Action)(() => menu.Dispose()));
        };
        _runtime.ApplyContextMenuTheme(menu);
        return menu;
    }

    private static void AddMenuChoice(
        Forms.ToolStripMenuItem parent,
        string text,
        bool isChecked,
        Action action)
    {
        var item = new Forms.ToolStripMenuItem(text)
        {
            Checked = isChecked,
            CheckOnClick = false
        };
        item.Click += (_, _) => action();
        parent.DropDownItems.Add(item);
    }

    private static void DrawMenuIcon(Graphics graphics, RectangleF bounds, Color color)
    {
        var centerX = bounds.Left + bounds.Width / 2;
        var centerY = bounds.Top + bounds.Height / 2;
        using var pen = new Pen(color, 1.6f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        for (var offset = -3; offset <= 3; offset += 3)
        {
            graphics.DrawLine(
                pen,
                centerX - 4,
                centerY + offset,
                centerX + 4,
                centerY + offset);
        }
    }

    private static void DrawAutoExpandButton(
        Graphics graphics,
        RectangleF bounds,
        bool enabled,
        bool hovered,
        Color accent,
        Color textColor,
        bool isDark)
    {
        if (enabled || hovered)
        {
            var fillColor = enabled
                ? Color.FromArgb(isDark ? 76 : 48, accent)
                : Color.FromArgb(isDark ? 36 : 24, textColor);
            using var fill = new SolidBrush(fillColor);
            using var path = RoundedRectangle(RectangleF.Inflate(bounds, -2, -2), 4);
            graphics.FillPath(fill, path);
            if (enabled)
            {
                using var border = new Pen(Color.FromArgb(isDark ? 150 : 120, accent), 1);
                graphics.DrawPath(border, path);
            }
        }

        var iconColor = enabled ? accent : textColor;
        // Keep the hit target at 26x28 DIP, but render the glyph at the
        // compact Fluent icon size used by the other header buttons.
        var scale = Math.Min(bounds.Width, bounds.Height) / 20f * 0.72f;
        var originX = bounds.Left + (bounds.Width - 20 * scale) / 2;
        var originY = bounds.Top + (bounds.Height - 20 * scale) / 2;
        PointF Point(float x, float y) => new(originX + x * scale, originY + y * scale);
        using var iconPen = new Pen(iconColor, Math.Max(1, scale))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        using var bracket = new GraphicsPath();
        bracket.AddLine(Point(9.5f, 3.5f), Point(5, 3.5f));
        bracket.AddBezier(Point(5, 3.5f), Point(4.17f, 3.5f), Point(3.5f, 4.17f), Point(3.5f, 5));
        bracket.AddLine(Point(3.5f, 5), Point(3.5f, 15));
        bracket.AddBezier(Point(3.5f, 15), Point(3.5f, 15.83f), Point(4.17f, 16.5f), Point(5, 16.5f));
        bracket.AddLine(Point(5, 16.5f), Point(9.5f, 16.5f));
        graphics.DrawPath(iconPen, bracket);
        graphics.DrawLines(iconPen,
        [
            Point(12.5f, 4.5f),
            Point(14.5f, 2.5f),
            Point(16.5f, 4.5f)
        ]);
        graphics.DrawLine(iconPen, Point(14.5f, 2.75f), Point(14.5f, 7.5f));
        graphics.DrawLine(iconPen, Point(14.5f, 12.5f), Point(14.5f, 17.25f));
        graphics.DrawLines(iconPen,
        [
            Point(12.5f, 15.5f),
            Point(14.5f, 17.5f),
            Point(16.5f, 15.5f)
        ]);
    }

    private static Font CreateFont(
        string? familyName,
        float size,
        FontStyle style,
        GraphicsUnit unit)
    {
        try
        {
            return new Font(
                string.IsNullOrWhiteSpace(familyName) ? "Segoe UI" : familyName,
                size,
                style,
                unit);
        }
        catch (ArgumentException)
        {
            return new Font("Segoe UI", size, style, unit);
        }
    }

    private bool IsEffectivelyCollapsed(DesktopBox box) =>
        box.ExpandOnHover && !_hoverExpandedBoxes.Contains(box.Id);

    private void ExpandHoveredBox(Guid boxId)
    {
        var box = DesktopBoxes.FirstOrDefault(candidate => candidate.Id == boxId);
        if (box is null || _hoverExpandedBoxes.Contains(boxId))
        {
            return;
        }
        var fromHeight = GetVisualBoxHeight(box);
        _hoverExpandedBoxes.Clear();
        _hoverExpandedBoxes.Add(boxId);
        _geometryDirty = true;
        StartBoxHeightAnimation(box, fromHeight);
        UpdateWindowRegion();
    }

    private void CollapseHoverExpandedBox(Guid boxId)
    {
        var box = DesktopBoxes.FirstOrDefault(candidate => candidate.Id == boxId);
        if (box is null || !_hoverExpandedBoxes.Contains(boxId))
        {
            _hoverExpandedBoxes.Remove(boxId);
            return;
        }
        var fromHeight = GetVisualBoxHeight(box);
        _hoverExpandedBoxes.Remove(boxId);
        _geometryDirty = true;
        StartBoxHeightAnimation(box, fromHeight);
        UpdateWindowRegion();
    }

    private double GetMinimumBoxWidth(DesktopBox box) =>
        DesktopItemLayoutEngine.GetMinimumBoxWidth(
            box.ViewMode,
            box.Appearance.IconSize,
            DesktopItemLayoutEngine.ScaleIconSpacing(_runtime.State.Settings.Appearance.IconHorizontalSpacing, box.Appearance.IconSize));

    private static float GetTitleRightPadding(DesktopBox box) => 92;

    private void InvalidateHeaderButton(Guid? boxId, Func<BoxGeometry, RectangleF> getBounds)
    {
        if (boxId is not { } id || _boxes.FirstOrDefault(box => box.Box.Id == id) is not { } geometry)
        {
            return;
        }
        InvalidateDip(getBounds(geometry));
    }

    private void InvalidateBoxVisualArea(Guid? boxId)
    {
        if (boxId is not { } id || DesktopBoxes.FirstOrDefault(box => box.Id == id) is not { } box)
        {
            return;
        }
        InvalidateDip(new RectangleF(
            (float)box.Bounds.X,
            (float)box.Bounds.Y,
            (float)box.Bounds.Width,
            (float)Math.Max(box.Bounds.Height, box.Appearance.TitleBarHeight)));
    }

    private void ClearAutoExpandHover()
    {
        if (_hoveredAutoExpandBoxId is not { } id)
        {
            return;
        }
        _hoveredAutoExpandBoxId = null;
        _headerToolTip.SetToolTip(this, null);
        InvalidateHeaderButton(id, box => box.AutoExpand);
    }

    private void BeginTitleEdit(DesktopBox box)
    {
        if (_editingBox is not null)
        {
            FinishTitleEdit(true);
        }
        RebuildGeometry();
        var geometry = _boxes.FirstOrDefault(candidate => candidate.Box.Id == box.Id);
        if (geometry is null)
        {
            return;
        }

        _editingBox = box;
        // GDI+ scales the drawn title with the surface transform. WinForms
        // already resolves point fonts against the monitor DPI, so applying
        // _scale here as well makes the editor text render at a different
        // size and baseline from the title it replaces.
        _titleEditorFont?.Dispose();
        _titleEditorFont = CreateFont(
            box.Appearance.TitleFontFamily,
            (float)box.Appearance.TitleFontSize,
            box.Appearance.TitleFontBold ? FontStyle.Bold : FontStyle.Regular,
            GraphicsUnit.Point);
        _titleEditor.FontFamily = new WpfMedia.FontFamily(
            ResolveTitleEditorFontFamily(box.Appearance.TitleFontFamily, box.Title));
        _titleEditor.FontSize = box.Appearance.TitleFontSize * 96d / 72d;
        _titleEditor.FontWeight = box.Appearance.TitleFontBold
            ? Wpf.FontWeights.Bold
            : Wpf.FontWeights.Regular;
        EnsureTitleEditorHandle();
        // Editing controls deliberately sit outside the box material: a box's
        // background color and opacity must never wash into typed text or its
        // selection state.
        var boxBackground = ParseOpaqueColor(box.Appearance.Background);
        _titleEditor.Background = CreateOpaqueWpfBrush(GetOpaqueTitleEditorBackColor(boxBackground));
        _titleEditor.Foreground = CreateOpaqueWpfBrush(ResolveTitleColor(box.Appearance.TitleColor, boxBackground));
        _titleEditor.Text = box.Title;
        _titleEditor.TextAlignment = box.Appearance.TitleAlignment == BoxTitleAlignment.Center
            ? Wpf.TextAlignment.Center
            : Wpf.TextAlignment.Left;
        ShowTitleEditor(geometry);
        Invalidate();
    }

    private void OnTitleEditorTextChanged(object? sender, WpfControls.TextChangedEventArgs eventArgs)
    {
        if (_editingBox is null || _titleEditorFont is null)
        {
            return;
        }

        var geometry = _boxes.FirstOrDefault(candidate => candidate.Box.Id == _editingBox.Id);
        if (geometry is not null)
        {
            LayoutTitleEditor(geometry);
        }
    }

    // A normal box drop back to the desktop is a virtual state transition,
    // not a filesystem drop. Explorer therefore reports DragDropEffects.None
    // for our private data format and would show the prohibited cursor. Keep
    // the native operation out of Explorer while giving the user a clear move
    // affordance; OnQueryContinueDrag then commits the virtual release.
    private void OnGiveFeedback(object? sender, Forms.GiveFeedbackEventArgs eventArgs)
    {
        if (!_showVirtualDesktopDropCursor ||
            eventArgs.Effect != Forms.DragDropEffects.None ||
            IsPointerOverAnyBox(Forms.Cursor.Position))
        {
            return;
        }

        eventArgs.UseDefaultCursors = false;
        Forms.Cursor.Current = Forms.Cursors.SizeAll;
    }

    private void LayoutTitleEditor(BoxGeometry geometry)
    {
        if (_titleEditorFont is null)
        {
            return;
        }

        var left = ToPixel(geometry.Header.X + 20);
        var rightPadding = GetTitleRightPadding(geometry.Box);
        var availableWidth = Math.Max(ToPixel(48), ToPixel(geometry.Header.Width - rightPadding));
        var minimumWidth = Math.Min(ToPixel(40), availableWidth);
        var text = string.IsNullOrEmpty(_titleEditor.Text) ? "M" : _titleEditor.Text;
        var measuredWidth = Forms.TextRenderer.MeasureText(
            text,
            _titleEditorFont,
            Size.Empty,
            Forms.TextFormatFlags.NoPadding | Forms.TextFormatFlags.SingleLine).Width + ToPixel(8);
        var editorWidth = Math.Clamp(measuredWidth, minimumWidth, availableWidth);
        if (geometry.Box.Appearance.TitleAlignment == BoxTitleAlignment.Center)
        {
            left += (availableWidth - editorWidth) / 2;
        }

        var editorHeight = Math.Min(
            Math.Max(20, ToPixel(geometry.Header.Height) - 10),
            Math.Max(22, _titleEditorFont.Height + 4));
        var clientBounds = new Rectangle(
            left,
            ToPixel(geometry.Header.Y + geometry.Header.Height / 2) - editorHeight / 2,
            editorWidth,
            editorHeight);
        var screenLocation = PointToScreen(clientBounds.Location);
        _titleEditorWindow.Bounds = new Rectangle(screenLocation, clientBounds.Size);
    }

    private static Color GetOpaqueTitleEditorBackColor(Color boxBackground) => boxBackground;

    private static WpfMedia.SolidColorBrush CreateOpaqueWpfBrush(Color color)
    {
        var brush = new WpfMedia.SolidColorBrush(WpfMedia.Color.FromRgb(color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    private static string ResolveTitleEditorFontFamily(string? configuredFamily, string title)
    {
        // GDI+ resolves Chinese glyphs in Segoe UI through Microsoft YaHei.
        // WPF otherwise picks the UI fallback, whose metrics and strokes differ
        // visibly from the title that the editor replaces.
        if (string.Equals(configuredFamily, "Segoe UI", StringComparison.OrdinalIgnoreCase) &&
            title.Any(character => character is >= '\u3400' and <= '\u9FFF'))
        {
            return "Microsoft YaHei";
        }

        return string.IsNullOrWhiteSpace(configuredFamily) ? "Segoe UI" : configuredFamily;
    }

    private void ResetTitleEditorHighlight()
    {
        // Start with a caret at the end, matching the desktop rename field.
        _titleEditor.Select(_titleEditor.Text.Length, 0);
    }

    private void EnsureTitleEditorHandle()
    {
        if (!_titleEditorWindow.IsDisposed && !_titleEditorWindow.IsHandleCreated)
        {
            _titleEditorWindow.CreateControl();
        }
    }

    private void ShowTitleEditor(BoxGeometry geometry)
    {
        if (_titleEditorWindow.IsDisposed)
        {
            return;
        }

        // Creating the handle before assigning the first bounds prevents
        // WinForms from reinterpreting our monitor-pixel bounds during the
        // initial per-monitor DPI negotiation.
        EnsureTitleEditorHandle();
        LayoutTitleEditor(geometry);
        _titleEditorWindow.Show();
        LayoutTitleEditor(geometry);
        _titleEditorWindow.Activate();
        _titleEditorHost.Focus();
        _titleEditor.Focus();
        DiagnosticLog.Info(
            $"Title editor shown bounds={_titleEditorWindow.Bounds} " +
            $"editorFocused={_titleEditor.IsKeyboardFocused}");

        // ElementHost can finish its first WPF measure pass after Show().
        // Reapply the bounds on the UI queue so the first edit has the same
        // compact geometry as every subsequent edit.
        if (IsHandleCreated)
        {
            var editingBoxId = geometry.Box.Id;
            BeginInvoke((Action)(() =>
            {
                if (_resourcesDisposed || _editingBox?.Id != editingBoxId || !_titleEditorWindow.Visible)
                {
                    return;
                }

                var currentGeometry = _boxes.FirstOrDefault(candidate => candidate.Box.Id == editingBoxId);
                if (currentGeometry is not null)
                {
                    LayoutTitleEditor(currentGeometry);
                }
            }));
        }
    }

    private void OnTitleEditorWindowDeactivate(object? sender, EventArgs eventArgs)
    {
        if (_resourcesDisposed || _editingBox is null || !_titleEditorWindow.Visible)
        {
            return;
        }

        FinishTitleEdit(true);
    }

    private void OnTitleEditorKeyDown(object? sender, WpfInput.KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == WpfInput.Key.Enter)
        {
            eventArgs.Handled = true;
            FinishTitleEdit(true);
        }
        else if (eventArgs.Key == WpfInput.Key.Escape)
        {
            eventArgs.Handled = true;
            FinishTitleEdit(false);
        }
    }

    private void FinishTitleEdit(bool commit)
    {
        if (_editingBox is not { } box)
        {
            return;
        }
        var title = _titleEditor.Text.Trim();
        _editingBox = null;
        _titleEditorWindow.Hide();
        if (commit && title.Length > 0 && !string.Equals(title, box.Title, StringComparison.Ordinal))
        {
            box.Title = title;
            _runtime.BoxChanged(box, true);
        }
        else
        {
            Invalidate();
        }
    }

    private void ToggleBoxDisplayMode(DesktopBox box) =>
        SetBoxDisplayMode(box, !box.ExpandOnHover);

    private void SetBoxDisplayMode(DesktopBox box, bool expandOnHover)
    {
        FinishTitleEdit(true);
        if (box.ExpandOnHover == expandOnHover && box.IsCollapsed == expandOnHover)
        {
            return;
        }

        var fromHeight = GetVisualBoxHeight(box);
        var previouslyExpandedBoxIds = _hoverExpandedBoxes.ToArray();
        _hoverExpansion.Reset();
        foreach (var expandedBoxId in previouslyExpandedBoxIds.Where(id => id != box.Id))
        {
            CollapseHoverExpandedBox(expandedBoxId);
        }
        _hoverExpandedBoxes.Remove(box.Id);

        box.ExpandOnHover = expandOnHover;
        // IsCollapsed is retained in the persisted shape for compatibility,
        // but it is derived from the display mode rather than user-controlled.
        box.IsCollapsed = expandOnHover;
        _geometryDirty = true;
        StartBoxHeightAnimation(box, fromHeight);
        UpdateWindowRegion();
        _runtime.BoxChanged(box);

        foreach (var boxId in previouslyExpandedBoxIds)
        {
            InvalidateBoxVisualArea(boxId);
        }
        InvalidateBoxVisualArea(box.Id);
    }

    private void PrepareBoxTransform(DesktopBox box)
    {
        _transformDirtyBounds = ToVisualBounds(box, box.Bounds);
        _heightAnimations.Remove(box.Id);
        _geometryDirty = true;
        if (_heightAnimations.Count == 0)
        {
            _animationTimer.Stop();
        }
    }

    private void StartBoxHeightAnimation(DesktopBox box, double fromHeight)
    {
        var targetHeight = IsEffectivelyCollapsed(box)
            ? box.Appearance.TitleBarHeight
            : box.Bounds.Height;
        if (!_runtime.State.Settings.Appearance.AnimationEnabled || Math.Abs(targetHeight - fromHeight) < 0.5)
        {
            _heightAnimations.Remove(box.Id);
            return;
        }
        _heightAnimations[box.Id] = new BoxHeightAnimation(
            fromHeight,
            targetHeight,
            DateTimeOffset.UtcNow,
            TimeSpan.FromMilliseconds(BoxHeightAnimationMilliseconds));
        _dynamicVisualVersion++;
        _animationTimer.Start();
    }

    private double GetVisualBoxHeight(DesktopBox box)
    {
        var targetHeight = IsEffectivelyCollapsed(box)
            ? box.Appearance.TitleBarHeight
            : box.Bounds.Height;
        if (!_runtime.State.Settings.Appearance.AnimationEnabled)
        {
            _heightAnimations.Remove(box.Id);
            return targetHeight;
        }
        if (!_heightAnimations.TryGetValue(box.Id, out var animation))
        {
            return targetHeight;
        }
        if (Math.Abs(animation.ToHeight - targetHeight) > 0.5)
        {
            _heightAnimations.Remove(box.Id);
            return targetHeight;
        }
        var progress = (DateTimeOffset.UtcNow - animation.StartedAt).TotalMilliseconds /
            animation.Duration.TotalMilliseconds;
        return progress >= 1
            ? animation.ToHeight
            : AnimationMath.Interpolate(animation.FromHeight, animation.ToHeight, progress);
    }

    private void OnAnimationTick(object? sender, EventArgs eventArgs)
    {
        var now = DateTimeOffset.UtcNow;
        var animatedBoxIds = _heightAnimations.Keys.ToArray();
        foreach (var id in _heightAnimations
            .Where(pair => now - pair.Value.StartedAt >= pair.Value.Duration)
            .Select(pair => pair.Key)
            .ToArray())
        {
            _heightAnimations.Remove(id);
        }
        if (_heightAnimations.Count == 0)
        {
            _animationTimer.Stop();
        }
        _geometryDirty = true;
        UpdateWindowRegion();
        foreach (var id in animatedBoxIds)
        {
            InvalidateBoxVisualArea(id);
        }
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

    private static Color BrightenColor(Color color, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        return Color.FromArgb(
            color.A,
            (int)Math.Round(color.R + (255 - color.R) * amount),
            (int)Math.Round(color.G + (255 - color.G) * amount),
            (int)Math.Round(color.B + (255 - color.B) * amount));
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
