using System.Collections.Specialized;
using System.Drawing;
using System.Drawing.Drawing2D;
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
    private const string ItemKeysFormat = "CrabDesk.DesktopItemKeys";
    private const string SourceBoxFormat = "CrabDesk.SourceBoxId";
    private const string DragSessionFormat = "CrabDesk.InternalDragSession";
    private const int WmNcHitTest = 0x0084;
    private const int WmMouseActivate = 0x0021;
    private const int WmContextMenu = 0x007B;
    private const int WsClipSiblings = 0x04000000;
    private const int ExplorerHoverFillAlpha = 32;
    private const int ExplorerHoverBorderAlpha = 52;
    private const int HoverExpansionDelayMilliseconds = 120;
    private const int HoverCollapseDelayMilliseconds = 180;
    private const int HoverPollingIntervalMilliseconds = 25;
    private const int BoxHeightAnimationMilliseconds = 140;
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
    private readonly Dictionary<Guid, double> _scrollOffsets = [];
    private readonly Dictionary<Guid, IReadOnlyList<DesktopItemRef>> _boxItems = [];
    private readonly Dictionary<Guid, BoxHeightAnimation> _heightAnimations = [];
    private readonly List<BoxGeometry> _boxes = [];
    private readonly List<ItemGeometry> _items = [];
    private IReadOnlyList<LayoutRect> _lastWindowRegionRectangles = [];
    private readonly Forms.Timer _animationTimer;
    private readonly Forms.Timer _hoverTimer;
    private readonly Forms.ToolTip _headerToolTip;
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
    private Guid? _pressedBoxId;
    private LayoutRect _startBounds;
    private PointF _pressPoint;
    private PointF _selectionStart;
    private RectangleF _selectionRectangle;
    private bool _dragStarted;
    private bool _dragDropCommitted;
    private bool _dragCancelled;
    private bool _showVirtualDesktopDropCursor;
    private string? _hoveredItemKey;
    private Guid? _hoveredAutoExpandBoxId;
    private LayoutRect? _transformDirtyBounds;
    private string? _lastRegionDiagnostic;
    private int _iconCacheVersion;
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
        Region = new Region(new Rectangle(0, 0, 0, 0));
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
        _titleEditorHost = new FormsIntegration.ElementHost
        {
            Visible = false,
            Margin = Forms.Padding.Empty,
            Child = _titleEditor
        };
        _titleEditor.KeyDown += OnTitleEditorKeyDown;
        _titleEditor.TextChanged += OnTitleEditorTextChanged;
        Controls.Add(_titleEditorHost);

        MouseDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeave += OnMouseLeave;
        MouseUp += OnMouseUp;
        MouseDoubleClick += OnMouseDoubleClick;
        MouseWheel += OnMouseWheel;
        DragEnter += OnDragOver;
        DragOver += OnDragOver;
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
            // DefWindowProc forwards this child-window message to SHELLDLL_DefView.
            // CrabDesk menus are opened explicitly from OnMouseDown instead.
            message.Result = IntPtr.Zero;
            return;
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
        RebuildBoxItemCache();
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
        Invalidate();
        return true;
    }

    internal bool UpdateInteractionRegion()
    {
        if (!UpdateWindowRegion())
        {
            return false;
        }
        Invalidate();
        return true;
    }

    internal bool ValidateWindowRegion() =>
        IsHandleCreated && DesktopWindowTools.VerifyRegion(
            Handle,
            _lastWindowRegionRectangles,
            _scale,
            out _);

    protected override void OnPaintBackground(Forms.PaintEventArgs eventArgs)
    {
    }

    protected override void OnPaint(Forms.PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        _paintCount++;
        var graphics = eventArgs.Graphics;
        graphics.CompositingMode = CompositingMode.SourceOver;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.ScaleTransform((float)_scale, (float)_scale);
        RebuildGeometry();
        var clipBounds = new RectangleF(
            (float)(eventArgs.ClipRectangle.X / _scale),
            (float)(eventArgs.ClipRectangle.Y / _scale),
            (float)(eventArgs.ClipRectangle.Width / _scale),
            (float)(eventArgs.ClipRectangle.Height / _scale));
        clipBounds.Inflate(8, 8);
        foreach (var box in _boxes.Where(box => box.Bounds.IntersectsWith(clipBounds)))
        {
            DrawBox(graphics, box, clipBounds);
        }
        graphics.ResetTransform();
    }

    internal void RequestRender()
    {
        Refresh();
        if (IsHandleCreated)
        {
            var ex = DesktopWindowTools.GetSurfaceExtendedStyle(Handle);
            DiagnosticLog.Info(
                $"Surface exstyle=0x{ex:X} " +
                $"layered={(ex & 0x80000) != 0} transparent={(ex & 0x20) != 0}");
        }
    }

    internal int PaintCount => _paintCount;

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
            ClearIconCache();
            _iconLoadCancellation.Dispose();
            _shellContextMenu?.Dispose();
            _shellContextMenu = null;
            _titleEditorFont?.Dispose();
            _titleEditorFont = null;
            _titleEditorHost.Dispose();
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

    private bool UpdateWindowRegion()
    {
        var desktopBoxes = DesktopBoxes.ToArray();
        var currentRectangles = desktopBoxes.Select(box => new LayoutRect(
            box.Bounds.X,
            box.Bounds.Y,
            box.Bounds.Width,
            GetVisualBoxHeight(box))).ToArray();
        if (IsHandleCreated)
        {
            if (!DesktopWindowTools.ApplyRegion(
                Handle,
                currentRectangles,
                _scale,
                out var regionDiagnostic,
                redraw: true))
            {
                HandleRegionFailure(regionDiagnostic);
                return false;
            }
            // While a box is being dragged or resized the whole parent tree
            // is redrawn once on mouse release (FlushTransformTrail). Doing
            // it here per pointer move floods Explorer's desktop view with
            // full-tree erase/redraw and can trigger GPU watchdog timeouts.
            if (_lastWindowRegionRectangles.Count > 0 &&
                _movingBox is null &&
                _resizingBox is null)
            {
                DesktopWindowTools.RedrawExposedParentArea(
                    Handle,
                    _lastWindowRegionRectangles,
                    currentRectangles,
                    _scale);
            }
        }
        _lastWindowRegionRectangles = currentRectangles;

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
        foreach (var box in DesktopBoxes)
        {
            var titleBarHeight = (float)box.Appearance.TitleBarHeight;
            var height = (float)GetVisualBoxHeight(box);
            var isCollapsed = IsEffectivelyCollapsed(box);
            var bounds = new RectangleF((float)box.Bounds.X, (float)box.Bounds.Y, (float)box.Bounds.Width, (float)height);
            var geometry = new BoxGeometry(
                box,
                isCollapsed,
                bounds,
                new RectangleF(bounds.X, bounds.Y, bounds.Width, titleBarHeight),
                new RectangleF(bounds.X + 8, bounds.Y + titleBarHeight + 8, bounds.Width - 16, Math.Max(0, bounds.Height - titleBarHeight - 16)),
                new RectangleF(bounds.Right - (box.Appearance.ShowCollapseButton ? 92 : 62), bounds.Y + (titleBarHeight - 28) / 2, 26, 28),
                new RectangleF(bounds.Right - 62, bounds.Y + (titleBarHeight - 28) / 2, 26, 28),
                new RectangleF(bounds.Right - 32, bounds.Y + (titleBarHeight - 28) / 2, 26, 28),
                new RectangleF(bounds.Right - 18, bounds.Bottom - 18, 18, 18));
            _boxes.Add(geometry);
            if (!isCollapsed)
            {
                BuildItemGeometry(geometry);
            }
        }
    }

    private void BuildItemGeometry(BoxGeometry geometry)
    {
        if (_runtime.AreDesktopItemsHidden)
        {
            return;
        }
        var items = GetCachedItemsForBox(geometry.Box.Id);
        var appearance = _runtime.State.Settings.Appearance;
        var layout = DesktopItemLayoutEngine.CalculateVisible(
            geometry.Box.ViewMode,
            new LayoutRect(geometry.Body.X, geometry.Body.Y, geometry.Body.Width, geometry.Body.Height),
            items.Count,
            geometry.Box.Appearance.IconSize,
            appearance.IconHorizontalSpacing,
            appearance.IconVerticalSpacing,
            _scrollOffsets.GetValueOrDefault(geometry.Box.Id));
        _scrollOffsets[geometry.Box.Id] = layout.ScrollOffset;
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

    private void DrawBox(Graphics graphics, BoxGeometry geometry, RectangleF clipBounds)
    {
        var isDark = _runtime.IsDarkTheme;
        var baseColor = ParseOpaqueColor(geometry.Box.Appearance.Background);
        var opacity = Math.Clamp(geometry.Box.Appearance.Opacity, 0.35, 1);
        var boxColor = ApplyOpacity(isDark ? baseColor : Blend(baseColor, Color.White, 0.88f), opacity);
        var textColor = isDark ? Color.White : Color.FromArgb(31, 35, 41);
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
        using var titleBrush = new SolidBrush(ResolveTitleColor(geometry.Box.Appearance.TitleColor, isDark));
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
        if (geometry.Box.Appearance.ShowCollapseButton)
        {
            DrawChevron(graphics, geometry.Collapse, geometry.IsCollapsed, textColor);
        }
        DrawAutoExpandButton(
            graphics,
            geometry.AutoExpand,
            geometry.Box.ExpandOnHover,
            _hoveredAutoExpandBoxId == geometry.Box.Id,
            ParseOpaqueColor(geometry.Box.Appearance.Accent),
            textColor,
            isDark);
        DrawMenuIcon(graphics, geometry.Menu, textColor);

        if (geometry.IsCollapsed)
        {
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
            ? new SolidBrush(_runtime.IsDarkTheme ? Color.White : Color.FromArgb(31, 35, 41))
            : null;
        using var itemFormat = geometry.Box.Appearance.ShowItemLabels
            ? CreateItemTextFormat(geometry.Box.ViewMode)
            : null;
        foreach (var item in _items.Where(item =>
                     item.Box.Id == geometry.Box.Id && item.Bounds.IntersectsWith(clipBounds)))
        {
            DrawItem(graphics, item, itemFont, itemBrush, itemFormat);
        }
        if (!_runtime.AreDesktopItemsHidden && geometry.Box.IsMappedFolder &&
            !_items.Any(item => item.Box.Id == geometry.Box.Id))
        {
            DrawMappedFolderState(graphics, geometry);
        }
        if (_selectionBox?.Id == geometry.Box.Id && !_selectionRectangle.IsEmpty)
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

        if (_runtime.State.Settings.Appearance.ShowResizeGrip)
        {
            using var grip = new Pen(isDark
                ? Color.FromArgb(130, 255, 255, 255)
                : Color.FromArgb(130, 64, 70, 78), 1);
            graphics.DrawLine(grip, geometry.Resize.Right - 10, geometry.Resize.Bottom - 3, geometry.Resize.Right - 3, geometry.Resize.Bottom - 10);
            graphics.DrawLine(grip, geometry.Resize.Right - 6, geometry.Resize.Bottom - 3, geometry.Resize.Right - 3, geometry.Resize.Bottom - 6);
        }
    }

    private void DrawMappedFolderState(Graphics graphics, BoxGeometry geometry)
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
        using var font = new Font("Segoe UI", 9, FontStyle.Regular, GraphicsUnit.Point);
        using var brush = new SolidBrush(_runtime.IsDarkTheme
            ? Color.FromArgb(182, 205, 211, 220)
            : Color.FromArgb(138, 75, 82, 91));
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
        StringFormat? labelFormat)
    {
        var itemKey = item.Item.Key.ToString();
        var isSelected = _selection.Contains(itemKey);
        if (isSelected)
        {
            var configuredSelection = ParseOpaqueColor(_runtime.State.Settings.Appearance.SelectionColor);
            using var selected = new SolidBrush(_runtime.IsDarkTheme
                ? Blend(configuredSelection, Color.Black, 0.18f)
                : Blend(configuredSelection, Color.White, 0.68f));
            using var selectedPath = RoundedRectangle(RectangleF.Inflate(item.Bounds, -2, -2), 4);
            graphics.FillPath(selected, selectedPath);
        }
        else if (_runtime.State.Settings.Appearance.HoverFeedback &&
            string.Equals(_hoveredItemKey, itemKey, StringComparison.OrdinalIgnoreCase))
        {
            // Explorer uses a neutral translucent white hot-track surface on
            // desktop items. Keep the configured accent for selection only so
            // icons inside and outside a box share the same hover treatment.
            using var hovered = new SolidBrush(Color.FromArgb(ExplorerHoverFillAlpha, Color.White));
            using var hoverBorder = new Pen(Color.FromArgb(ExplorerHoverBorderAlpha, Color.White), 1);
            using var hoveredPath = RoundedRectangle(RectangleF.Inflate(item.Bounds, -1, -1), 4);
            graphics.FillPath(hovered, hoveredPath);
            graphics.DrawPath(hoverBorder, hoveredPath);
        }

        var iconSize = (float)item.Box.Appearance.IconSize;
        var iconBounds = item.Box.ViewMode == BoxViewMode.List
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
        var bitmap = GetIconBitmap(item.Item, iconSize) ?? ShellIconProvider.GetGenericFileIcon();
        if (bitmap is not null)
        {
            graphics.DrawImage(bitmap, iconBounds);
        }
        if (!item.Box.Appearance.ShowItemLabels)
        {
            return;
        }
        RectangleF textBounds;
        if (item.Box.ViewMode == BoxViewMode.List)
        {
            textBounds = new RectangleF(
                iconBounds.Right + 10,
                item.Bounds.Y,
                Math.Max(0, item.Bounds.Right - iconBounds.Right - 18),
                item.Bounds.Height);
        }
        else
        {
            textBounds = new RectangleF(
                item.Bounds.X + 2,
                iconBounds.Bottom + 3,
                item.Bounds.Width - 4,
                item.Bounds.Height - iconSize - 8);
        }
        graphics.DrawString(item.Item.DisplayName, labelFont!, labelBrush!, textBounds, labelFormat!);
    }

    private static StringFormat CreateItemTextFormat(BoxViewMode viewMode) => new()
    {
        Alignment = viewMode == BoxViewMode.List ? StringAlignment.Near : StringAlignment.Center,
        LineAlignment = viewMode == BoxViewMode.List ? StringAlignment.Center : StringAlignment.Near,
        Trimming = StringTrimming.EllipsisCharacter,
        FormatFlags = viewMode == BoxViewMode.List ? StringFormatFlags.NoWrap : 0
    };

    private Bitmap? GetIconBitmap(DesktopItemRef item, float iconSize)
    {
        var key = CreateIconBitmapKey(item, iconSize);
        if (_iconCache.TryGetValue(key, out var bitmap))
        {
            return bitmap;
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
                .Select(item => CreateIconBitmapKey(item, (float)box.Appearance.IconSize)))
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

    private IconBitmapKey CreateIconBitmapKey(DesktopItemRef item, float iconSize)
    {
        return new IconBitmapKey(
            item.ParsingName,
            Math.Clamp((int)Math.Round(iconSize * _scale), 16, 256),
            item.ModifiedAt?.UtcDateTime.Ticks ?? 0,
            0);
    }

    private void InvalidateIcon(IconBitmapKey key)
    {
        foreach (var item in _items.Where(candidate =>
                     string.Equals(candidate.Item.ParsingName, key.ParsingName, StringComparison.OrdinalIgnoreCase) &&
                     Math.Clamp((int)Math.Round(candidate.Box.Appearance.IconSize * _scale), 16, 256) == key.PixelSize))
        {
            InvalidateDip(item.Bounds);
        }
    }

    private void InvalidateItem(ItemGeometry? item)
    {
        if (item is not null)
        {
            InvalidateDip(item.Bounds);
        }
    }

    private void InvalidateDip(RectangleF bounds)
    {
        bounds.Inflate(4, 4);
        Invalidate(new Rectangle(
            ToPixel(bounds.X),
            ToPixel(bounds.Y),
            Math.Max(1, ToPixel(bounds.Width)),
            Math.Max(1, ToPixel(bounds.Height))));
    }

    private void OnMouseDown(object? sender, Forms.MouseEventArgs eventArgs)
    {
        if (_editingBox is not null)
        {
            FinishTitleEdit(true);
        }
        RebuildGeometry();
        var point = ToDip(eventArgs.Location);
        var box = _boxes.LastOrDefault(candidate => candidate.Bounds.Contains(point));
        if (box is not null && _runtime.BringBoxToFront(box.Box.Id))
        {
            RebuildGeometry();
            box = _boxes.LastOrDefault(candidate => candidate.Bounds.Contains(point));
        }
        var item = box is null
            ? null
            : _items.LastOrDefault(candidate =>
                candidate.Box.Id == box.Box.Id && candidate.Bounds.Contains(point));
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
        if (item is not null)
        {
            var key = item.Item.Key.ToString();
            if ((Forms.Control.ModifierKeys & Forms.Keys.Control) != 0 && _selection.Contains(key))
            {
                _selection.Remove(key);
                _pressedItem = null;
                _pressedBoxId = null;
                Invalidate();
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
            return;
        }
        if (box is null)
        {
            return;
        }
        _startBounds = box.Box.Bounds;
        if (box.AutoExpand.Contains(point))
        {
            ToggleAutoExpand(box.Box);
            return;
        }
        if (box.Box.Appearance.ShowCollapseButton && box.Collapse.Contains(point))
        {
            ToggleBoxCollapsed(box.Box);
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
            _selectionStart = point;
            _selectionRectangle = RectangleF.Empty;
            _selectionBase.Clear();
            if ((Forms.Control.ModifierKeys & Forms.Keys.Control) != 0)
            {
                _selectionBase.UnionWith(_selection);
            }
            else
            {
                _selection.Clear();
            }
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
        UpdatePointerCursor(point);
        UpdateHoverState(point);
        if (_selectionBox is not null)
        {
            var geometry = _boxes.FirstOrDefault(box => box.Box.Id == _selectionBox.Id);
            if (geometry is null)
            {
                return;
            }
            var raw = RectangleFromPoints(_selectionStart, point);
            _selectionRectangle = RectangleF.Intersect(raw, geometry.Body);
            _selection.Clear();
            _selection.UnionWith(_selectionBase);
            var selectionBounds = new LayoutRect(
                _selectionRectangle.X,
                _selectionRectangle.Y,
                _selectionRectangle.Width,
                _selectionRectangle.Height);
            foreach (var candidate in _items.Where(candidate =>
                candidate.Box.Id == _selectionBox.Id &&
                new LayoutRect(candidate.Bounds.X, candidate.Bounds.Y, candidate.Bounds.Width, candidate.Bounds.Height)
                    .Intersects(selectionBounds)))
            {
                _selection.Add(candidate.Item.Key.ToString());
            }
            Invalidate();
            return;
        }
        if (_pressedItem is null || eventArgs.Button != Forms.MouseButtons.Left || _dragStarted)
        {
            return;
        }
        if (Math.Abs(point.X - _pressPoint.X) < 4 && Math.Abs(point.Y - _pressPoint.Y) < 4)
        {
            return;
        }
        _dragStarted = true;
        if (_pressedBoxId is not { } sourceBoxId)
        {
            return;
        }
        var selected = GetCachedItemsForBox(sourceBoxId)
            .Where(candidate => _selection.Contains(candidate.Key.ToString()))
            .ToArray();
        var data = new Forms.DataObject();
        var itemKeys = selected.Select(candidate => candidate.Key.ToString()).ToArray();
        var dragSession = new InternalDragSession();
        data.SetData(ItemKeysFormat, itemKeys);
        data.SetData(SourceBoxFormat, sourceBoxId.ToString("D"));
        data.SetData(DragSessionFormat, false, dragSession);
        var sourceMapped = _runtime.State.Boxes.FirstOrDefault(box => box.Id == sourceBoxId)?.IsMappedFolder == true;
        var paths = selected.Where(candidate => candidate.FileSystemPath is not null).Select(candidate => candidate.FileSystemPath!).ToArray();
        if (paths.Length > 0 && BoxDragCompletionPolicy.ShouldExposeFileDrop(sourceMapped))
        {
            var collection = new StringCollection();
            collection.AddRange(paths);
            data.SetFileDropList(collection);
        }
        _dragDropCommitted = false;
        _dragCancelled = false;
        _showVirtualDesktopDropCursor = !sourceMapped;
        try
        {
            DoDragDrop(data, Forms.DragDropEffects.Move | Forms.DragDropEffects.Copy);
        }
        finally
        {
            _showVirtualDesktopDropCursor = false;
            Forms.Cursor.Current = Forms.Cursors.Default;
        }
        if (BoxDragCompletionPolicy.ShouldUnassign(
                _dragDropCommitted,
                _dragCancelled,
                dragSession.HandledByBox,
                sourceMapped,
                IsPointerOverAnyBox(Forms.Cursor.Position)))
        {
            _ = ReleaseBoxItemsToDesktopAsync(itemKeys, Forms.Cursor.Position);
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
                    box.IsCollapsed ? box.Appearance.TitleBarHeight : box.Bounds.Height).Contains(x, y));
        }
        return false;
    }

    private void OnMouseLeave(object? sender, EventArgs eventArgs)
    {
        if (_movingBox is not null || _resizingBox is not null)
        {
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
            InvalidateItem(previousHoveredItem);
        }
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
                    enabled ? "关闭悬停自动展开" : "开启悬停自动展开");
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
            box.Menu.Contains(point) ||
            (box.Box.Appearance.ShowCollapseButton && box.Collapse.Contains(point))) is not null;
        Cursor = resizeEdges switch
        {
            ResizeEdges.Left or ResizeEdges.Right => Forms.Cursors.SizeWE,
            ResizeEdges.Top or ResizeEdges.Bottom => Forms.Cursors.SizeNS,
            ResizeEdges.TopLeft or ResizeEdges.BottomRight => Forms.Cursors.SizeNWSE,
            ResizeEdges.TopRight or ResizeEdges.BottomLeft => Forms.Cursors.SizeNESW,
            _ => isHeaderButton ? Forms.Cursors.Hand : Forms.Cursors.Default
        };
    }

    private void OnHoverTimer(object? sender, EventArgs eventArgs)
    {
        try
        {
            if (_movingBox is not null || _resizingBox is not null)
            {
                return;
            }
            if (!DesktopBoxes.Any(box => box.ExpandOnHover) && _hoverExpandedBoxes.Count == 0)
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

    private void ClearHoverState()
    {
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
            hoveredItem = _items.LastOrDefault(candidate => candidate.Bounds.Contains(point));
            var itemKey = hoveredItem?.Item.Key.ToString();
            hoverChanged = !string.Equals(_hoveredItemKey, itemKey, StringComparison.OrdinalIgnoreCase);
            _hoveredItemKey = itemKey;
        }

        var structureChanged = false;
        var collapsedHeaderBoxId = _boxes.LastOrDefault(box =>
            box.Box.IsCollapsed &&
            box.Box.ExpandOnHover &&
            box.Header.Contains(point) &&
            !box.AutoExpand.Contains(point) &&
            !box.Menu.Contains(point) &&
            !(box.Box.Appearance.ShowCollapseButton && box.Collapse.Contains(point)))?.Box.Id;
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
            InvalidateBoxVisualArea(transition.CollapsedBoxId);
            InvalidateBoxVisualArea(transition.ExpandedBoxId);
        }
        else if (hoverChanged)
        {
            InvalidateItem(previousHoveredItem);
            InvalidateItem(hoveredItem);
        }
    }

    private void OnMouseUp(object? sender, Forms.MouseEventArgs eventArgs)
    {
        DiagnosticLog.Info(
            $"Surface mouse up monitor={_monitor.Id} button={eventArgs.Button} moving={_movingBox is not null} resizing={_resizingBox is not null} selecting={_selectionBox is not null}");
        if (_selectionBox is not null)
        {
            _selectionBox = null;
            _selectionBase.Clear();
            _selectionRectangle = RectangleF.Empty;
            Capture = false;
            Invalidate();
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
            _selectionBox = null;
            _selectionBase.Clear();
            _selectionRectangle = RectangleF.Empty;
            Invalidate();
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
        _movingBox = null;
        _resizingBox = null;
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

        UpdateWindowRegion();
        FlushTransformTrail();
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
            SnapDipToPixel(LayoutGrid.Snap(_startBounds.X + point.X - _pressPoint.X)),
            SnapDipToPixel(LayoutGrid.Snap(_startBounds.Y + point.Y - _pressPoint.Y)),
            _startBounds.Width,
            _startBounds.Height).Clamp(
                new LayoutRect(0, 0, _monitor.WorkArea.Width, _monitor.WorkArea.Height),
                GetMinimumBoxWidth(box));
        ApplyBoxTransform(box, nextBounds);
    }

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
        const double minHeight = 120;
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
            _runtime.State.Settings.Appearance.IconHorizontalSpacing);
        var heightSlot = DesktopItemLayoutEngine.SnapBoxHeight(
            box.ViewMode,
            requestedHeight,
            box.Appearance.TitleBarHeight,
            box.Appearance.IconSize,
            _runtime.State.Settings.Appearance.IconVerticalSpacing);
        const double snapThreshold = 14;
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
        UpdateWindowRegion();
        Invalidate();
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
        if (_transformDirtyBounds is not { } dirty || !IsHandleCreated)
        {
            _transformDirtyBounds = null;
            return;
        }
        _transformDirtyBounds = null;
        DesktopWindowTools.RedrawExposedParentArea(
            Handle,
            [dirty],
            _lastWindowRegionRectangles,
            _scale,
            updateNow: true);
        Update();
    }

    private void OnMouseDoubleClick(object? sender, Forms.MouseEventArgs eventArgs)
    {
        if (eventArgs.Button != Forms.MouseButtons.Left)
        {
            return;
        }
        var point = ToDip(eventArgs.Location);
        var box = _boxes.LastOrDefault(candidate => candidate.Bounds.Contains(point));
        var item = box is null
            ? null
            : _items.LastOrDefault(candidate =>
                candidate.Box.Id == box.Box.Id && candidate.Bounds.Contains(point));
        DiagnosticLog.Info(
            $"Surface double click monitor={_monitor.Id} x={point.X:0} y={point.Y:0} box={box?.Box.Id} itemKind={item?.Item.Key.Kind}");
        if (item is not null)
        {
            TryAction(() => _runtime.FileOperations.Open(item.Item));
            return;
        }
        if (box is not null &&
            box.Header.Contains(point) &&
            !box.Menu.Contains(point) &&
            !box.AutoExpand.Contains(point) &&
            !(box.Box.Appearance.ShowCollapseButton && box.Collapse.Contains(point)))
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
        _scrollOffsets[box.Box.Id] = Math.Max(0, _scrollOffsets.GetValueOrDefault(box.Box.Id) - eventArgs.Delta / 3d);
        Invalidate();
    }

    private void OnDragOver(object? sender, Forms.DragEventArgs eventArgs)
    {
        var point = ToDip(PointToClient(new Point(eventArgs.X, eventArgs.Y)));
        RebuildGeometry();
        var target = _boxes.LastOrDefault(candidate => candidate.Bounds.Contains(point))?.Box;
        if (target is null || target.MappedFolder?.IsReadOnly == true)
        {
            eventArgs.Effect = Forms.DragDropEffects.None;
            return;
        }
        var effect = ResolveTransferEffect(eventArgs, target);
        // A desktop file dropped into a normal box is a virtual assignment,
        // not a filesystem move. Advertising Move makes Explorer dim the
        // source icon as a cut operation until its delayed shell refresh.
        eventArgs.Effect = IsDesktopVirtualAssignment(eventArgs, target)
            ? Forms.DragDropEffects.Copy
            : ToDragDropEffects(effect);
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
                var beforeKey = _items.LastOrDefault(candidate =>
                    candidate.Box.Id == box.Box.Id && candidate.Bounds.Contains(point))?.Item.Key.ToString();
                _runtime.ReorderBoxItems(box.Box.Id, keys, beforeKey);
                return;
            }
            try
            {
                await _runtime.TransferBoxItemsAsync(
                    sourceBoxId,
                    keys,
                    box.Box.Id,
                    transferEffect == BoxTransferEffect.MoveFiles);
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
                await _runtime.ImportFilesToBoxAsync(
                    paths,
                    box.Box.Id,
                    transferEffect == BoxTransferEffect.MoveFiles);
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
        _runtime.AssignItems(assignedKeys, box.Box.Id);
        if (external.Count > 0)
        {
            await _runtime.ImportFilesAsync(
                external,
                box.Box.Id,
                transferEffect == BoxTransferEffect.MoveFiles);
        }
        // Assigned desktop icons are parked outside the visible work area by
        // the runtime; no per-drop Explorer move is needed here.
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
        if (eventArgs.Data.GetDataPresent(SourceBoxFormat) &&
            eventArgs.Data.GetData(SourceBoxFormat) is string sourceValue &&
            Guid.TryParse(sourceValue, out var parsedSourceId))
        {
            sourceId = parsedSourceId;
            sourceMapped = _runtime.State.Boxes.FirstOrDefault(box => box.Id == parsedSourceId)?.IsMappedFolder == true;
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
            (eventArgs.KeyState & controlKeyState) != 0);
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
        menu.Items.Add("重命名", null, (_, _) =>
        {
            BeginInvoke((Action)(() => BeginTitleEdit(box)));
        });
        menu.Items.Add(box.IsCollapsed ? "展开" : "折叠", null, (_, _) =>
        {
            ToggleBoxCollapsed(box);
        });
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
        menu.Items.Add("删除盒子", null, (_, _) =>
        {
            var detail = box.IsMappedFolder
                ? "不会删除映射文件夹或其中的文件。"
                : "盒子中的文件仍保留在桌面。";
            if (!_runtime.State.Settings.ConfirmDeleteBox || DesktopConfirmationDialog.Show(
                    this,
                    _runtime.IsDarkTheme,
                    $"删除“{box.Title}”？",
                    detail,
                    "删除盒子"))
            {
                _runtime.DeleteBox(box);
            }
        });
        return menu;
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

        var session = ShellContextMenuSession.TryCreate(
                selectedItems.Select(candidate => candidate.ParsingName),
                Handle)
            ?? ShellContextMenuSession.TryCreate([item.ParsingName], Handle);
        if (session is null)
        {
            return;
        }
        _shellContextMenu = session;
        try
        {
            var screenPoint = PointToScreen(location);
            session.Show(Handle, screenPoint.X, screenPoint.Y);
        }
        finally
        {
            _shellContextMenu = null;
            session.Dispose();
        }
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

    private static void DrawChevron(Graphics graphics, RectangleF bounds, bool pointsDown, Color color)
    {
        var centerX = bounds.Left + bounds.Width / 2;
        var centerY = bounds.Top + bounds.Height / 2;
        const float halfWidth = 4;
        const float halfHeight = 2.25f;
        var edgeY = pointsDown ? centerY - halfHeight : centerY + halfHeight;
        var tipY = pointsDown ? centerY + halfHeight : centerY - halfHeight;
        using var pen = new Pen(color, 1.6f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        graphics.DrawLines(pen,
        [
            new PointF(centerX - halfWidth, edgeY),
            new PointF(centerX, tipY),
            new PointF(centerX + halfWidth, edgeY)
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
        box.IsCollapsed && !_hoverExpandedBoxes.Contains(box.Id);

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
        StartBoxHeightAnimation(box, fromHeight);
        UpdateWindowRegion();
    }

    private double GetMinimumBoxWidth(DesktopBox box) =>
        DesktopItemLayoutEngine.GetMinimumBoxWidth(
            box.ViewMode,
            box.Appearance.IconSize,
            _runtime.State.Settings.Appearance.IconHorizontalSpacing);

    private static float GetTitleRightPadding(DesktopBox box) =>
        box.Appearance.ShowCollapseButton ? 122 : 92;

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
        // Editing controls deliberately sit outside the box material: a box's
        // background color and opacity must never wash into typed text or its
        // selection state.
        _titleEditor.Background = CreateOpaqueWpfBrush(GetOpaqueTitleEditorBackColor());
        _titleEditor.Foreground = CreateOpaqueWpfBrush(ResolveTitleColor(box.Appearance.TitleColor, _runtime.IsDarkTheme));
        _titleEditor.Text = box.Title;
        LayoutTitleEditor(geometry);
        _titleEditorHost.Visible = true;
        _titleEditorHost.BringToFront();
        ResetTitleEditorHighlight();
        _titleEditor.TextAlignment = box.Appearance.TitleAlignment == BoxTitleAlignment.Center
            ? Wpf.TextAlignment.Center
            : Wpf.TextAlignment.Left;
        ActivateTitleEditor();
        _titleEditor.Focus();
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
        var availableWidth = Math.Max(48, ToPixel(geometry.Header.Width - rightPadding));
        var minimumWidth = Math.Min(88, availableWidth);
        var measuredWidth = Forms.TextRenderer.MeasureText(
            string.IsNullOrEmpty(_titleEditor.Text) ? "M" : _titleEditor.Text + "M",
            _titleEditorFont,
            Size.Empty,
            Forms.TextFormatFlags.NoPadding | Forms.TextFormatFlags.SingleLine).Width + 12;
        var editorWidth = Math.Clamp(measuredWidth, minimumWidth, availableWidth);
        if (geometry.Box.Appearance.TitleAlignment == BoxTitleAlignment.Center)
        {
            left += (availableWidth - editorWidth) / 2;
        }

        var editorHeight = Math.Min(
            Math.Max(20, ToPixel(geometry.Header.Height) - 10),
            Math.Max(22, _titleEditorFont.Height + 4));
        _titleEditorHost.Bounds = new Rectangle(
            left,
            ToPixel(geometry.Header.Y + geometry.Header.Height / 2) - editorHeight / 2,
            editorWidth,
            editorHeight);
    }

    private Color GetOpaqueTitleEditorBackColor() => _runtime.IsDarkTheme
        ? Color.FromArgb(24, 27, 31)
        : Color.White;

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

    // The desktop surface normally keeps WS_EX_NOACTIVATE so mouse clicks
    // never steal activation from Explorer. In that state RichEdit renders
    // its selection in the inactive cyan color and ignores SelectionBackColor.
    // While the title is being edited, lift the flag and activate the surface
    // so the editor takes real focus and shows the native rename blue.
    private void ActivateTitleEditor()
    {
        if (!IsHandleCreated)
        {
            return;
        }
        if (!DesktopWindowTools.IsSurfaceActive(Handle) ||
            DesktopWindowTools.IsSurfaceNoActivate(Handle))
        {
            DesktopWindowTools.ActivateSurface(Handle);
        }
        if (!DesktopWindowTools.FocusChild(_titleEditorHost.Handle))
        {
            _titleEditorHost.Focus();
        }
        _titleEditor.Focus();
        DiagnosticLog.Info(
            $"Title editor activation active={DesktopWindowTools.IsSurfaceActive(Handle)} " +
            $"editorFocused={_titleEditor.IsKeyboardFocused} " +
            $"noActivate={DesktopWindowTools.IsSurfaceNoActivate(Handle)}");
    }

    private void RestoreTitleEditorActivation()
    {
        if (!IsHandleCreated)
        {
            return;
        }
        if (!DesktopWindowTools.IsSurfaceNoActivate(Handle))
        {
            DesktopWindowTools.SetSurfaceNoActivate(Handle, true);
        }
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
        _titleEditorHost.Visible = false;
        RestoreTitleEditorActivation();
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

    private void ToggleBoxCollapsed(DesktopBox box)
    {
        var fromHeight = GetVisualBoxHeight(box);
        box.IsCollapsed = !box.IsCollapsed;
        _hoverExpansion.Reset();
        _hoverExpandedBoxes.Remove(box.Id);
        StartBoxHeightAnimation(box, fromHeight);
        _runtime.BoxChanged(box);
    }

    private void ToggleAutoExpand(DesktopBox sourceBox)
    {
        FinishTitleEdit(true);
        var enabled = !sourceBox.ExpandOnHover;
        sourceBox.ExpandOnHover = enabled;
        if (enabled)
        {
            if (!sourceBox.IsCollapsed)
            {
                var fromHeight = GetVisualBoxHeight(sourceBox);
                sourceBox.IsCollapsed = true;
                _hoverExpansion.Reset();
                _hoverExpandedBoxes.Clear();
                _hoverExpansion.AdoptExpanded(sourceBox.Id);
                _hoverExpandedBoxes.Add(sourceBox.Id);
                StartBoxHeightAnimation(sourceBox, fromHeight);
                UpdateWindowRegion();
            }
        }
        else if (_hoverExpandedBoxes.Contains(sourceBox.Id))
        {
            CollapseHoverExpandedBox(sourceBox.Id);
        }
        _runtime.BoxChanged(sourceBox);
        InvalidateBoxVisualArea(sourceBox.Id);
    }

    private void PrepareBoxTransform(DesktopBox box)
    {
        _transformDirtyBounds = ToVisualBounds(box, box.Bounds);
        _heightAnimations.Remove(box.Id);
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

    private static Color ResolveTitleColor(string value, bool isDark)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            return isDark ? Color.White : Color.FromArgb(31, 35, 41);
        }
        return ParseOpaqueColor(value);
    }

    private static Color Blend(Color source, Color target, float amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(
            255,
            (int)(source.R + (target.R - source.R) * amount),
            (int)(source.G + (target.G - source.G) * amount),
            (int)(source.B + (target.B - source.B) * amount));
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
        RectangleF Body,
        RectangleF AutoExpand,
        RectangleF Collapse,
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

    private sealed class InternalDragSession
    {
        public bool HandledByBox { get; set; }
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
