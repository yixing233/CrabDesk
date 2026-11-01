using System.Drawing;
using CrabDesk.Core;

namespace CrabDesk.Runtime;

internal sealed partial class DesktopBoxForm
{
    private DesktopWidgetTextInput? _noteInput;
    private DesktopWidget? _noteEditingWidget;
    private LayoutRect _noteEditingBounds;
    private readonly Dictionary<Guid, DesktopWidgetTextVisual> _noteTextVisuals = [];

    private static RectangleF NoteFieldRectangle(DesktopWidget widget)
    {
        var body = WidgetBodyRectangle(widget);
        return new(body.X, body.Y, body.Width, body.Height - 24);
    }

    private void BeginWidgetNoteInput(DesktopWidget widget)
    {
        DismissWidgetInputs();
        _runtime.CommitActiveDesktopInlineRename();
        _noteInput ??= new DesktopWidgetTextInput("便签内容");
        _noteEditingWidget = widget;
        _noteEditingBounds = widget.Bounds;
        var region = NoteFieldRectangle(widget);
        var location = PointToScreen(new Point(ToPixel(region.X), ToPixel(region.Y)));
        var appearance = _runtime.State.Boxes.FirstOrDefault()?.Appearance ?? _runtime.State.Settings.DefaultBoxAppearance;
        var background = ParseOpaqueColor(appearance.Background);
        _noteInput.Present(new Rectangle(location, new Size(ToPixel(region.Width), ToPixel(region.Height))), widget.Text,
            background, ResolveAutoTextColor(background), ParseOpaqueColor(appearance.Accent),
            text => _runtime.UpdateWidgetNoteText(widget, text), dismissed: offset =>
            {
                _noteEditingWidget = null;
                if (_resourcesDisposed || IsDisposed || !_runtime.State.Widgets.Contains(widget)) return;
                _widgetScroll[widget.Id] = (float)offset;
                RequestVisualLayerRender();
            }, verticalOffset: _widgetScroll.GetValueOrDefault(widget.Id),
            cornerRadius: Math.Min(8, _runtime.State.Settings.Appearance.CornerRadius),
            contentPadding: new System.Windows.Thickness(6), showFocusBorder: false, borderThickness: 0);
    }

    internal void DismissWidgetInputs()
    {
        DismissTodoDeleteConfirmation();
        DismissWidgetTranslationInput();
        DismissWidgetNoteInput();
        DismissWidgetTodoInput();
    }

    private void DismissWidgetNoteInput()
    {
        _noteEditingWidget = null;
        if (_noteInput is { IsDisposed: false }) _noteInput.Dismiss();
    }

    private void RefreshWidgetNoteInput()
    {
        foreach (var id in _noteTextVisuals.Keys.Where(id => !_runtime.State.Widgets.Any(widget => widget.Id == id)).ToArray())
        { _noteTextVisuals[id].Dispose(); _noteTextVisuals.Remove(id); }
        if (_noteEditingWidget is not { } widget) return;
        if (!_runtime.State.Widgets.Contains(widget) || widget.MonitorId != _monitor.Id ||
            widget.Bounds != _noteEditingBounds || _runtime.IsPaused)
            DismissWidgetNoteInput();
    }
}
