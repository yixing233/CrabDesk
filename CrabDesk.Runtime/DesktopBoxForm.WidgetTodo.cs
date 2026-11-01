using System.Drawing;
using CrabDesk.Core;
using Forms = System.Windows.Forms;

namespace CrabDesk.Runtime;

internal sealed partial class DesktopBoxForm
{
    private DesktopWidgetTextInput? _todoInput;
    private DesktopWidget? _todoEditingWidget;
    private Guid? _todoEditingTaskId;
    private LayoutRect _todoEditingBounds;
    private bool _todoInputCancelled;
    private string _todoDraft = string.Empty;
    private Guid? _hoveredTodoTaskId;
    private Guid? _hoveredTodoAddId;
    private Guid? _hoveredTodoGroupId;
    private readonly HashSet<Guid> _expandedTodoWidgets = [];
    private readonly Dictionary<(Guid Widget, Guid Task), DesktopWidgetTextVisual> _todoTextVisuals = [];
    private FluentContextMenuStrip? _todoDeleteMenu;
    private TodoDeleteConfirmationCard? _todoDeleteCard;
    private (DesktopWidget Widget, Guid TaskId, LayoutRect Bounds)? _todoDeletion;

    internal readonly record struct TodoLayout(RectangleF List, RectangleF Add, int Rows);
    internal static TodoLayout GetTodoLayout(DesktopWidget widget)
    {
        var body = WidgetBodyRectangle(widget);
        var list = new RectangleF(body.X, body.Y - 4, body.Width, body.Height + 4);
        var bounds = WidgetRectangle(widget);
        return new(list, new(WidgetMenuRectangle(widget).Left - 28, bounds.Y + 4, 24, 24), Math.Max(1, (int)(list.Height / 30)));
    }

    private List<WidgetTodoRow> TodoRows(DesktopWidget widget) => WidgetTodoPresentation.Rows(widget,
        _expandedTodoWidgets.Contains(widget.Id), _todoEditingWidget == widget && _todoEditingTaskId is null);

    private int WidgetTodoStart(DesktopWidget widget) => WidgetTodoStart(widget, TodoRows(widget));

    private int WidgetTodoStart(DesktopWidget widget, IReadOnlyList<WidgetTodoRow> rows)
        => Math.Clamp((int)_widgetScroll.GetValueOrDefault(widget.Id), 0, TodoLastStart(rows, GetTodoLayout(widget).List.Height));

    private static int TodoLastStart(IReadOnlyList<WidgetTodoRow> rows, float remaining)
    {
        var lastStart = rows.Count;
        while (lastStart > 0)
        {
            var height = rows[lastStart - 1].Height;
            if (height > remaining) break;
            remaining -= height; lastStart--;
        }
        return lastStart;
    }

    private List<(WidgetTodoRow Row, RectangleF Bounds)> VisibleTodoRows(DesktopWidget widget)
    {
        var rows = TodoRows(widget); var list = GetTodoLayout(widget).List;
        var visible = new List<(WidgetTodoRow, RectangleF)>(); var y = list.Top;
        for (var index = WidgetTodoStart(widget, rows); index < rows.Count && y + rows[index].Height <= list.Bottom; index++)
        {
            visible.Add((rows[index], new(list.X, y, list.Width, rows[index].Height)));
            y += rows[index].Height;
        }
        return visible;
    }

    private RectangleF TodoRowRectangle(DesktopWidget widget, int row)
    {
        var visible = VisibleTodoRows(widget);
        return row >= 0 && row < visible.Count ? visible[row].Bounds : RectangleF.Empty;
    }
    private int TodoRowAtPoint(DesktopWidget widget, PointF point)
    {
        var visible = VisibleTodoRows(widget);
        for (var row = 0; row < visible.Count; row++) if (visible[row].Bounds.Contains(point)) return row;
        return -1;
    }
    private RectangleF TodoTextRectangle(DesktopWidget widget, int row, bool reserveActions = false)
    {
        var bounds = TodoRowRectangle(widget, row);
        return new(bounds.X + 28, bounds.Y + 2, Math.Max(0, bounds.Width - (reserveActions ? 108 : 36)), 26);
    }

    internal RectangleF TodoEditRectangle(DesktopWidget widget, int row)
    {
        var bounds = TodoRowRectangle(widget, row);
        return new(bounds.Right - 52, bounds.Y + 3, 24, 24);
    }
    internal RectangleF TodoDatesRectangle(DesktopWidget widget, int row)
    {
        var bounds = TodoRowRectangle(widget, row);
        return new(bounds.Right - 76, bounds.Y + 3, 24, 24);
    }
    internal RectangleF TodoDeleteRectangle(DesktopWidget widget, int row)
    {
        var bounds = TodoRowRectangle(widget, row);
        return new(bounds.Right - 28, bounds.Y + 3, 24, 24);
    }

    private Guid? TodoTaskAtPoint(DesktopWidget widget, PointF point)
    {
        var layout = GetTodoLayout(widget);
        if (!layout.List.Contains(point)) return null;
        var row = TodoRowAtPoint(widget, point);
        var visible = VisibleTodoRows(widget);
        return row >= 0 && row < visible.Count ? visible[row].Row.Task?.Id : null;
    }

    internal RectangleF TodoCompletedHeaderRectangle(DesktopWidget widget) =>
        VisibleTodoRows(widget).FirstOrDefault(row => row.Row.IsCompletedHeader).Bounds;

    private void EnsureTodoRowVisible(DesktopWidget widget, int index)
    {
        var rows = TodoRows(widget);
        var start = WidgetTodoStart(widget, rows);
        if (index >= start && index < start + VisibleTodoRows(widget).Count) return;
        var remaining = GetTodoLayout(widget).List.Height - rows[index].Height;
        start = index;
        while (start > 0 && rows[start - 1].Height <= remaining)
        { remaining -= rows[--start].Height; }
        _widgetScroll[widget.Id] = start;
    }

    private void BeginWidgetTodoInput(DesktopWidget widget, WidgetTodoItem? task = null)
    {
        DismissWidgetInputs();
        _runtime.CommitActiveDesktopInlineRename();
        _todoInput ??= new DesktopWidgetTextInput("待办事项", 500);
        _todoInput.InputBox.FontSize = 12.5;
        _todoInput.InputBox.ToolTip = "Enter 保存 · Esc 取消";
        _todoInput.InputBox.IsUndoEnabled = false;
        _todoInput.InputBox.IsUndoEnabled = true;
        _todoEditingWidget = widget; _todoEditingTaskId = task?.Id; _todoEditingBounds = widget.Bounds;
        if (task?.IsCompleted == true) _expandedTodoWidgets.Add(widget.Id);
        _todoInputCancelled = false; _todoDraft = task?.Text ?? string.Empty;
        var index = TodoRows(widget).FindIndex(row => !row.IsCompletedHeader && row.Task?.Id == task?.Id);
        if (index < 0) { _todoEditingWidget = null; return; }
        EnsureTodoRowVisible(widget, index);
        var start = WidgetTodoStart(widget);
        _widgetScroll[widget.Id] = start;
        var field = TodoTextRectangle(widget, index - start);
        var location = PointToScreen(new Point(ToPixel(field.X), ToPixel(field.Y)));
        var appearance = _runtime.State.Boxes.FirstOrDefault()?.Appearance ?? _runtime.State.Settings.DefaultBoxAppearance;
        var background = ParseOpaqueColor(appearance.Background);
        var foreground = ResolveAutoTextColor(background);
        RequestVisualLayerRender();
        _todoInput.Present(new(location, new Size(ToPixel(field.Width), ToPixel(field.Height))), _todoDraft,
            TranslationFieldColor(background, foreground, 12), foreground, ParseOpaqueColor(appearance.Accent),
            value => _todoDraft = value, () => { }, dismissed: _ =>
            {
                _todoEditingWidget = null;
                if (!_todoInputCancelled && !_resourcesDisposed && !IsDisposed)
                    _runtime.SaveWidgetTask(widget, task?.Id, _todoDraft);
                if (!_resourcesDisposed && !IsDisposed) RequestVisualLayerRender();
            }, cornerRadius: Math.Min(6, _runtime.State.Settings.Appearance.CornerRadius), singleLine: true,
            cancelled: () => _todoInputCancelled = true, showFocusBorder: false, borderThickness: 0);
    }

    private void DismissWidgetTodoInput()
    {
        if (_todoInput is { IsDisposed: false }) _todoInput.Dismiss();
        _todoEditingWidget = null;
    }

    private void RefreshWidgetTodoInput()
    {
        if (_todoDeletion is { } pending && (_runtime.IsPaused || !_runtime.State.Widgets.Contains(pending.Widget) ||
            pending.Widget.MonitorId != _monitor.Id || pending.Widget.Bounds != pending.Bounds ||
            !pending.Widget.Tasks.Any(task => task.Id == pending.TaskId))) DismissTodoDeleteConfirmation();
        var valid = _runtime.State.Widgets.Where(widget => widget.Kind == DesktopWidgetKind.Todo)
            .SelectMany(widget => widget.Tasks.Select(task => (widget.Id, task.Id)).Append((widget.Id, Guid.Empty))).ToHashSet();
        foreach (var key in _todoTextVisuals.Keys.Where(key => !valid.Contains(key)).ToArray())
        { _todoTextVisuals[key].Dispose(); _todoTextVisuals.Remove(key); }
        if (_todoEditingWidget is not { } widget) return;
        if (!_runtime.State.Widgets.Contains(widget) || widget.MonitorId != _monitor.Id || widget.Bounds != _todoEditingBounds ||
            _runtime.IsPaused || _todoEditingTaskId is { } id && !widget.Tasks.Any(task => task.Id == id)) DismissWidgetTodoInput();
    }

    private void HandleTodoWidgetClick(DesktopWidget widget, PointF point)
    {
        var layout = GetTodoLayout(widget);
        if (layout.Add.Contains(point)) { BeginWidgetTodoInput(widget); return; }
        if (!layout.List.Contains(point)) return;
        var row = TodoRowAtPoint(widget, point);
        var visible = VisibleTodoRows(widget);
        if (row < 0 || row >= visible.Count) return;
        if (visible[row].Row.IsCompletedHeader)
        {
            DismissWidgetTodoInput();
            if (!_expandedTodoWidgets.Remove(widget.Id))
            {
                _expandedTodoWidgets.Add(widget.Id);
                var first = TodoRows(widget).FindIndex(item => item.Task?.IsCompleted == true);
                if (first >= 0) EnsureTodoRowVisible(widget, first);
            }
            _widgetScroll[widget.Id] = WidgetTodoStart(widget);
            _hoveredTodoTaskId = null; _hoveredTodoGroupId = null;
            RequestVisualLayerRender();
            return;
        }
        if (visible[row].Row.Task is not { } task) return;
        if (point.X < layout.List.X + 24)
        {
            _runtime.ToggleWidgetTask(widget, task.Id);
            _widgetScroll[widget.Id] = WidgetTodoStart(widget);
            _hoveredTodoTaskId = TodoTaskAtPoint(widget, point);
            RequestVisualLayerRender();
        }
        else if (TodoDatesRectangle(widget, row).Contains(point)) _runtime.EditWidgetTaskDates(widget, task.Id);
        else if (TodoEditRectangle(widget, row).Contains(point)) BeginWidgetTodoInput(widget, task);
        else if (TodoDeleteRectangle(widget, row).Contains(point))
            ShowTodoDeleteConfirmation(widget, task, TodoDeleteRectangle(widget, row));
    }

    private void ShowTodoDeleteConfirmation(DesktopWidget widget, WidgetTodoItem task, RectangleF anchor)
    {
        DismissWidgetInputs();
        var dark = UsesLightText(ParseOpaqueColor((_runtime.State.Boxes.FirstOrDefault()?.Appearance ??
            _runtime.State.Settings.DefaultBoxAppearance).Background));
        var menu = new FluentContextMenuStrip { PreferredDpiScale = (float)_scale, ShowImageMargin = false,
            ShowCheckMargin = false, Renderer = new FluentMenuRenderer(dark),
            AnimationsEnabled = _runtime.State.Settings.Appearance.AnimationEnabled && Forms.SystemInformation.IsMenuAnimationEnabled && Forms.SystemInformation.IsMenuFadeEnabled };
        var card = new TodoDeleteConfirmationCard(task.Text, dark, _scale);
        menu.BackColor = card.BackColor; menu.ForeColor = card.ForeColor;
        menu.Items.Add(new Forms.ToolStripControlHost(card) { AutoSize = false, Size = card.Size, Margin = Forms.Padding.Empty, Padding = Forms.Padding.Empty });
        _todoDeleteMenu = menu; _todoDeleteCard = card; _todoDeletion = (widget, task.Id, widget.Bounds);
        card.CancelButton.Click += (_, _) => DismissTodoDeleteConfirmation();
        card.DeleteButton.Click += (_, _) =>
        {
            if (_todoDeleteMenu != menu || _todoDeletion is not { } pending) return;
            DismissTodoDeleteConfirmation();
            if (!_runtime.IsPaused && widget.Bounds == pending.Bounds && widget.MonitorId == _monitor.Id &&
                _runtime.RemoveWidgetTask(widget, pending.TaskId))
            {
                _widgetScroll[widget.Id] = WidgetTodoStart(widget);
                _hoveredTodoTaskId = null;
                RequestVisualLayerRender();
            }
        };
        menu.Closed += (_, _) =>
        {
            if (_todoDeleteMenu == menu) { _todoDeleteMenu = null; _todoDeleteCard = null; _todoDeletion = null; }
            if (!IsDisposed && IsHandleCreated) BeginInvoke((Action)menu.Dispose); else menu.Dispose();
        };
        var size = menu.GetPreferredSize(Size.Empty);
        var screen = PointToScreen(new Point(ToPixel(anchor.Right), ToPixel(anchor.Bottom + 6)));
        var area = _monitor.PixelWorkArea;
        var x = Math.Clamp(screen.X - size.Width, (int)area.X, Math.Max((int)area.X, (int)(area.X + area.Width) - size.Width));
        var y = screen.Y + size.Height <= area.Y + area.Height ? screen.Y : screen.Y - size.Height - ToPixel(anchor.Height + 12);
        y = Math.Clamp(y, (int)area.Y, Math.Max((int)area.Y, (int)(area.Y + area.Height) - size.Height));
        menu.Show(this, PointToClient(new Point(x, y)));
    }

    private void DismissTodoDeleteConfirmation()
    {
        var menu = _todoDeleteMenu;
        _todoDeleteMenu = null; _todoDeleteCard = null; _todoDeletion = null;
        if (menu is not { IsDisposed: false }) return;
        foreach (Forms.ToolStripItem item in menu.Items) item.Enabled = false;
        menu.Close();
    }

    private void DrawTodoWidget(Graphics graphics, DesktopWidget widget, RectangleF body, Color text, Color muted, Color accent)
    {
        var layout = GetTodoLayout(widget);
        var addHovered = _hoveredTodoAddId == widget.Id;
        if (addHovered)
        {
            using var addPath = RoundedRectangle(layout.Add, (float)Math.Min(6, _runtime.State.Settings.Appearance.CornerRadius));
            using var addFill = new SolidBrush(Color.FromArgb(20, text)); graphics.FillPath(addFill, addPath);
        }
        LucideRuntimeIcons.Draw(graphics, LucideRuntimeIcon.Plus, layout.Add, accent, 14);
        var bounds = WidgetRectangle(widget);
        var status = $"· {widget.Tasks.Count(task => !task.IsCompleted)}";
        var headerLeft = bounds.X + 12;
        var headerWidth = layout.Add.Left - headerLeft - 12;
        using var statusFont = new Font(BoxAppearance.DefaultFontFamily, 10.5f, GraphicsUnit.Pixel);
        var statusWidth = (float)Math.Ceiling(graphics.MeasureString(status, statusFont).Width);
        using var titleFont = new Font(BoxAppearance.DefaultFontFamily, 13.333f, FontStyle.Bold, GraphicsUnit.Pixel);
        var titleWidth = Math.Min(graphics.MeasureString(widget.DisplayTitle, titleFont).Width, Math.Max(0, headerWidth - statusWidth - 8));
        WidgetText(graphics, widget.DisplayTitle, new(headerLeft, bounds.Y + 4, titleWidth, 24), text, 13.333f, bold: true);
        WidgetText(graphics, status, new(headerLeft + titleWidth + 8, bounds.Y + 4, statusWidth, 24), muted, 10.5f);
        var adding = _todoEditingWidget == widget && _todoEditingTaskId is null;
        if (widget.Tasks.Count == 0 && !adding)
            WidgetText(graphics, "点击顶栏 + 添加待办", new(layout.List.X, layout.List.Y + 6, layout.List.Width, 40), muted, 11.5f, multiline: true);
        var visible = VisibleTodoRows(widget);
        for (var row = 0; row < visible.Count; row++)
        {
            var (item, slot) = visible[row]; var task = item.Task;
            var editing = !item.IsCompletedHeader && _todoEditingWidget == widget && task?.Id == _todoEditingTaskId;
            var hovered = _hoveredWidgetId == widget.Id &&
                (item.IsCompletedHeader ? _hoveredTodoGroupId == widget.Id : task is not null && _hoveredTodoTaskId == task.Id);
            if (hovered || editing)
            {
                using var rowPath = RoundedRectangle(RectangleF.Inflate(slot, 0, -1), (float)Math.Min(6, _runtime.State.Settings.Appearance.CornerRadius));
                using var rowFill = new SolidBrush(Color.FromArgb(14, text));
                graphics.FillPath(rowFill, rowPath);
            }
            if (item.IsCompletedHeader)
            {
                if (widget.Tasks.Any(value => !value.IsCompleted) || adding)
                {
                    using var divider = new Pen(Color.FromArgb(20, text), 1);
                    graphics.DrawLine(divider, slot.Left + 4, slot.Top, slot.Right - 6, slot.Top);
                }
                using var chevron = new Pen(muted, 1.3f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round, LineJoin = System.Drawing.Drawing2D.LineJoin.Round };
                var x = slot.X + 12; var y = slot.Y + 14;
                graphics.DrawLines(chevron, _expandedTodoWidgets.Contains(widget.Id)
                    ? new[] { new PointF(x - 4, y - 2), new PointF(x, y + 2), new PointF(x + 4, y - 2) }
                    : new[] { new PointF(x - 2, y - 4), new PointF(x + 2, y), new PointF(x - 2, y + 4) });
                WidgetText(graphics, $"已完成 · {widget.Tasks.Count(value => value.IsCompleted)}", new(slot.X + 28, slot.Y + 2, slot.Width - 36, 24), muted, 11);
                continue;
            }
            var check = new RectangleF(slot.X + 5, slot.Y + 7.5f, 15, 15);
            using var checkPath = RoundedRectangle(check, (float)Math.Min(5, _runtime.State.Settings.Appearance.CornerRadius));
            using var checkPen = new Pen(task?.IsCompleted == true ? accent : Color.FromArgb(155, text), 1.1f);
            if (task?.IsCompleted == true)
            {
                using var completed = new SolidBrush(accent); graphics.FillPath(completed, checkPath);
                LucideRuntimeIcons.Draw(graphics, LucideRuntimeIcon.Check, check, ResolveAutoTextColor(accent), 11);
            }
            graphics.DrawPath(checkPen, checkPath);
            var actionsVisible = task is not null && hovered && !editing;
            var key = (widget.Id, task?.Id ?? Guid.Empty);
            if (!_todoTextVisuals.TryGetValue(key, out var visual)) _todoTextVisuals[key] = visual = new();
            visual.Draw(graphics, task?.Text ?? "输入新的待办…", TodoTextRectangle(widget, row, actionsVisible),
                task?.IsCompleted == true ? Color.FromArgb(150, muted) : text, 0, fontSize: 12.5,
                insets: new System.Windows.Thickness(2, 0, 2, 0), singleLine: true, strike: task?.IsCompleted == true);
            if (task?.HasDateRange == true)
                WidgetText(graphics, WidgetTodoPresentation.DateLabel(task, DateTime.Today.Year), new(slot.X + 28, slot.Y + 28, slot.Width - 36, 14), Color.FromArgb(170, text), 10.5f);
            if (actionsVisible)
            {
                LucideRuntimeIcons.Draw(graphics, LucideRuntimeIcon.Pencil, TodoEditRectangle(widget, row), muted, 12);
                DrawTodoDateButton(graphics, TodoDatesRectangle(widget, row), task!.HasDateRange ? accent : muted);
                LucideRuntimeIcons.Draw(graphics, LucideRuntimeIcon.Trash2, TodoDeleteRectangle(widget, row), muted, 12);
            }
        }
        var rows = TodoRows(widget);
        if (_hoveredWidgetId == widget.Id && rows.Count > visible.Count)
        {
            var start = WidgetTodoStart(widget, rows);
            var height = Math.Max(18, layout.List.Height * layout.List.Height / rows.Sum(item => item.Height));
            var maximumStart = TodoLastStart(rows, layout.List.Height);
            var maxOffset = rows.Take(maximumStart).Sum(item => item.Height);
            var offset = rows.Take(start).Sum(item => item.Height);
            var y = layout.List.Y + (layout.List.Height - height) * offset / Math.Max(1, maxOffset);
            using var thumb = new SolidBrush(Color.FromArgb(80, text));
            using var path = RoundedRectangle(new(layout.List.Right - 3, y, 2, height), 1);
            graphics.FillPath(thumb, path);
        }
    }

    private static void DrawTodoDateButton(Graphics graphics, RectangleF bounds, Color color)
    {
        using var pen = new Pen(color, 1.2f);
        var icon = new RectangleF(bounds.X + 6, bounds.Y + 6, 12, 12);
        using var path = RoundedRectangle(icon, 2); graphics.DrawPath(pen, path);
        graphics.DrawLine(pen, icon.Left, icon.Top + 4, icon.Right, icon.Top + 4);
        graphics.DrawLine(pen, icon.Left + 3, icon.Top - 2, icon.Left + 3, icon.Top + 2);
        graphics.DrawLine(pen, icon.Right - 3, icon.Top - 2, icon.Right - 3, icon.Top + 2);
    }
}
