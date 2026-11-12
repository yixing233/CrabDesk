using System.Collections.Specialized;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using CrabDesk.Core;
using CrabDesk.Native;
using Forms = System.Windows.Forms;

namespace CrabDesk.Runtime;

internal sealed partial class DesktopBoxForm
{
    internal const string FileShelfDragFormat = "CrabDesk.FileShelfSource";
    private readonly Dictionary<Guid, HashSet<Guid>> _fileShelfSelections = [];
    private readonly Dictionary<Guid, Guid> _fileShelfSelectionAnchors = [];
    private Guid? _pressedFileShelfItemId;
    private Guid? _hoveredFileShelfItemId;
    private RectangleF _hoveredFileShelfButton;
    private Guid? _fileShelfDropWidgetId;
    private bool _fileShelfItemDragging;
    private const float FileShelfIconSize = 22;

    internal readonly record struct FileShelfLayout(RectangleF Add, RectangleF Mode, RectangleF Count, RectangleF List, RectangleF Footer, float RowHeight, int Rows);
    internal static FileShelfLayout GetFileShelfLayout(DesktopWidget widget, double cornerRadius = 8)
    {
        var bounds = WidgetRectangle(widget);
        var body = WidgetBodyRectangle(widget);
        var layout = WidgetFileShelfPresentation.Arrange(body.Width, body.Height, widget.FontScale);
        var add = WidgetFileShelfPresentation.AddButton(cornerRadius);
        RectangleF Place(LayoutRect rect) => new(body.X + (float)rect.X, body.Y + (float)rect.Y, (float)rect.Width, (float)rect.Height);
        return new(new(bounds.X + (float)add.X, bounds.Y + (float)add.Y, (float)add.Width, (float)add.Height), Place(layout.Mode), Place(layout.Count), Place(layout.List),
            Place(layout.Footer), (float)layout.RowHeight, layout.Rows);
    }

    private HashSet<Guid> FileShelfSelection(DesktopWidget widget)
    {
        if (!_fileShelfSelections.TryGetValue(widget.Id, out var selected)) _fileShelfSelections[widget.Id] = selected = [];
        selected.RemoveWhere(id => !widget.FileShelfItems.Any(item => item.Id == id));
        return selected;
    }

    private int FileShelfStart(DesktopWidget widget) => Math.Clamp((int)_widgetScroll.GetValueOrDefault(widget.Id),
        0, Math.Max(0, widget.FileShelfItems.Count - GetFileShelfLayout(widget, WidgetCornerRadius).Rows));

    private WidgetFileShelfItem? FileShelfItemAt(DesktopWidget widget, PointF point)
    {
        var layout = GetFileShelfLayout(widget, WidgetCornerRadius);
        if (!layout.List.Contains(point)) return null;
        var row = (int)((point.Y - layout.List.Y) / layout.RowHeight);
        if (row >= layout.Rows) return null;
        var index = FileShelfStart(widget) + row;
        return index < widget.FileShelfItems.Count ? widget.FileShelfItems[index] : null;
    }

    internal void SelectFileShelfItem(DesktopWidget widget, Guid id, bool control, bool shift)
    {
        var selected = FileShelfSelection(widget);
        var index = widget.FileShelfItems.FindIndex(item => item.Id == id);
        if (index < 0) return;
        if (shift && _fileShelfSelectionAnchors.TryGetValue(widget.Id, out var anchor))
        {
            var first = widget.FileShelfItems.FindIndex(item => item.Id == anchor);
            if (!control) selected.Clear();
            if (first < 0) first = index;
            for (var row = Math.Min(first, index); row <= Math.Max(first, index); row++) selected.Add(widget.FileShelfItems[row].Id);
        }
        else
        {
            if (control) { if (!selected.Add(id)) selected.Remove(id); }
            else if (!selected.Contains(id)) { selected.Clear(); selected.Add(id); }
            _fileShelfSelectionAnchors[widget.Id] = id;
        }
        RequestVisualLayerRender();
    }

    private bool HandleFileShelfMouseDown(DesktopWidget widget, Forms.MouseEventArgs args, PointF point)
    {
        _pressedFileShelfItemId = null;
        var item = FileShelfItemAt(widget, point);
        if (args.Button == Forms.MouseButtons.Right)
        {
            if (item is null) return false;
            if (!FileShelfSelection(widget).Contains(item.Id)) SelectFileShelfItem(widget, item.Id, false, false);
            ShowFileShelfItemMenu(widget, item, args.Location);
            return true;
        }
        if (args.Button != Forms.MouseButtons.Left || item is null || widget.FileShelfBusy) return false;
        var modifiers = DesktopWindowTools.GetAsyncModifierKeys();
        SelectFileShelfItem(widget, item.Id, (modifiers & Forms.Keys.Control) != 0, (modifiers & Forms.Keys.Shift) != 0);
        _pressedFileShelfItemId = item.Id;
        return false;
    }

    private bool TryBeginFileShelfDrag(DesktopWidget widget, PointF point)
    {
        if (widget.Kind != DesktopWidgetKind.FileShelf || _pressedFileShelfItemId is null || widget.FileShelfBusy || _fileShelfItemDragging) return false;
        if (Math.Abs(point.X - _widgetPressPoint.X) <= 4 && Math.Abs(point.Y - _widgetPressPoint.Y) <= 4) return false;
        var paths = CrabDeskRuntime.FileShelfPaths(widget, FileShelfSelection(widget), item => _runtime.GetFileShelfPath(widget, item));
        FinishWidgetPress(false);
        _pressedFileShelfItemId = null;
        if (paths.Length == 0) return true;
        var data = new Forms.DataObject();
        var files = new StringCollection(); files.AddRange(paths);
        data.SetFileDropList(files);
        data.SetData(FileShelfDragFormat, widget.Id.ToString("D"));
        FileClipboardCodec.WritePreferredDropEffect(data, move: false);
        _fileShelfItemDragging = true;
        try { DoDragDrop(data, Forms.DragDropEffects.Copy); }
        catch (Exception exception) { DiagnosticLog.Error("File shelf drag failed", exception); }
        finally { _fileShelfItemDragging = false; Capture = false; SetFileShelfDropTarget(null); RequestVisualLayerRender(); }
        return true;
    }

    private bool HandleFileShelfClick(DesktopWidget widget, PointF point)
    {
        var layout = GetFileShelfLayout(widget, WidgetCornerRadius);
        if (widget.FileShelfBusy) return true;
        if (layout.Add.Contains(point)) { _ = _runtime.PickFileShelfFilesAsync(widget); return true; }
        if (layout.Mode.Contains(point)) { ShowFileShelfModeMenu(widget, new Point(ToPixel(layout.Mode.X), ToPixel(layout.Mode.Bottom))); return true; }
        if (layout.List.Contains(point) && widget.FileShelfItems.Count == 0) { _ = _runtime.PickFileShelfFilesAsync(widget); return true; }
        return layout.List.Contains(point);
    }

    private void UpdateFileShelfHover(DesktopWidget? widget, PointF point)
    {
        var button = RectangleF.Empty;
        if (widget?.Kind == DesktopWidgetKind.FileShelf && !widget.FileShelfBusy)
        {
            var layout = GetFileShelfLayout(widget, WidgetCornerRadius);
            button = new[] { layout.Add, layout.Mode }.FirstOrDefault(rect => rect.Contains(point));
            if (widget.FileShelfItems.Count == 0 && layout.List.Contains(point)) button = layout.List;
        }
        if (_hoveredFileShelfButton == button) return;
        _hoveredFileShelfButton = button;
        RequestVisualLayerRender();
    }

    private void ShowFileShelfModeMenu(DesktopWidget widget, Point location)
    {
        var menu = CreateFileShelfMenu();
        foreach (var mode in Enum.GetValues<WidgetFileShelfMode>())
        {
            var item = new FluentToolStripMenuItem(mode == WidgetFileShelfMode.Reference ? "引用原文件" : "复制到中转站") { Checked = widget.FileShelfMode == mode };
            item.Click += (_, _) => _runtime.SetFileShelfMode(widget, mode);
            menu.Items.Add(item);
        }
        menu.Show(this, location);
    }

    private FluentContextMenuStrip CreateFileShelfMenu()
    {
        var menu = new FluentContextMenuStrip { PreferredDpiScale = (float)_scale };
        menu.Opening += (_, _) => _runtime.ApplyContextMenuTheme(menu);
        menu.Closed += (_, _) => { if (!IsDisposed && IsHandleCreated) BeginInvoke((Action)menu.Dispose); else menu.Dispose(); };
        return menu;
    }

    private void ShowFileShelfItemMenu(DesktopWidget widget, WidgetFileShelfItem item, Point location)
    {
        var menu = CreateFileShelfMenu();
        menu.Items.Add("打开文件", null, async (_, _) => await OpenFileShelfItemAsync(widget, item, false));
        menu.Items.Add("打开所在目录", null, async (_, _) => await OpenFileShelfItemAsync(widget, item, true));
        menu.Items.Add("复制选中文件路径", null, (_, _) => TryAction(() =>
        {
            var selected = FileShelfSelection(widget);
            var paths = widget.FileShelfItems.Where(entry => selected.Contains(entry.Id)).Select(entry => _runtime.GetFileShelfPath(widget, entry));
            var text = string.Join(Environment.NewLine, paths);
            if (text.Length > 0) Forms.Clipboard.SetText(text);
        }));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("移除选中条目", null, async (_, _) => await ConfirmRemoveFileShelfItemsAsync(widget, FileShelfSelection(widget).ToArray()));
        menu.Show(this, location);
    }

    private async Task OpenFileShelfItemAsync(DesktopWidget widget, WidgetFileShelfItem item, bool folder)
    {
        try
        {
            var path = _runtime.GetFileShelfPath(widget, item);
            var available = await Task.Run(() => File.Exists(path));
            if (IsDisposed || _resourcesDisposed || !_runtime.State.Widgets.Contains(widget)) return;
            item.IsAvailable = available;
            if (!available) { widget.FileShelfStatus = "文件不存在或无法访问"; _runtime.WidgetChanged(widget); return; }
            await Task.Run(() => Process.Start(folder
                ? new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }
                : new ProcessStartInfo(path) { UseShellExecute = true }));
        }
        catch (Exception exception)
        {
            widget.FileShelfStatus = "无法打开文件：" + exception.Message;
            if (!IsDisposed && !_resourcesDisposed) _runtime.WidgetChanged(widget);
        }
    }

    private async Task ConfirmRemoveFileShelfItemsAsync(DesktopWidget widget, IReadOnlyCollection<Guid> ids)
    {
        if (widget.FileShelfBusy) return;
        var items = widget.FileShelfItems.Where(item => ids.Contains(item.Id)).ToArray();
        if (items.Length == 0) return;
        var copies = items.Count(item => item.Mode == WidgetFileShelfMode.Copy);
        var message = $"将移除 {items.Length} 条暂存记录。" + (copies > 0 ? $"其中 {copies} 份暂存副本会同时清理。" : "") + "原文件保留。";
        if (!await ConfirmDesktopAsync("移除暂存条目？", message, "移除")) return;
        foreach (var item in items) await _runtime.RemoveFileShelfItemAsync(widget, item.Id);
        if (!IsDisposed && !_resourcesDisposed) RequestVisualLayerRender();
    }

    private void AddFileShelfContextMenu(FluentContextMenuStrip menu, DesktopWidget widget)
    {
        var floating = new FluentToolStripMenuItem("全局悬浮中转站") { Checked = widget.FileShelfFloating.Enabled };
        floating.Click += (_, _) => _runtime.SetFileShelfFloating(widget, !widget.FileShelfFloating.Enabled, widget.FileShelfFloating.AutoHide);
        menu.Items.Add(floating);
        var autoHide = new FluentToolStripMenuItem("贴边自动收起") { Checked = widget.FileShelfFloating.AutoHide, Enabled = widget.FileShelfFloating.Enabled };
        autoHide.Click += (_, _) => _runtime.SetFileShelfFloating(widget, true, !widget.FileShelfFloating.AutoHide);
        menu.Items.Add(autoHide);
        menu.Items.Add(new Forms.ToolStripSeparator());
        var modeMenu = new FluentToolStripMenuItem("加入方式");
        foreach (var mode in Enum.GetValues<WidgetFileShelfMode>())
        {
            var item = new FluentToolStripMenuItem(mode == WidgetFileShelfMode.Reference ? "引用原文件" : "复制到中转站") { Checked = widget.FileShelfMode == mode };
            item.Click += (_, _) => _runtime.SetFileShelfMode(widget, mode);
            modeMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(modeMenu);
        var add = menu.Items.Add("添加文件…", null, async (_, _) => await _runtime.PickFileShelfFilesAsync(widget));
        add.Enabled = !widget.FileShelfBusy;
        var clear = menu.Items.Add("清空中转站…", null, async (_, _) => await ConfirmRemoveFileShelfItemsAsync(widget, widget.FileShelfItems.Select(item => item.Id).ToArray()));
        clear.Enabled = !widget.FileShelfBusy && widget.FileShelfItems.Count > 0;
    }

    private void SetFileShelfDropTarget(Guid? id)
    {
        if (_fileShelfDropWidgetId == id) return;
        _fileShelfDropWidgetId = id;
        RequestVisualLayerRender();
    }

    private bool TryHandleFileShelfDragOver(Forms.DragEventArgs args, PointF point)
    {
        var widget = WidgetAt(point);
        if (widget?.Kind != DesktopWidgetKind.FileShelf) { SetFileShelfDropTarget(null); return false; }
        var accepts = !widget.FileShelfBusy && (args.AllowedEffect & Forms.DragDropEffects.Copy) != 0 &&
            args.Data?.GetDataPresent(Forms.DataFormats.FileDrop) == true && args.Data.GetData(Forms.DataFormats.FileDrop) is string[] { Length: > 0 } &&
            !(args.Data.GetDataPresent(FileShelfDragFormat) && args.Data.GetData(FileShelfDragFormat) is string source && source == widget.Id.ToString("D"));
        ClearDropPreview();
        SetFileShelfDropTarget(accepts ? widget.Id : null);
        args.Effect = accepts ? Forms.DragDropEffects.Copy : Forms.DragDropEffects.None;
        return true;
    }

    private async Task<bool> TryHandleFileShelfDropAsync(Forms.DragEventArgs args, PointF point)
    {
        var widget = WidgetAt(point);
        if (widget?.Kind != DesktopWidgetKind.FileShelf) return false;
        var accepted = TryHandleFileShelfDragOver(args, point) && args.Effect == Forms.DragDropEffects.Copy;
        SetFileShelfDropTarget(null);
        if (!accepted || args.Data?.GetData(Forms.DataFormats.FileDrop) is not string[] paths) return true;
        if (args.Data.GetDataPresent(DesktopIconSurface.DesktopIconDragSessionFormat) &&
            args.Data.GetData(DesktopIconSurface.DesktopIconDragSessionFormat) is DesktopIconSurfaceDragSession desktopDrag) desktopDrag.HandledByBox = true;
        if (args.Data.GetDataPresent(DragSessionFormat) && args.Data.GetData(DragSessionFormat) is InternalDragSession boxDrag) boxDrag.HandledByBox = true;
        await _runtime.AddFileShelfFilesAsync(widget, paths);
        return true;
    }

    private DesktopItemRef? GetFileShelfIconItem(DesktopWidget widget, WidgetFileShelfItem item)
    {
        // Preview/test runtimes may have no Shell provider. Their file names
        // and statuses still render through the same local fallback asset.
        if (_runtime.IconProvider is null) return null;
        string path;
        try { path = _runtime.GetFileShelfPath(widget, item); }
        catch (Exception exception) when (exception is IOException or InvalidDataException or
            ArgumentException or NotSupportedException or UnauthorizedAccessException) { return null; }
        if (string.IsNullOrWhiteSpace(path)) return null;
        return new DesktopItemRef
        {
            Key = new("file-shelf", $"{widget.Id:N}/{item.Id:N}"),
            DisplayName = item.Name,
            ParsingName = path,
            FileSystemPath = path,
            Kind = DesktopItemKind.File
        };
    }

    private void DrawFileShelfItemIcon(Graphics graphics, DesktopWidget widget, WidgetFileShelfItem item, RectangleF bounds)
    {
        var iconItem = GetFileShelfIconItem(widget, item);
        var bitmap = iconItem is null ? null : GetIconBitmap(iconItem, FileShelfIconSize);
        var opacity = item.IsAvailable == false ? 0.45f : 1f;
        if (bitmap is null)
        {
            WidgetColorIcons.Draw(graphics, "file-generic", bounds, opacity);
            return;
        }

        var destination = IconImageLayout.Contain(bitmap, bounds);
        if (destination.IsEmpty) return;
        using var attributes = new ImageAttributes();
        attributes.SetColorMatrix(new ColorMatrix { Matrix33 = opacity });
        graphics.DrawImage(bitmap,
            new PointF[] { new(destination.Left, destination.Top), new(destination.Right, destination.Top), new(destination.Left, destination.Bottom) },
            new RectangleF(0, 0, bitmap.Width, bitmap.Height), GraphicsUnit.Pixel, attributes);
    }

    private RectangleF? GetFileShelfIconDirtyBounds(IconBitmapKey key)
    {
        RectangleF? dirty = null;
        foreach (var widget in DesktopWidgetItems.Where(widget => widget.Kind == DesktopWidgetKind.FileShelf))
        {
            var layout = GetFileShelfLayout(widget, WidgetCornerRadius);
            var start = FileShelfStart(widget);
            for (var row = 0; row < layout.Rows && row + start < widget.FileShelfItems.Count; row++)
            {
                var item = GetFileShelfIconItem(widget, widget.FileShelfItems[row + start]);
                if (item is null || CreateIconBitmapKey(item, FileShelfIconSize) != key) continue;
                var bounds = new RectangleF(layout.List.X, layout.List.Y + row * layout.RowHeight, layout.List.Width, layout.RowHeight);
                dirty = dirty is { } existing ? RectangleF.Union(existing, bounds) : bounds;
            }
        }
        return dirty;
    }

    private void DrawFileShelfWidget(Graphics graphics, DesktopWidget widget, Color text, Color muted, Color accent)
    {
        var layout = GetFileShelfLayout(widget, WidgetCornerRadius);
        var scale = (float)Math.Max(1, WidgetTypography.NormalizeScale(widget.FontScale));
        var dropping = _fileShelfDropWidgetId == widget.Id;
        bool Hovered(RectangleF rect) => !widget.FileShelfBusy && _hoveredWidgetId == widget.Id && _hoveredFileShelfButton == rect;
        void Fill(RectangleF rect, Color color, float radius = 6)
        {
            using var path = RoundedRectangle(rect, radius);
            using var brush = new SolidBrush(color);
            graphics.FillPath(brush, path);
        }
        LucideRuntimeIcons.Draw(graphics, LucideRuntimeIcon.Plus, layout.Add,
            widget.FileShelfBusy ? muted : Color.FromArgb(Hovered(layout.Add) ? 255 : 210, accent), 14);
        Fill(layout.Mode, Color.FromArgb(Hovered(layout.Mode) ? 24 : 10, text));
        WidgetText(graphics, WidgetFileShelfPresentation.ModeLabel(widget, layout.Mode.Width),
            new(layout.Mode.X + 7, layout.Mode.Y, layout.Mode.Width - 25, layout.Mode.Height), muted, 10.5f);
        using (var chevron = new Pen(muted, 1.2f))
        {
            var x = layout.Mode.Right - 11; var y = layout.Mode.Y + layout.Mode.Height / 2;
            graphics.DrawLines(chevron, new PointF[] { new(x - 3, y - 1.5f), new(x, y + 1.5f), new(x + 3, y - 1.5f) });
        }
        WidgetText(graphics, $"{widget.FileShelfItems.Count} 项", layout.Count, muted, 10.5f, rightAligned: true);
        var selected = FileShelfSelection(widget);
        var start = FileShelfStart(widget);
        var overflow = widget.FileShelfItems.Count > layout.Rows;
        for (var row = 0; !dropping && row < layout.Rows && row + start < widget.FileShelfItems.Count; row++)
        {
            var item = widget.FileShelfItems[row + start];
            var bounds = new RectangleF(layout.List.X, layout.List.Y + row * layout.RowHeight, layout.List.Width - (overflow ? 7 : 0), layout.RowHeight);
            var isSelected = selected.Contains(item.Id);
            var hovered = _hoveredWidgetId == widget.Id && _hoveredFileShelfItemId == item.Id;
            Fill(RectangleF.Inflate(bounds, 0, -1), Color.FromArgb(isSelected ? 48 : hovered ? 24 : 7, isSelected || hovered ? accent : text));
            if (isSelected)
            {
                Fill(new(bounds.X, bounds.Y + 7, 2, bounds.Height - 14), accent, 1);
                LucideRuntimeIcons.Draw(graphics, LucideRuntimeIcon.Check, new(bounds.Right - 20, bounds.Y, 16, bounds.Height), accent, 12);
            }
            var ink = item.IsAvailable == false ? muted : text;
            DrawFileShelfItemIcon(graphics, widget, item, new(bounds.X + 5, bounds.Y + (bounds.Height - FileShelfIconSize) / 2, FileShelfIconSize, FileShelfIconSize));
            var textWidth = bounds.Width - 38 - (isSelected ? 18 : 0);
            WidgetText(graphics, item.Name, new(bounds.X + 34, bounds.Y, textWidth, 18 * scale), ink, 12);
            WidgetText(graphics, WidgetFileShelfPresentation.ItemDetail(item), new(bounds.X + 34, bounds.Y + 18 * scale, textWidth, 14 * scale),
                item.IsAvailable == false ? ContentTone("content-muted", text) : muted, 10);
        }
        if (overflow && !dropping && layout.Rows > 0)
        {
            var trackHeight = layout.Rows * layout.RowHeight - 4;
            var thumbHeight = Math.Max(12, trackHeight * layout.Rows / widget.FileShelfItems.Count);
            var thumbY = layout.List.Y + 2 + (trackHeight - thumbHeight) * start / (widget.FileShelfItems.Count - layout.Rows);
            Fill(new(layout.List.Right - 3, layout.List.Y + 2, 3, trackHeight), Color.FromArgb(12, text), 1.5f);
            Fill(new(layout.List.Right - 3, thumbY, 3, thumbHeight), Color.FromArgb(90, muted), 1.5f);
        }
        if (widget.FileShelfItems.Count == 0 || dropping)
        {
            var area = RectangleF.Inflate(layout.List, -1, -2);
            Fill(area, Color.FromArgb(dropping ? 30 : Hovered(layout.List) ? 18 : 8, accent), 8);
            using var outline = RoundedRectangle(area, 8);
            using var border = new Pen(Color.FromArgb(dropping ? 190 : 70, accent), 1) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
            graphics.DrawPath(border, outline);
            var showIcon = area.Height >= 82 * scale;
            var contentHeight = (showIcon ? 36 : 0) + 36 * scale;
            var y = area.Y + (area.Height - contentHeight) / 2;
            if (showIcon)
            {
                DrawContentIcon(graphics, "file-shelf", new(area.X + (area.Width - 30) / 2, y, 30, 30));
                y += 36;
            }
            WidgetText(graphics, dropping ? "松开以暂存文件" : "拖入文件，集中暂存", new(area.X + 4, y, area.Width - 8, 20 * scale), text, 12, centered: true);
            WidgetText(graphics, dropping ? widget.FileShelfMode == WidgetFileShelfMode.Copy ? "复制到中转站" : "引用原文件" : "或点击此处添加",
                new(area.X + 4, y + 20 * scale, area.Width - 8, 16 * scale), dropping || Hovered(layout.List) ? accent : muted, 10, centered: true);
        }
        var footer = WidgetFileShelfPresentation.Footer(widget, selected.Count);
        var footerBounds = layout.Footer;
        if (overflow && footerBounds.Width >= 280)
        {
            var rangeWidth = 80 * scale;
            WidgetText(graphics, $"{start + 1}–{Math.Min(start + layout.Rows, widget.FileShelfItems.Count)} / {widget.FileShelfItems.Count}",
                new(footerBounds.Right - rangeWidth, footerBounds.Y, rangeWidth, footerBounds.Height), muted, 10, rightAligned: true);
            footerBounds.Width -= rangeWidth + 8;
        }
        WidgetText(graphics, footer, footerBounds, selected.Count > 0 ? accent : muted, 10);
        if (dropping)
        {
            using var path = RoundedRectangle(RectangleF.Inflate(WidgetRectangle(widget), -2, -2), (float)WidgetCornerRadius);
            using var pen = new Pen(accent, 2); graphics.DrawPath(pen, path);
        }
    }
}
