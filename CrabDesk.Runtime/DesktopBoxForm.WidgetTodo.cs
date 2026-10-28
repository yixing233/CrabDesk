using System.Drawing;
using CrabDesk.Core;

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

    internal readonly record struct TodoLayout(RectangleF List, RectangleF Add, int Rows);
    internal static TodoLayout GetTodoLayout(DesktopWidget widget)
    {
        var body = WidgetBodyRectangle(widget);
        var list = new RectangleF(body.X, body.Y - 4, body.Width, body.Height + 4);
        var bounds = WidgetRectangle(widget);
        return new(list, new(WidgetMenuRectangle(widget).Left - 28, bounds.Y + 4, 24, 24), Math.Max(1, (int)(list.Height / 28)));
    }

    private int WidgetTodoStart(DesktopWidget widget)
    {
        var extra = _todoEditingWidget == widget && _todoEditingTaskId is null ? 1 : 0;
        return Math.Clamp((int)_widgetScroll.GetValueOrDefault(widget.Id), 0, Math.Max(0, widget.Tasks.Count + extra - GetTodoLayout(widget).Rows));
    }

    private static RectangleF TodoRowRectangle(TodoLayout layout, int row) => new(layout.List.X, layout.List.Y + row * 28, layout.List.Width, 28);
    private RectangleF TodoTextRectangle(DesktopWidget widget, int row)
    {
        var bounds = TodoRowRectangle(GetTodoLayout(widget), row);
        return new(bounds.X + 24, bounds.Y + 1, bounds.Width - 56, 26);
    }

    internal static RectangleF TodoEditRectangle(DesktopWidget widget, int row)
    {
        var bounds = TodoRowRectangle(GetTodoLayout(widget), row);
        return new(bounds.Right - 28, bounds.Y + 2, 24, 24);
    }

    private Guid? TodoTaskAtPoint(DesktopWidget widget, PointF point)
    {
        var layout = GetTodoLayout(widget);
        if (!layout.List.Contains(point)) return null;
        var row = (int)((point.Y - layout.List.Y) / 28);
        var index = WidgetTodoStart(widget) + row;
        return row < layout.Rows && index < widget.Tasks.Count ? widget.Tasks[index].Id : null;
    }

    private void BeginWidgetTodoInput(DesktopWidget widget, WidgetTodoItem? task = null)
    {
        DismissWidgetInputs();
        _runtime.CommitActiveDesktopInlineRename();
        _todoInput ??= new DesktopWidgetTextInput("待办事项", 500);
        _todoInput.InputBox.FontSize = 12;
        _todoInput.InputBox.ToolTip = "Enter 保存 · Esc 取消";
        _todoInput.InputBox.IsUndoEnabled = false;
        _todoInput.InputBox.IsUndoEnabled = true;
        _todoEditingWidget = widget; _todoEditingTaskId = task?.Id; _todoEditingBounds = widget.Bounds;
        _todoInputCancelled = false; _todoDraft = task?.Text ?? string.Empty;
        var layout = GetTodoLayout(widget);
        var index = task is null ? widget.Tasks.Count : widget.Tasks.IndexOf(task);
        var start = WidgetTodoStart(widget);
        if (index < start || index >= start + layout.Rows) start = Math.Max(0, index - layout.Rows + 1);
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
            cancelled: () => _todoInputCancelled = true);
    }

    private void DismissWidgetTodoInput()
    {
        if (_todoInput is { IsDisposed: false }) _todoInput.Dismiss();
        _todoEditingWidget = null;
    }

    private void RefreshWidgetTodoInput()
    {
        if (_todoEditingWidget is not { } widget) return;
        if (!_runtime.State.Widgets.Contains(widget) || widget.MonitorId != _monitor.Id || widget.Bounds != _todoEditingBounds ||
            _runtime.IsPaused || _todoEditingTaskId is { } id && !widget.Tasks.Any(task => task.Id == id)) DismissWidgetTodoInput();
    }

    private void HandleTodoWidgetClick(DesktopWidget widget, PointF point)
    {
        var layout = GetTodoLayout(widget);
        if (layout.Add.Contains(point)) { BeginWidgetTodoInput(widget); return; }
        if (!layout.List.Contains(point)) return;
        var row = (int)((point.Y - layout.List.Y) / 28);
        var index = WidgetTodoStart(widget) + row;
        if (row >= layout.Rows || index >= widget.Tasks.Count) return;
        var task = widget.Tasks[index];
        if (point.X < layout.List.X + 22) _runtime.ToggleWidgetTask(widget, task.Id);
        else if (TodoEditRectangle(widget, row).Contains(point)) BeginWidgetTodoInput(widget, task);
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
        var status = $"{widget.Tasks.Count(task => !task.IsCompleted)} · {widget.Tasks.Count(task => task.IsCompleted)} 完成";
        var headerLeft = bounds.X + 12;
        var headerWidth = layout.Add.Left - headerLeft - 12;
        using var statusFont = new Font(BoxAppearance.DefaultFontFamily, 10.5f, GraphicsUnit.Pixel);
        var statusWidth = Math.Min((float)Math.Ceiling(graphics.MeasureString(status, statusFont).Width), Math.Max(0, headerWidth - 40));
        WidgetText(graphics, widget.DisplayTitle, new(headerLeft, bounds.Y + 4, Math.Max(0, headerWidth - statusWidth - 8), 24), text, 13.333f, bold: true);
        WidgetText(graphics, status, new(layout.Add.Left - 12 - statusWidth, bounds.Y + 4, statusWidth, 24), muted, 10.5f);
        var start = WidgetTodoStart(widget);
        var adding = _todoEditingWidget == widget && _todoEditingTaskId is null;
        if (widget.Tasks.Count == 0 && !adding)
            WidgetText(graphics, "点击顶栏 + 添加待办", new(layout.List.X, layout.List.Y + 6, layout.List.Width, 40), muted, 11.5f, multiline: true);
        for (var row = 0; row < layout.Rows && start + row < widget.Tasks.Count + (adding ? 1 : 0); row++)
        {
            var task = start + row < widget.Tasks.Count ? widget.Tasks[start + row] : null;
            var slot = TodoRowRectangle(layout, row);
            using var rowPath = RoundedRectangle(RectangleF.Inflate(slot, 0, -2), (float)Math.Min(6, _runtime.State.Settings.Appearance.CornerRadius));
            using var rowFill = new SolidBrush(Color.FromArgb(task?.IsCompleted == true ? 5 : 10, text));
            graphics.FillPath(rowFill, rowPath);
            var check = new RectangleF(slot.X + 4, slot.Y + 7, 14, 14);
            using var checkPath = RoundedRectangle(check, (float)Math.Min(4, _runtime.State.Settings.Appearance.IconHoverCornerRadius));
            using var checkPen = new Pen(task?.IsCompleted == true ? accent : Color.FromArgb(150, muted), 1);
            if (task?.IsCompleted == true)
            {
                using var completed = new SolidBrush(accent); graphics.FillPath(completed, checkPath);
                LucideRuntimeIcons.Draw(graphics, LucideRuntimeIcon.Check, check, ResolveAutoTextColor(accent), 11);
            }
            graphics.DrawPath(checkPen, checkPath);
            WidgetText(graphics, task?.Text ?? "输入新的待办…", TodoTextRectangle(widget, row), task?.IsCompleted == true ? Color.FromArgb(150, muted) : text, 12, strike: task?.IsCompleted == true);
            if (task is not null && _hoveredWidgetId == widget.Id && _hoveredTodoTaskId == task.Id &&
                !(_todoEditingWidget == widget && _todoEditingTaskId == task.Id))
                LucideRuntimeIcons.Draw(graphics, LucideRuntimeIcon.Pencil, TodoEditRectangle(widget, row), muted, 12);
        }
        if (widget.Tasks.Count + (adding ? 1 : 0) > layout.Rows)
        {
            var height = layout.List.Height * layout.Rows / (widget.Tasks.Count + (adding ? 1 : 0));
            var y = layout.List.Y + start * (layout.List.Height - height) / Math.Max(1, widget.Tasks.Count + (adding ? 1 : 0) - layout.Rows);
            using var thumb = new SolidBrush(Color.FromArgb(80, text));
            graphics.FillRectangle(thumb, layout.List.Right - 2, y, 2, height);
        }
    }
}
