using System.Drawing;
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
    // Kept for compatibility with the box surface's legacy OLE handlers.
    // Desktop icon dragging itself no longer publishes this payload.
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
    private const int HoverFillAlpha = 48;
    private const int HoverBorderAlpha = 156;
    // A per-pixel-alpha layered window is click-through where alpha is zero.
    // Keep the desktop background visually transparent while leaving it
    // targetable for blank-area marquee selection.
    private const int DesktopHitTestAlpha = 1;
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
    private readonly HashSet<string> _boxDropItemKeys = new(StringComparer.OrdinalIgnoreCase);
    private DesktopItemRef? _pressedItem;
    private PointF _pressPoint;
    private PointF _dragPointer;
    // Keep the pointer relationship to the actual rendered icon so the
    // floating preview stays attached to the exact pixel that was grabbed.
    private PointF _dragIconGrabOffset;
    private string? _dragAnchorKey;
    private GridCell? _dragAnchorCell;
    private bool _dragPointerOverBox;
    private PointF? _boxDragPointer;
    private string? _boxDragPrimaryKey;
    private bool _persistingLayout;
    private PointF _selectionStart;
    private RectangleF _selectionRectangle;
    private bool _dragStarted;
    private bool _selecting;
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
    private DesktopGridTopology? _previousGridTopology;
    private Action<Graphics, RectangleF>? _boxRenderer;

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
        PresentLayer();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _shellContextMenu?.Dispose();
            _shellContextMenu = null;
        }
        base.Dispose(disposing);
    }

    internal bool RefreshWorkspace() => PresentLayer();

    internal string MonitorId => _monitor.Id;

    internal void SetBoxRenderer(Action<Graphics, RectangleF>? renderer) =>
        _boxRenderer = renderer;

    internal bool RequestRender() => PresentLayer();

    // The full-monitor layered window accepts input only in the Windows work
    // area. That keeps the taskbar outside both desktop selection and icon
    // layout while preserving the ordinary blank-area marquee gesture.
    internal bool IsLayerReady => _lastPresentSucceeded && _lastRegionSucceeded;

    internal void SetVirtualBoxDropTargetEnabled(bool enabled)
    {
        AllowDrop = enabled;
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

    private bool PresentLayer()
    {
        if (IsDisposed || !IsHandleCreated || ClientSize.Width <= 0 || ClientSize.Height <= 0)
        {
            _lastPresentSucceeded = false;
            _lastRegionSucceeded = false;
            _lastPresentDiagnostic = "The desktop icon surface has no valid handle or size.";
            _lastRegionDiagnostic = _lastPresentDiagnostic;
            return false;
        }

        RebuildGeometry();
        var workAreaBounds = GetDesktopWorkAreaBounds();
        // The shared layered surface owns the complete visual desktop. It
        // draws desktop icons first and then boxes, so box opacity naturally
        // reveals the same icon pixels underneath without a native-region
        // hole or an Explorer sibling ordering dependency.
        _lastRegionSucceeded = DesktopWindowTools.ApplyRegion(
            Handle,
            [new LayoutRect(
                workAreaBounds.X,
                workAreaBounds.Y,
                workAreaBounds.Width,
                workAreaBounds.Height)],
            _scale,
            out _lastRegionDiagnostic);
        if (!_lastRegionSucceeded)
        {
            _lastPresentSucceeded = false;
            DiagnosticLog.Error(
                $"Desktop icon interaction region failed monitor={_monitor.Id}: {_lastRegionDiagnostic}",
                new InvalidOperationException(_lastRegionDiagnostic));
            return false;
        }

        using var bitmap = new Bitmap(ClientSize.Width, ClientSize.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            graphics.CompositingMode = CompositingMode.SourceOver;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            graphics.ScaleTransform((float)_scale, (float)_scale);
            graphics.SetClip(workAreaBounds, CombineMode.Replace);
            using var hitTestBackground = new SolidBrush(Color.FromArgb(DesktopHitTestAlpha, Color.Black));
            graphics.FillRectangle(hitTestBackground, workAreaBounds);
            DrawDesktopItems(graphics);
            DrawBoxItemDropPreview(graphics);
            _boxRenderer?.Invoke(graphics, workAreaBounds);
            graphics.ResetTransform();
        }

        _lastPresentSucceeded = LayeredWindowPresenter.TryPresent(
            Handle,
            bitmap,
            PointToScreen(Point.Empty),
            out _lastPresentDiagnostic);
        if (!_lastPresentSucceeded)
        {
            DiagnosticLog.Error(
                $"Desktop icon layered presentation failed monitor={_monitor.Id}: {_lastPresentDiagnostic}",
                new InvalidOperationException(_lastPresentDiagnostic));
        }
        return _lastPresentSucceeded;
    }

    private void RebuildGeometry()
    {
        _items.Clear();
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

    private void DrawDesktopItems(Graphics graphics)
    {
        var systemIconFont = SystemFonts.IconTitleFont;
        using var font = systemIconFont is null
            ? new Font("Segoe UI", 9, FontStyle.Regular, GraphicsUnit.Point)
            : new Font(
                systemIconFont.FontFamily,
                systemIconFont.Size,
                FontStyle.Regular,
                GraphicsUnit.Point);
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
        foreach (var entry in _items.OrderBy(item => _selection.Contains(item.Item.Key.ToString())))
        {
            var itemKey = entry.Item.Key.ToString();
            var selected = _selection.Contains(itemKey);
            var dragging = _dragStarted && _dragItemKeys.Contains(itemKey);
            if (dragging || _boxDropItemKeys.Contains(itemKey))
            {
                // The selected group is rendered once as a floating preview
                // below. Keeping the source pixels here would look like a
                // copy and was the source of the old duplicate-icon effect.
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
            var hovered = !selected &&
                _runtime.State.Settings.Appearance.HoverFeedback &&
                string.Equals(_hoveredItemKey, itemKey, StringComparison.OrdinalIgnoreCase);
            var iconBounds = GetIconBounds(drawBounds);
            var textBounds = GetItemTextBounds(
                graphics,
                entry.Item.DisplayName,
                drawBounds,
                iconBounds,
                font,
                selected);
            var textHitBounds = GetTextHitBounds(
                graphics,
                entry.Item.DisplayName,
                textBounds,
                font);
            var itemHitBounds = GetItemHitBounds(entry);
            if (selected && !dragging)
            {
                using var fill = new SolidBrush(Color.FromArgb(112, selectionColor));
                var selectedBounds = RectangleF.Inflate(itemHitBounds, 2, 2);
                using var path = RoundedRectangle(selectedBounds, 4);
                graphics.FillPath(fill, path);
            }
            else if (hovered)
            {
                using var fill = new SolidBrush(Color.FromArgb(HoverFillAlpha, selectionColor));
                using var border = new Pen(Color.FromArgb(HoverBorderAlpha, selectionColor), 1);
                using var path = RoundedRectangle(RectangleF.Inflate(itemHitBounds, 1, 1), 4);
                graphics.FillPath(fill, path);
                graphics.DrawPath(border, path);
            }

            var bitmap = _runtime.IconProvider.GetIcon(
                entry.Item.ParsingName,
                Math.Clamp((int)Math.Round(_iconSize * _scale), 16, 256))
                ?? ShellIconProvider.GetGenericFileIcon();
            if (bitmap is not null)
            {
                DrawImageWithAlpha(graphics, bitmap, iconBounds, 1f);
            }
            var shadowBounds = textBounds;
            shadowBounds.Offset(1, 1);
            graphics.DrawString(entry.Item.DisplayName, font, shadowBrush, shadowBounds, textFormat);
            graphics.DrawString(entry.Item.DisplayName, font, textBrush, textBounds, textFormat);
        }

        if (_dragStarted)
        {
            DrawDragPreview(graphics, font, textFormat);
        }

        if (_selecting && !_selectionRectangle.IsEmpty)
        {
            using var fill = new SolidBrush(Color.FromArgb(42, selectionColor));
            using var border = new Pen(Color.FromArgb(190, selectionColor), 1);
            graphics.FillRectangle(fill, _selectionRectangle);
            graphics.DrawRectangle(border, _selectionRectangle.X, _selectionRectangle.Y,
                Math.Max(1, _selectionRectangle.Width), Math.Max(1, _selectionRectangle.Height));
        }
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
            var bitmap = _runtime.IconProvider.GetIcon(
                entry.Item.ParsingName,
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
            : _runtime.IconProvider.GetIcon(
                    primaryItem.ParsingName,
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
        var textBottom = Math.Min(
            itemBounds.Bottom - 3,
            GetDesktopWorkAreaBounds().Bottom - 3);
        var availableTextHeight = Math.Max(0, textBottom - textTop);
        var compactHeight = Math.Max(
            0,
            Math.Min(
                availableTextHeight,
                font.GetHeight(graphics) * CompactLabelLineCount + 2));
        var textHeight = selected
            ? Math.Min(
                availableTextHeight,
                MeasureFullLabelHeight(graphics, displayName, font, textWidth))
            : compactHeight;
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
            Capture = true;
            PresentLayer();
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
            var selectionBounds = RectangleFromPoints(_selectionStart, point);
            _selectionRectangle = selectionBounds;
            _selection.Clear();
            _selection.UnionWith(_selectionBase);
            foreach (var item in _items)
            {
                if (IsSelectionHit(selectionBounds, GetItemHitBounds(item)))
                {
                    _selection.Add(item.Item.Key.ToString());
                }
            }
            PresentLayer();
            return;
        }

        if (_pressedItem is null && !_dragStarted && SetHoveredItem(GetItemAt(point)))
        {
            PresentLayer();
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
        }
        if (!_dragStarted)
        {
            return;
        }

        _dragPointer = point;
        UpdateDesktopDragPreview(point);
        _runtime.UpdateDesktopItemDropPreview(
            PointToScreen(eventArgs.Location),
            _dragItemKeys.ToArray(),
            out _dragPointerOverBox);
        if (_dragPointerOverBox)
        {
            _dragPreviewCells.Clear();
            _dragTargetCells.Clear();
        }
        PresentLayer();
    }

    private void OnMouseUp(object? sender, Forms.MouseEventArgs eventArgs)
    {
        var point = ToDip(eventArgs.Location);
        if (_selecting && eventArgs.Button == Forms.MouseButtons.Left)
        {
            _selecting = false;
            _selectionRectangle = RectangleF.Empty;
            _selectionBase.Clear();
            PresentLayer();
        }

        if (_dragStarted && eventArgs.Button == Forms.MouseButtons.Left)
        {
            var screenPoint = PointToScreen(eventArgs.Location);
            var droppedIntoBox = _runtime.TryDropDesktopItemsIntoBox(screenPoint, _dragItemKeys.ToArray());
            _runtime.ClearDesktopItemDropPreviews();
            if (!droppedIntoBox)
            {
                CommitDesktopDrop();
            }
            EndDesktopDrag();
            if (!droppedIntoBox)
            {
                PresentLayer();
            }
        }

        _pressedItem = null;
        Capture = false;
        DiagnosticLog.Info(
            $"Icon surface mouse up monitor={_monitor.Id} button={eventArgs.Button} " +
            $"x={point.X:0} y={point.Y:0} selected={_selection.Count}");
    }

    private void OnMouseLeave(object? sender, EventArgs eventArgs)
    {
        if (_pressedItem is null && !_selecting && SetHoveredItem(null))
        {
            PresentLayer();
        }
    }

    private void OnMouseCaptureChanged(object? sender, EventArgs eventArgs)
    {
        if (!Capture && _dragStarted)
        {
            EndDesktopDrag();
            PresentLayer();
        }
        else if (!Capture && !_selecting)
        {
            _pressedItem = null;
        }
    }

    private void OnDragOver(object? sender, Forms.DragEventArgs eventArgs)
    {
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
            PresentLayer();
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
            PresentLayer();
            return false;
        }

        foreach (var (key, cell) in result.Placements)
        {
            _boxDropPreviewCells[key] = new GridCell(cell.Column, cell.Row);
        }
        PresentLayer();
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
        PresentLayer();
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
        _dragPreviewCells.Clear();
        _dragTargetCells.Clear();
        if (_dragAnchorCell is not { } anchorCell ||
            _dragAnchorKey is null)
        {
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
            return;
        }

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
        _dragIconGrabOffset = PointF.Empty;
        _dragPointerOverBox = false;
        _dragItemKeys.Clear();
        _dragPreviewCells.Clear();
        _dragTargetCells.Clear();
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

    private static bool IsSelectionHit(RectangleF selection, RectangleF itemBounds) =>
        selection.Width > 0 && selection.Height > 0
            ? selection.IntersectsWith(itemBounds)
            : itemBounds.Contains(selection.Location);

    private RectangleF GetItemHitBounds(DesktopIconGeometry entry)
    {
        var iconBounds = GetIconBounds(entry.Bounds);
        var textTop = iconBounds.Bottom + 3;
        var textHeight = Math.Max(0, entry.Bounds.Bottom - textTop - 3);
        var textBounds = new RectangleF(
            entry.Bounds.X + 2,
            textTop,
            Math.Max(0, entry.Bounds.Width - 4),
            textHeight);
        return RectangleF.Intersect(
            GetDesktopWorkAreaBounds(),
            RectangleF.Inflate(RectangleF.Union(iconBounds, textBounds), 2, 2));
    }

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

    private DesktopIconGeometry? GetItemAt(PointF point)
    {
        for (var index = _items.Count - 1; index >= 0; index--)
        {
            var item = _items[index];
            var key = item.Item.Key.ToString();
            if (GetItemHitBounds(item).Contains(point))
            {
                return item;
            }
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
        GridCell Cell);
}

/// <summary>
/// Private drag data for moving rendered desktop icons. This deliberately
/// carries stable item keys instead of a FileDrop payload, so a drag within
/// the replacement surface is never interpreted by Explorer as a filesystem
/// copy or move.
/// </summary>
internal sealed class DesktopIconSurfaceDragSession(IReadOnlyList<string> itemKeys)
{
    public IReadOnlyList<string> ItemKeys { get; } = itemKeys;
    public bool HandledByBox { get; set; }
}
