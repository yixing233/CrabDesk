using System.Collections.Specialized;
using System.Drawing;
using System.Drawing.Drawing2D;
using CrabDesk.Core;
using CrabDesk.Native;
using Forms = System.Windows.Forms;

namespace CrabDesk.Runtime;

internal sealed class DesktopBoxForm : Forms.Form
{
    private const string ItemKeysFormat = "CrabDesk.DesktopItemKeys";
    private const string SourceBoxFormat = "CrabDesk.SourceBoxId";
    private const int WmNcHitTest = 0x0084;
    private const int WmMouseActivate = 0x0021;
    private const int WmContextMenu = 0x007B;
    private const int WsClipSiblings = 0x04000000;
    private static readonly IntPtr HtTransparent = new(-1);
    private static readonly IntPtr MaNoActivate = new(3);
    private readonly CrabDeskRuntime _runtime;
    private readonly DesktopHostService _desktopHost;
    private readonly MonitorLayout _monitor;
    private readonly double _scale;
    private readonly Dictionary<IconBitmapKey, Bitmap?> _iconCache = [];
    private readonly HashSet<IconBitmapKey> _pendingIconLoads = [];
    private readonly CancellationTokenSource _iconLoadCancellation = new();
    private readonly SemaphoreSlim _iconLoadGate = new(2, 2);
    private readonly HashSet<string> _selection = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Guid> _hoverExpandedBoxes = [];
    private readonly HashSet<string> _selectionBase = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, double> _scrollOffsets = [];
    private readonly Dictionary<Guid, BoxHeightAnimation> _heightAnimations = [];
    private readonly List<BoxGeometry> _boxes = [];
    private readonly List<ItemGeometry> _items = [];
    private readonly Forms.Timer _animationTimer;
    private readonly Forms.Timer _hoverTimer;
    private readonly Forms.TextBox _titleEditor;
    private ShellContextMenuSession? _shellContextMenu;
    private DesktopBox? _editingBox;
    private bool _cancelTitleEdit;
    private Font? _titleEditorFont;
    private DesktopBox? _movingBox;
    private DesktopBox? _resizingBox;
    private DesktopItemRef? _pressedItem;
    private DesktopBox? _selectionBox;
    private Guid? _pressedBoxId;
    private LayoutRect _startBounds;
    private PointF _pressPoint;
    private PointF _selectionStart;
    private RectangleF _selectionRectangle;
    private bool _dragStarted;
    private string? _hoveredItemKey;
    private string? _lastRegionDiagnostic;
    private int _iconCacheVersion;
    private int _paintCount;
    private bool _resourcesDisposed;

    internal DesktopBoxForm(
        CrabDeskRuntime runtime,
        DesktopHostService desktopHost,
        MonitorLayout monitor)
    {
        _runtime = runtime;
        _desktopHost = desktopHost;
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
        _hoverTimer = new Forms.Timer { Interval = 50 };
        _hoverTimer.Tick += OnHoverTimer;
        _hoverTimer.Start();

        _titleEditor = new Forms.TextBox
        {
            Visible = false,
            BorderStyle = Forms.BorderStyle.None,
            AutoSize = true,
            HideSelection = false,
            Margin = Forms.Padding.Empty
        };
        _titleEditor.KeyDown += OnTitleEditorKeyDown;
        _titleEditor.LostFocus += (_, _) => FinishTitleEdit(!_cancelTitleEdit);
        Controls.Add(_titleEditor);

        MouseDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeave += OnMouseLeave;
        MouseUp += OnMouseUp;
        MouseDoubleClick += OnMouseDoubleClick;
        MouseWheel += OnMouseWheel;
        DragEnter += OnDragOver;
        DragOver += OnDragOver;
        DragDrop += OnDragDrop;
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

    private IEnumerable<DesktopBox> DesktopBoxes => _runtime.State.Boxes.Where(box =>
        string.Equals(box.MonitorId, _monitor.Id, StringComparison.OrdinalIgnoreCase));

    internal void RefreshWorkspace()
    {
        if (!_runtime.State.Settings.DesktopBehavior.ExpandBoxOnHover)
        {
            _hoverExpandedBoxes.Clear();
        }
        else
        {
            var activeBoxIds = DesktopBoxes.Select(box => box.Id).ToHashSet();
            _hoverExpandedBoxes.RemoveWhere(id => !activeBoxIds.Contains(id));
        }
        var visibleKeys = DesktopBoxes
            .SelectMany(box => _runtime.GetItemsForBox(box.Id))
            .Select(item => item.Key.ToString())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _selection.RemoveWhere(key => !visibleKeys.Contains(key));
        PruneIconCache();
        UpdateWindowRegion();
        Invalidate();
    }

    internal void UpdateInteractionRegion()
    {
        UpdateWindowRegion();
        Invalidate();
    }

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
            DrawBox(graphics, box);
        }
        graphics.ResetTransform();
    }

    internal bool EnsureRendered()
    {
        Refresh();
        return IsHandleCreated && Visible && Enabled && _paintCount > 0;
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
            _titleEditor.Dispose();
            Region?.Dispose();
        }
        base.Dispose(disposing);
    }

    internal int ClearIconCache()
    {
        _iconCacheVersion++;
        _pendingIconLoads.Clear();
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

    private void UpdateWindowRegion()
    {
        var region = new Region(new Rectangle(0, 0, 0, 0));
        var desktopBoxes = DesktopBoxes.ToArray();
        foreach (var box in desktopBoxes)
        {
            var height = GetVisualBoxHeight(box);
            var bounds = new Rectangle(
                ToPixel(box.Bounds.X),
                ToPixel(box.Bounds.Y),
                Math.Max(1, ToPixel(box.Bounds.Width)),
                Math.Max(1, ToPixel(height)));
            region.Union(bounds);
        }
        var previous = Region;
        Region = region;
        previous?.Dispose();

        var diagnostic = $"{desktopBoxes.Length}:{_runtime.AreDesktopItemsHidden}";
        if (!string.Equals(diagnostic, _lastRegionDiagnostic, StringComparison.Ordinal))
        {
            _lastRegionDiagnostic = diagnostic;
            DiagnosticLog.Info(
                $"Surface region monitor={_monitor.Id} boxes={desktopBoxes.Length} hidden={_runtime.AreDesktopItemsHidden}");
        }
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
        var items = _runtime.GetItemsForBox(geometry.Box.Id);
        var appearance = _runtime.State.Settings.Appearance;
        var layout = DesktopItemLayoutEngine.Calculate(
            geometry.Box.ViewMode,
            new LayoutRect(geometry.Body.X, geometry.Body.Y, geometry.Body.Width, geometry.Body.Height),
            items.Count,
            geometry.Box.Appearance.IconSize,
            appearance.IconHorizontalSpacing,
            appearance.IconVerticalSpacing,
            _scrollOffsets.GetValueOrDefault(geometry.Box.Id));
        _scrollOffsets[geometry.Box.Id] = layout.ScrollOffset;
        for (var index = 0; index < layout.Items.Count; index++)
        {
            var itemBounds = layout.Items[index];
            var bounds = new RectangleF(
                (float)itemBounds.X,
                (float)itemBounds.Y,
                (float)itemBounds.Width,
                (float)itemBounds.Height);
            if (bounds.Bottom >= geometry.Body.Top && bounds.Top <= geometry.Body.Bottom)
            {
                _items.Add(new ItemGeometry(geometry.Box, items[index], bounds));
            }
        }
    }

    private void DrawBox(Graphics graphics, BoxGeometry geometry)
    {
        var isDark = _runtime.IsDarkTheme;
        var baseColor = ParseOpaqueColor(geometry.Box.Appearance.Background);
        var opacity = Math.Clamp(geometry.Box.Appearance.Opacity, 0.35, 1);
        var boxColor = ApplyOpacity(isDark ? baseColor : Blend(baseColor, Color.White, 0.88f), opacity);
        var borderColor = ApplyOpacity(
            isDark ? Color.FromArgb(82, 90, 101) : Color.FromArgb(190, 198, 207),
            opacity);
        var textColor = isDark ? Color.White : Color.FromArgb(31, 35, 41);
        var paintedBounds = RectangleF.Inflate(geometry.Bounds, -0.5f, -0.5f);
        using var path = RoundedRectangle(
            paintedBounds,
            (float)_runtime.State.Settings.Appearance.CornerRadius);
        using var fill = new SolidBrush(boxColor);
        using var border = new Pen(borderColor, 1);
        graphics.FillPath(fill, path);

        var headerState = graphics.Save();
        graphics.SetClip(path);
        using var headerFill = new SolidBrush(ApplyOpacity(isDark
            ? Color.FromArgb(24, 27, 31)
            : Color.FromArgb(239, 242, 245), opacity));
        graphics.FillRectangle(headerFill, geometry.Header);
        graphics.Restore(headerState);

        using var separator = new Pen(ApplyOpacity(isDark
            ? Color.FromArgb(58, 65, 74)
            : Color.FromArgb(211, 217, 223), opacity), 1);
        graphics.DrawLine(separator, geometry.Header.Left, geometry.Header.Bottom, geometry.Header.Right, geometry.Header.Bottom);
        using var accentPath = RoundedRectangle(
            new RectangleF(geometry.Header.X + 8, geometry.Header.Y + 9, 4, geometry.Header.Height - 18),
            2);
        using var accent = new SolidBrush(ParseOpaqueColor(geometry.Box.Appearance.Accent));
        graphics.FillPath(accent, accentPath);
        if (_runtime.State.Settings.Appearance.ShowBorder)
        {
            graphics.DrawPath(border, path);
        }

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
        var titleRightPadding = geometry.Box.Appearance.ShowCollapseButton ? 92 : 62;
        if (_editingBox?.Id != geometry.Box.Id)
        {
            graphics.DrawString(geometry.Box.Title, titleFont, titleBrush,
                new RectangleF(geometry.Header.X + 20, geometry.Header.Y, geometry.Header.Width - titleRightPadding, geometry.Header.Height), titleFormat);
        }
        if (geometry.Box.Appearance.ShowCollapseButton)
        {
            DrawChevron(graphics, geometry.Collapse, geometry.IsCollapsed, textColor);
        }
        DrawMenuIcon(graphics, geometry.Menu, textColor);

        if (geometry.IsCollapsed)
        {
            return;
        }

        var state = graphics.Save();
        graphics.SetClip(geometry.Body);
        foreach (var item in _items.Where(item => item.Box.Id == geometry.Box.Id))
        {
            DrawItem(graphics, item);
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

    private void DrawItem(Graphics graphics, ItemGeometry item)
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
            var configuredHover = ParseOpaqueColor(_runtime.State.Settings.Appearance.SelectionColor);
            using var hovered = new SolidBrush(Color.FromArgb(
                _runtime.IsDarkTheme ? 58 : 40,
                configuredHover));
            using var hoverBorder = new Pen(Color.FromArgb(
                _runtime.IsDarkTheme ? 145 : 105,
                configuredHover), 1);
            using var hoveredPath = RoundedRectangle(RectangleF.Inflate(item.Bounds, -2, -2), 4);
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
        var bitmap = GetIconBitmap(item.Item, iconSize);
        if (bitmap is not null)
        {
            graphics.DrawImage(bitmap, iconBounds);
        }
        if (!item.Box.Appearance.ShowItemLabels)
        {
            return;
        }
        using var font = CreateFont(
            item.Box.Appearance.LabelFontFamily,
            (float)item.Box.Appearance.LabelFontSize,
            FontStyle.Regular,
            GraphicsUnit.Point);
        using var brush = new SolidBrush(_runtime.IsDarkTheme
            ? Color.White
            : Color.FromArgb(31, 35, 41));
        using var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter };
        RectangleF textBounds;
        if (item.Box.ViewMode == BoxViewMode.List)
        {
            format.Alignment = StringAlignment.Near;
            format.LineAlignment = StringAlignment.Center;
            format.FormatFlags = StringFormatFlags.NoWrap;
            textBounds = new RectangleF(
                iconBounds.Right + 10,
                item.Bounds.Y,
                Math.Max(0, item.Bounds.Right - iconBounds.Right - 18),
                item.Bounds.Height);
        }
        else
        {
            format.Alignment = StringAlignment.Center;
            format.LineAlignment = StringAlignment.Near;
            textBounds = new RectangleF(
                item.Bounds.X + 2,
                iconBounds.Bottom + 3,
                item.Bounds.Width - 4,
                item.Bounds.Height - iconSize - 8);
        }
        graphics.DrawString(item.Item.DisplayName, font, brush, textBounds, format);
    }

    private Bitmap? GetIconBitmap(DesktopItemRef item, float iconSize)
    {
        var key = CreateIconBitmapKey(item, iconSize);
        if (_iconCache.TryGetValue(key, out var bitmap))
        {
            return bitmap;
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
                _iconCache[key] = bitmap;
                Invalidate();
            }));
        }
        catch (InvalidOperationException)
        {
            bitmap?.Dispose();
        }
    }

    private void PruneIconCache()
    {
        if (_iconCache.Count == 0)
        {
            return;
        }
        var activeKeys = DesktopBoxes
            .SelectMany(box => _runtime.GetItemsForBox(box.Id)
                .Select(item => CreateIconBitmapKey(item, (float)box.Appearance.IconSize)))
            .ToHashSet();
        foreach (var key in _iconCache.Keys.Where(key => !activeKeys.Contains(key)).ToArray())
        {
            _iconCache[key]?.Dispose();
            _iconCache.Remove(key);
        }
    }

    private IconBitmapKey CreateIconBitmapKey(DesktopItemRef item, float iconSize)
    {
        long modifiedTicks = 0;
        long length = 0;
        try
        {
            if (item.FileSystemPath is { } path && File.Exists(path))
            {
                var info = new FileInfo(path);
                modifiedTicks = info.LastWriteTimeUtc.Ticks;
                length = info.Length;
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        return new IconBitmapKey(
            item.ParsingName,
            Math.Clamp((int)Math.Round(iconSize * _scale), 16, 256),
            modifiedTicks,
            length);
    }

    private void OnMouseDown(object? sender, Forms.MouseEventArgs eventArgs)
    {
        RebuildGeometry();
        var point = ToDip(eventArgs.Location);
        var box = _boxes.LastOrDefault(candidate => candidate.Bounds.Contains(point));
        var item = _items.LastOrDefault(candidate => candidate.Bounds.Contains(point));
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
            if ((Forms.Control.ModifierKeys & Forms.Keys.Control) == 0)
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
        if (_runtime.State.Settings.Appearance.ShowResizeGrip &&
            !box.IsCollapsed && box.Resize.Contains(point))
        {
            PrepareBoxTransform(box.Box);
            _resizingBox = box.Box;
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
        var selected = _runtime.GetItemsForBox(sourceBoxId)
            .Where(candidate => _selection.Contains(candidate.Key.ToString()))
            .ToArray();
        var data = new Forms.DataObject();
        data.SetData(ItemKeysFormat, selected.Select(candidate => candidate.Key.ToString()).ToArray());
        data.SetData(SourceBoxFormat, sourceBoxId.ToString("D"));
        var paths = selected.Where(candidate => candidate.FileSystemPath is not null).Select(candidate => candidate.FileSystemPath!).ToArray();
        if (paths.Length > 0)
        {
            var collection = new StringCollection();
            collection.AddRange(paths);
            data.SetFileDropList(collection);
        }
        DoDragDrop(data, Forms.DragDropEffects.Move | Forms.DragDropEffects.Copy);
    }

    private void OnMouseLeave(object? sender, EventArgs eventArgs)
    {
        if (_movingBox is not null || _resizingBox is not null)
        {
            return;
        }
        Cursor = Forms.Cursors.Default;
        ClearHoverState();
    }

    private void UpdatePointerCursor(PointF point)
    {
        var canResize = _runtime.State.Settings.Appearance.ShowResizeGrip &&
            _boxes.LastOrDefault(box => !box.IsCollapsed && box.Resize.Contains(point)) is not null;
        Cursor = canResize ? Forms.Cursors.SizeNWSE : Forms.Cursors.Default;
    }

    private void OnHoverTimer(object? sender, EventArgs eventArgs)
    {
        try
        {
            if (_movingBox is not null || _resizingBox is not null)
            {
                return;
            }
            if (!_runtime.State.Settings.DesktopBehavior.ExpandBoxOnHover && _hoverExpandedBoxes.Count == 0)
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
            UpdateHoverState(ToDip(clientPoint));
        }
        catch
        {
            ClearHoverState();
        }
    }

    private bool IsInteractivePoint(PointF point)
    {
        RebuildGeometry();
        return _boxes.Any(box => box.Bounds.Contains(point)) ||
            _items.Any(item => item.Bounds.Contains(point));
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
        var changed = _hoveredItemKey is not null || _hoverExpandedBoxes.Count > 0;
        _hoveredItemKey = null;
        _hoverExpandedBoxes.Clear();
        if (changed)
        {
            UpdateWindowRegion();
            Invalidate();
        }
    }

    private void UpdateHoverState(PointF point)
    {
        RebuildGeometry();
        var hoveredItem = _items.LastOrDefault(candidate => candidate.Bounds.Contains(point));
        var itemKey = hoveredItem?.Item.Key.ToString();
        var changed = !string.Equals(_hoveredItemKey, itemKey, StringComparison.OrdinalIgnoreCase);
        _hoveredItemKey = itemKey;

        Guid? expandedBoxId = null;
        if (_runtime.State.Settings.DesktopBehavior.ExpandBoxOnHover &&
            _movingBox is null && _resizingBox is null)
        {
            var candidate = _boxes.LastOrDefault(box =>
                box.Box.IsCollapsed &&
                (box.Header.Contains(point) || (_hoverExpandedBoxes.Contains(box.Box.Id) && box.Bounds.Contains(point))));
            expandedBoxId = candidate?.Box.Id;
        }
        if (_hoverExpandedBoxes.Count != (expandedBoxId.HasValue ? 1 : 0) ||
            (expandedBoxId.HasValue && !_hoverExpandedBoxes.Contains(expandedBoxId.Value)))
        {
            _hoverExpandedBoxes.Clear();
            if (expandedBoxId.HasValue)
            {
                _hoverExpandedBoxes.Add(expandedBoxId.Value);
            }
            UpdateWindowRegion();
            changed = true;
        }
        if (changed)
        {
            Invalidate();
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
        _movingBox = null;
        _resizingBox = null;
        _pressedItem = null;
        _pressedBoxId = null;
        Capture = false;
        if (movingBox is not null)
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
            _runtime.BoxChanged(movingBox, true);
        }
        else if (resizingBox is not null)
        {
            _runtime.BoxChanged(resizingBox, true);
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
        var nextBounds = new LayoutRect(
            _startBounds.X,
            _startBounds.Y,
            SnapDipToPixel(LayoutGrid.Snap(_startBounds.Width + point.X - _pressPoint.X)),
            SnapDipToPixel(LayoutGrid.Snap(_startBounds.Height + point.Y - _pressPoint.Y))).Clamp(
                new LayoutRect(0, 0, _monitor.WorkArea.Width, _monitor.WorkArea.Height),
                LayoutGrid.SnapUp(GetMinimumBoxWidth(box)));
        ApplyBoxTransform(box, nextBounds);
    }

    private void ApplyBoxTransform(DesktopBox box, LayoutRect nextBounds)
    {
        if (box.Bounds == nextBounds)
        {
            return;
        }
        box.Bounds = nextBounds;
        UpdateWindowRegion();
        Invalidate();
    }

    private void OnMouseDoubleClick(object? sender, Forms.MouseEventArgs eventArgs)
    {
        if (eventArgs.Button != Forms.MouseButtons.Left)
        {
            return;
        }
        var point = ToDip(eventArgs.Location);
        var box = _boxes.LastOrDefault(candidate => candidate.Bounds.Contains(point));
        var item = _items.LastOrDefault(candidate => candidate.Bounds.Contains(point));
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
        eventArgs.Effect = ToDragDropEffects(ResolveTransferEffect(eventArgs, target));
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
        var external = new List<string>();
        var nativeNames = new List<string>();
        foreach (var path in paths)
        {
            var fullPath = Path.GetFullPath(path);
            if (desktopPaths.TryGetValue(fullPath, out var item))
            {
                _runtime.AssignItem(item.Key.ToString(), box.Box.Id);
                nativeNames.Add(item.DisplayName);
                nativeNames.Add(Path.GetFileName(fullPath));
            }
            else
            {
                external.Add(path);
            }
        }
        if (external.Count > 0)
        {
            await _runtime.ImportFilesAsync(
                external,
                box.Box.Id,
                transferEffect == BoxTransferEffect.MoveFiles);
            nativeNames.AddRange(external.Select(Path.GetFileName).OfType<string>().Where(name => !string.IsNullOrEmpty(name)));
        }

        DesktopIconPositionService.MoveItemsUnderBox(
            _desktopHost.DesktopListView,
            nativeNames,
            (int)(_monitor.PixelBounds.X + (box.Box.Bounds.X + 24) * _scale),
            (int)(_monitor.PixelBounds.Y + (box.Box.Bounds.Y + 64) * _scale));
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
            if (!_runtime.State.Settings.ConfirmDeleteBox || Forms.MessageBox.Show(
                    $"删除“{box.Title}”？{detail}",
                    "CrabDesk",
                    Forms.MessageBoxButtons.YesNo,
                    Forms.MessageBoxIcon.Question) == Forms.DialogResult.Yes)
            {
                _runtime.DeleteBox(box);
            }
        });
        return menu;
    }

    private void ShowItemContextMenu(DesktopBox box, DesktopItemRef item, Point location)
    {
        var selectedItems = _runtime.GetItemsForBox(box.Id)
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

    private double GetMinimumBoxWidth(DesktopBox box) =>
        DesktopItemLayoutEngine.GetMinimumBoxWidth(
            box.ViewMode,
            box.Appearance.IconSize,
            _runtime.State.Settings.Appearance.IconHorizontalSpacing);

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
        _cancelTitleEdit = false;
        _titleEditorFont?.Dispose();
        _titleEditorFont = CreateFont(
            box.Appearance.TitleFontFamily,
            (float)box.Appearance.TitleFontSize,
            box.Appearance.TitleFontBold ? FontStyle.Bold : FontStyle.Regular,
            GraphicsUnit.Point);
        _titleEditor.Font = _titleEditorFont;
        _titleEditor.TextAlign = box.Appearance.TitleAlignment == BoxTitleAlignment.Center
            ? Forms.HorizontalAlignment.Center
            : Forms.HorizontalAlignment.Left;
        _titleEditor.BackColor = _runtime.IsDarkTheme
            ? Color.FromArgb(24, 27, 31)
            : Color.FromArgb(239, 242, 245);
        _titleEditor.ForeColor = ResolveTitleColor(box.Appearance.TitleColor, _runtime.IsDarkTheme);
        var rightPadding = box.Appearance.ShowCollapseButton ? 92 : 62;
        var editorHeight = _titleEditor.PreferredHeight;
        _titleEditor.Bounds = new Rectangle(
            ToPixel(geometry.Header.X + 20),
            ToPixel(geometry.Header.Y + geometry.Header.Height / 2) - editorHeight / 2,
            Math.Max(48, ToPixel(geometry.Header.Width - rightPadding)),
            editorHeight);
        _titleEditor.Text = box.Title;
        _titleEditor.Visible = true;
        _titleEditor.BringToFront();
        _titleEditor.SelectAll();
        _titleEditor.Focus();
        Invalidate();
    }

    private void OnTitleEditorKeyDown(object? sender, Forms.KeyEventArgs eventArgs)
    {
        if (eventArgs.KeyCode == Forms.Keys.Enter)
        {
            eventArgs.Handled = true;
            eventArgs.SuppressKeyPress = true;
            FinishTitleEdit(true);
        }
        else if (eventArgs.KeyCode == Forms.Keys.Escape)
        {
            eventArgs.Handled = true;
            eventArgs.SuppressKeyPress = true;
            _cancelTitleEdit = true;
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
        _cancelTitleEdit = false;
        _titleEditor.Visible = false;
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
        _hoverExpandedBoxes.Remove(box.Id);
        StartBoxHeightAnimation(box, fromHeight);
        _runtime.BoxChanged(box);
    }

    private void PrepareBoxTransform(DesktopBox box)
    {
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
            TimeSpan.FromMilliseconds(180));
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
        Invalidate();
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
        RectangleF Collapse,
        RectangleF Menu,
        RectangleF Resize);

    private sealed record ItemGeometry(DesktopBox Box, DesktopItemRef Item, RectangleF Bounds);

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
}
