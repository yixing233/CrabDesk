using System.Collections.Specialized;
using System.Drawing;
using System.Drawing.Drawing2D;
using CrabDesk.Core;
using CrabDesk.Native;
using Forms = System.Windows.Forms;

namespace CrabDesk.App;

internal sealed class DesktopBoxForm : Forms.Form
{
    private const string ItemKeysFormat = "CrabDesk.DesktopItemKeys";
    private const string SourceBoxFormat = "CrabDesk.SourceBoxId";
    private readonly CrabDeskRuntime _runtime;
    private readonly DesktopHostService _desktopHost;
    private readonly MonitorLayout _monitor;
    private readonly double _scale;
    private readonly DesktopBox _looseItemBox;
    private readonly Dictionary<IconBitmapKey, Bitmap?> _iconCache = [];
    private readonly HashSet<string> _selection = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Guid> _hoverExpandedBoxes = [];
    private readonly HashSet<string> _selectionBase = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, double> _scrollOffsets = [];
    private readonly Dictionary<Guid, BoxHeightAnimation> _heightAnimations = [];
    private readonly List<BoxGeometry> _boxes = [];
    private readonly List<ItemGeometry> _items = [];
    private readonly Forms.Timer _animationTimer;
    private readonly Forms.Timer _hoverTimer;
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

    internal DesktopBoxForm(
        CrabDeskRuntime runtime,
        DesktopHostService desktopHost,
        MonitorLayout monitor)
    {
        _runtime = runtime;
        _desktopHost = desktopHost;
        _monitor = monitor;
        _scale = monitor.DpiScale;
        _looseItemBox = new DesktopBox
        {
            Id = Guid.Empty,
            MonitorId = monitor.Id,
            ViewMode = BoxViewMode.Grid,
            Appearance = new BoxAppearance
            {
                IconSize = 48,
                LabelFontSize = 9,
                ShowItemLabels = true,
                ShowShortcutBadges = true
            }
        };
        Text = "CrabDesk Desktop Boxes";
        FormBorderStyle = Forms.FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = Forms.FormStartPosition.Manual;
        AutoScaleMode = Forms.AutoScaleMode.None;
        BackColor = Color.FromArgb(31, 34, 39);
        ClientSize = new Size((int)monitor.PixelBounds.Width, (int)monitor.PixelBounds.Height);
        DoubleBuffered = false;
        AllowDrop = true;
        SetStyle(Forms.ControlStyles.AllPaintingInWmPaint | Forms.ControlStyles.UserPaint, true);
        SetStyle(Forms.ControlStyles.OptimizedDoubleBuffer, false);

        _animationTimer = new Forms.Timer { Interval = 15 };
        _animationTimer.Tick += OnAnimationTick;
        _hoverTimer = new Forms.Timer { Interval = 50 };
        _hoverTimer.Tick += OnHoverTimer;
        _hoverTimer.Start();

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
            .Concat(_runtime.GetUnassignedDesktopItems())
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
        var graphics = eventArgs.Graphics;
        graphics.CompositingMode = CompositingMode.SourceOver;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.ScaleTransform((float)_scale, (float)_scale);
        RebuildGeometry();
        foreach (var item in _items.Where(item => item.Box.Id == Guid.Empty))
        {
            DrawItem(graphics, item);
        }
        foreach (var box in _boxes)
        {
            DrawBox(graphics, box);
        }
        graphics.ResetTransform();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _animationTimer.Stop();
            _animationTimer.Dispose();
            _hoverTimer.Stop();
            _hoverTimer.Dispose();
            ClearIconCache();
            Region?.Dispose();
        }
        base.Dispose(disposing);
    }

    internal int ClearIconCache()
    {
        var count = _iconCache.Count;
        foreach (var bitmap in _iconCache.Values)
        {
            bitmap?.Dispose();
        }
        _iconCache.Clear();
        Invalidate();
        return count;
    }

    private void UpdateWindowRegion()
    {
        var region = new Region(new Rectangle(0, 0, 0, 0));
        RebuildGeometry();
        foreach (var box in DesktopBoxes)
        {
            var height = GetVisualBoxHeight(box);
            var bounds = new RectangleF(
                (float)(box.Bounds.X * _scale),
                (float)(box.Bounds.Y * _scale),
                (float)(box.Bounds.Width * _scale),
                (float)(height * _scale));
            region.Union(bounds);
        }
        foreach (var item in _items.Where(item => item.Box.Id == Guid.Empty))
        {
            region.Union(new RectangleF(
                item.Bounds.X * (float)_scale,
                item.Bounds.Y * (float)_scale,
                item.Bounds.Width * (float)_scale,
                item.Bounds.Height * (float)_scale));
        }
        var previous = Region;
        Region = region;
        previous?.Dispose();
    }

    private void RebuildGeometry()
    {
        _boxes.Clear();
        _items.Clear();
        BuildLooseItemGeometry();
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

    private void BuildLooseItemGeometry()
    {
        if (_runtime.AreDesktopItemsHidden)
        {
            return;
        }
        var listViewBounds = DesktopWindowTools.GetWindowBounds(_desktopHost.DesktopListView);
        foreach (var item in _runtime.GetUnassignedDesktopItems())
        {
            if (!_runtime.TryGetDesktopIconPosition(item, out var position))
            {
                continue;
            }
            var screenX = listViewBounds.X + position.X;
            var screenY = listViewBounds.Y + position.Y;
            if (screenX < _monitor.PixelBounds.X - 4 ||
                screenX >= _monitor.PixelBounds.X + _monitor.PixelBounds.Width ||
                screenY < _monitor.PixelBounds.Y - 4 ||
                screenY >= _monitor.PixelBounds.Y + _monitor.PixelBounds.Height)
            {
                continue;
            }
            _items.Add(new ItemGeometry(
                _looseItemBox,
                item,
                new RectangleF(
                    (float)((screenX - _monitor.PixelBounds.X) / _scale),
                    (float)((screenY - _monitor.PixelBounds.Y) / _scale),
                    88,
                    96)));
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

        using var titleFont = new Font(
            "Segoe UI",
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
        graphics.DrawString(geometry.Box.Title, titleFont, titleBrush,
            new RectangleF(geometry.Header.X + 20, geometry.Header.Y, geometry.Header.Width - titleRightPadding, geometry.Header.Height), titleFormat);
        if (geometry.Box.Appearance.ShowCollapseButton)
        {
            DrawGlyph(graphics, geometry.Collapse, geometry.IsCollapsed ? "□" : "−", textColor);
        }
        DrawGlyph(graphics, geometry.Menu, "⋯", textColor);

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
        var isLooseItem = item.Box.Id == Guid.Empty;
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
            using var hovered = new SolidBrush(_runtime.IsDarkTheme
                ? Color.FromArgb(34, 255, 255, 255)
                : Color.FromArgb(24, 28, 43, 58));
            using var hoveredPath = RoundedRectangle(RectangleF.Inflate(item.Bounds, -2, -2), 4);
            graphics.FillPath(hovered, hoveredPath);
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
        if (item.Item.Kind == DesktopItemKind.Shortcut && item.Box.Appearance.ShowShortcutBadges)
        {
            DrawShortcutBadge(graphics, iconBounds);
        }
        if (!item.Box.Appearance.ShowItemLabels)
        {
            return;
        }
        using var font = new Font("Segoe UI", (float)item.Box.Appearance.LabelFontSize, FontStyle.Regular, GraphicsUnit.Point);
        using var brush = new SolidBrush(isLooseItem || _runtime.IsDarkTheme
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
        if (isLooseItem)
        {
            using var shadow = new SolidBrush(Color.FromArgb(220, 0, 0, 0));
            var shadowBounds = textBounds;
            shadowBounds.Offset(1, 1);
            graphics.DrawString(item.Item.DisplayName, font, shadow, shadowBounds, format);
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
        var source = _runtime.IconProvider.GetIcon(item.ParsingName, key.PixelSize);
        if (source is null)
        {
            _iconCache[key] = null;
            return null;
        }
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        stream.Position = 0;
        using var temporary = new Bitmap(stream);
        bitmap = new Bitmap(temporary);
        _iconCache[key] = bitmap;
        return bitmap;
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
            .Concat(_runtime.GetUnassignedDesktopItems()
                .Select(item => CreateIconBitmapKey(item, (float)_looseItemBox.Appearance.IconSize)))
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
        var item = _items.LastOrDefault(candidate =>
            candidate.Bounds.Contains(point) && (box is null || candidate.Box.Id != Guid.Empty));
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
                (item.Box.Id == Guid.Empty
                    ? BuildLooseItemMenu(item.Item)
                    : BuildItemMenu(item.Box, item.Item)).Show(this, eventArgs.Location);
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
        if (!box.IsCollapsed && box.Resize.Contains(point))
        {
            _resizingBox = box.Box;
        }
        else if (box.Header.Contains(point))
        {
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
        UpdateHoverState(point);
        if (_movingBox is not null)
        {
            _movingBox.Bounds = new LayoutRect(
                _startBounds.X + point.X - _pressPoint.X,
                _startBounds.Y + point.Y - _pressPoint.Y,
                _startBounds.Width,
                _startBounds.Height).Clamp(new LayoutRect(0, 0, _monitor.WorkArea.Width, _monitor.WorkArea.Height));
            _runtime.BoxChanged(_movingBox);
            return;
        }
        if (_resizingBox is not null)
        {
            _resizingBox.Bounds = new LayoutRect(
                _startBounds.X,
                _startBounds.Y,
                _startBounds.Width + point.X - _pressPoint.X,
                _startBounds.Height + point.Y - _pressPoint.Y).Clamp(new LayoutRect(0, 0, _monitor.WorkArea.Width, _monitor.WorkArea.Height));
            _runtime.BoxChanged(_resizingBox);
            return;
        }
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
        var selected = (sourceBoxId == Guid.Empty
                ? _runtime.GetUnassignedDesktopItems()
                : _runtime.GetItemsForBox(sourceBoxId))
            .Where(candidate => _selection.Contains(candidate.Key.ToString()))
            .ToArray();
        var data = new Forms.DataObject();
        data.SetData(ItemKeysFormat, selected.Select(candidate => candidate.Key.ToString()).ToArray());
        if (sourceBoxId != Guid.Empty)
        {
            data.SetData(SourceBoxFormat, sourceBoxId.ToString("D"));
        }
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
        ClearHoverState();
    }

    private void OnHoverTimer(object? sender, EventArgs eventArgs)
    {
        if (!_runtime.State.Settings.DesktopBehavior.ExpandBoxOnHover && _hoverExpandedBoxes.Count == 0)
        {
            return;
        }
        var clientPoint = PointToClient(Forms.Cursor.Position);
        if (clientPoint.X < 0 || clientPoint.Y < 0 || clientPoint.X >= ClientSize.Width || clientPoint.Y >= ClientSize.Height)
        {
            ClearHoverState();
            return;
        }
        UpdateHoverState(ToDip(clientPoint));
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
            if (LayoutCoordinator.TryMoveBoxToMonitor(
                movingBox,
                _runtime.Monitors,
                cursor.X,
                cursor.Y,
                grabOffsetX,
                grabOffsetY))
            {
                _runtime.BoxChanged(movingBox, true);
            }
        }
    }

    private void OnMouseDoubleClick(object? sender, Forms.MouseEventArgs eventArgs)
    {
        var point = ToDip(eventArgs.Location);
        var box = _boxes.LastOrDefault(candidate => candidate.Bounds.Contains(point));
        var item = _items.LastOrDefault(candidate =>
            candidate.Bounds.Contains(point) && (box is null || candidate.Box.Id != Guid.Empty));
        if (item is not null)
        {
            TryAction(() => _runtime.FileOperations.Open(item.Item));
        }
    }

    private void OnMouseWheel(object? sender, Forms.MouseEventArgs eventArgs)
    {
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
        if (eventArgs.Data.GetDataPresent(ItemKeysFormat) &&
            eventArgs.Data.GetData(ItemKeysFormat) is string[] looseKeys &&
            !eventArgs.Data.GetDataPresent(SourceBoxFormat) &&
            !box.Box.IsMappedFolder)
        {
            foreach (var key in looseKeys)
            {
                _runtime.AssignItem(key, box.Box.Id);
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
            return target.SortMode == BoxSortMode.Manual
                ? BoxTransferEffect.VirtualMove
                : BoxTransferEffect.None;
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
        var menu = new Forms.ContextMenuStrip();
        if (box.IsMappedFolder)
        {
            menu.Items.Add("打开映射文件夹", null, (_, _) =>
                TryAction(() => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(box.MappedFolder!.Path)
                {
                    UseShellExecute = true
                })));
            menu.Items.Add(new Forms.ToolStripSeparator());
        }
        var pasteItem = menu.Items.Add("粘贴", null, async (_, _) =>
        {
            try
            {
                await _runtime.PasteIntoBoxAsync(box.Id);
            }
            catch (Exception exception)
            {
                Forms.MessageBox.Show(exception.Message, "CrabDesk", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Error);
            }
        });
        pasteItem.Enabled = _runtime.CanPasteIntoBox(box);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("重命名", null, (_, _) =>
        {
            var name = TextInputDialog.Show("重命名盒子", "名称", box.Title);
            if (name is not null)
            {
                box.Title = name;
                _runtime.BoxChanged(box, true);
            }
        });
        menu.Items.Add(box.IsCollapsed ? "展开" : "折叠", null, (_, _) =>
        {
            ToggleBoxCollapsed(box);
        });
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

    private Forms.ContextMenuStrip BuildItemMenu(DesktopBox box, DesktopItemRef item)
    {
        var menu = new Forms.ContextMenuStrip();
        var selectedItems = _runtime.GetItemsForBox(box.Id)
            .Where(candidate => _selection.Contains(candidate.Key.ToString()))
            .ToArray();
        if (selectedItems.Length == 0)
        {
            selectedItems = [item];
        }
        menu.Items.Add("打开", null, (_, _) => TryAction(() => _runtime.FileOperations.Open(item)));
        menu.Items.Add("打开所在位置", null, (_, _) => TryAction(() => _runtime.FileOperations.OpenLocation(item)));
        var fileItems = selectedItems.Where(candidate => candidate.FileSystemPath is not null).ToArray();
        if (fileItems.Length > 0)
        {
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("复制", null, (_, _) =>
                TryAction(() => _runtime.FileOperations.SetClipboardFiles(fileItems, false)));
            var canModify = box.MappedFolder?.IsReadOnly != true && fileItems.All(candidate => !candidate.IsReadOnly);
            var cutItem = menu.Items.Add("剪切", null, (_, _) =>
                TryAction(() => _runtime.FileOperations.SetClipboardFiles(fileItems, true)));
            cutItem.Enabled = canModify;
            if (selectedItems.Length == 1)
            {
                var renameItem = menu.Items.Add("重命名", null, async (_, _) =>
                {
                    var name = TextInputDialog.Show("重命名项目", "新名称", item.DisplayName);
                    if (name is null)
                    {
                        return;
                    }
                    try
                    {
                        await _runtime.RenameItemAsync(item, name, box.Id);
                    }
                    catch (Exception exception)
                    {
                        Forms.MessageBox.Show(exception.Message, "CrabDesk", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Error);
                    }
                });
                renameItem.Enabled = canModify;
            }
        }
        if (!box.IsMappedFolder)
        {
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("移出盒子", null, (_, _) =>
            {
                foreach (var selected in selectedItems)
                {
                    _runtime.UnassignItem(selected.Key.ToString());
                }
            });
        }
        if (fileItems.Length > 0 && box.MappedFolder?.IsReadOnly != true && fileItems.All(candidate => !candidate.IsReadOnly))
        {
            var deleteLabel = fileItems.Length == 1 ? "移入回收站" : $"将 {fileItems.Length} 项移入回收站";
            menu.Items.Add(deleteLabel, null, async (_, _) =>
            {
                var prompt = fileItems.Length == 1
                    ? $"将“{item.DisplayName}”移入回收站？"
                    : $"将选中的 {fileItems.Length} 个项目移入回收站？";
                if (Forms.MessageBox.Show(prompt, "CrabDesk", Forms.MessageBoxButtons.YesNo, Forms.MessageBoxIcon.Question) == Forms.DialogResult.Yes)
                {
                    await _runtime.FileOperations.DeleteAsync(fileItems);
                    await _runtime.RefreshItemsAsync();
                }
            });
        }
        menu.Items.Add("属性", null, (_, _) => TryAction(() => _runtime.FileOperations.ShowProperties(item)));
        return menu;
    }

    private Forms.ContextMenuStrip BuildLooseItemMenu(DesktopItemRef item)
    {
        var menu = new Forms.ContextMenuStrip();
        var selectedItems = _runtime.GetUnassignedDesktopItems()
            .Where(candidate => _selection.Contains(candidate.Key.ToString()))
            .ToArray();
        if (selectedItems.Length == 0)
        {
            selectedItems = [item];
        }
        menu.Items.Add("打开", null, (_, _) => TryAction(() => _runtime.FileOperations.Open(item)));
        menu.Items.Add("打开所在位置", null, (_, _) => TryAction(() => _runtime.FileOperations.OpenLocation(item)));
        var fileItems = selectedItems.Where(candidate => candidate.FileSystemPath is not null).ToArray();
        if (fileItems.Length > 0)
        {
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("复制", null, (_, _) =>
                TryAction(() => _runtime.FileOperations.SetClipboardFiles(fileItems, false)));
            var canModify = fileItems.All(candidate => !candidate.IsReadOnly);
            var cutItem = menu.Items.Add("剪切", null, (_, _) =>
                TryAction(() => _runtime.FileOperations.SetClipboardFiles(fileItems, true)));
            cutItem.Enabled = canModify;
            if (selectedItems.Length == 1)
            {
                var renameItem = menu.Items.Add("重命名", null, async (_, _) =>
                {
                    var name = TextInputDialog.Show("重命名项目", "新名称", item.DisplayName);
                    if (name is null)
                    {
                        return;
                    }
                    try
                    {
                        await _runtime.RenameUnassignedItemAsync(item, name);
                    }
                    catch (Exception exception)
                    {
                        Forms.MessageBox.Show(exception.Message, "CrabDesk", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Error);
                    }
                });
                renameItem.Enabled = canModify;
            }
            if (canModify)
            {
                var deleteLabel = fileItems.Length == 1 ? "移入回收站" : $"将 {fileItems.Length} 项移入回收站";
                menu.Items.Add(deleteLabel, null, async (_, _) =>
                {
                    var prompt = fileItems.Length == 1
                        ? $"将“{item.DisplayName}”移入回收站？"
                        : $"将选中的 {fileItems.Length} 个项目移入回收站？";
                    if (Forms.MessageBox.Show(prompt, "CrabDesk", Forms.MessageBoxButtons.YesNo, Forms.MessageBoxIcon.Question) == Forms.DialogResult.Yes)
                    {
                        await _runtime.FileOperations.DeleteAsync(fileItems);
                        await _runtime.RefreshItemsAsync();
                    }
                });
            }
        }
        menu.Items.Add("属性", null, (_, _) => TryAction(() => _runtime.FileOperations.ShowProperties(item)));
        return menu;
    }

    private static void DrawGlyph(Graphics graphics, RectangleF bounds, string glyph, Color color)
    {
        using var font = new Font("Segoe UI Symbol", 11, FontStyle.Regular, GraphicsUnit.Point);
        using var brush = new SolidBrush(color);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        graphics.DrawString(glyph, font, brush, bounds, format);
    }

    private void DrawShortcutBadge(Graphics graphics, RectangleF iconBounds)
    {
        var size = Math.Clamp(iconBounds.Width * 0.32f, 11, 16);
        var bounds = new RectangleF(iconBounds.Left - 1, iconBounds.Bottom - size + 1, size, size);
        using var path = RoundedRectangle(bounds, 3);
        using var background = new SolidBrush(_runtime.IsDarkTheme ? Color.FromArgb(235, 244, 246, 249) : Color.White);
        using var border = new Pen(Color.FromArgb(120, 76, 84, 94), 1);
        graphics.FillPath(background, path);
        graphics.DrawPath(border, path);
        using var font = new Font("Segoe UI Symbol", Math.Max(7, size * 0.48f), FontStyle.Bold, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(Color.FromArgb(45, 53, 63));
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        graphics.DrawString("↗", font, brush, bounds, format);
    }

    private bool IsEffectivelyCollapsed(DesktopBox box) =>
        box.IsCollapsed && !_hoverExpandedBoxes.Contains(box.Id);

    private void ToggleBoxCollapsed(DesktopBox box)
    {
        var fromHeight = GetVisualBoxHeight(box);
        box.IsCollapsed = !box.IsCollapsed;
        _hoverExpandedBoxes.Remove(box.Id);
        StartBoxHeightAnimation(box, fromHeight);
        _runtime.BoxChanged(box);
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
