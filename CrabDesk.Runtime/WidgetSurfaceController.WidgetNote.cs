using System.Drawing;
using CrabDesk.Core;

namespace CrabDesk.Runtime;

internal abstract partial class WidgetSurfaceController
{
    protected DesktopWidgetTextInput? _noteInput;
    protected DesktopWidget? _noteEditingWidget;
    protected LayoutRect _noteEditingBounds;
    protected readonly Dictionary<Guid, DesktopWidgetTextVisual> _noteTextVisuals = [];

    protected static RectangleF NoteFieldRectangle(DesktopWidget widget)
    {
        var body = WidgetBodyRectangle(widget);
        return new(body.X, body.Y, body.Width, body.Height - 24);
    }

    protected void BeginWidgetNoteInput(DesktopWidget widget)
    {
        using var widgetView = EnterWidgetView();
        DismissWidgetInputs();
        _runtime.CommitActiveDesktopInlineRename();
        _noteInput ??= new DesktopWidgetTextInput("便签内容");
        _noteEditingWidget = widget;
        _noteEditingBounds = GetWidgetBounds(widget);
        var region = NoteFieldRectangle(widget);
        var location = WidgetPointToScreen(new(region.X, region.Y));
        var appearance = ResolveWidgetSurface(widget);
        var background = ParseOpaqueColor(appearance.Background);
        _noteInput.Owner = this;
        _noteInput.Present(new Rectangle(location, new Size(ToPixel(region.Width), ToPixel(region.Height))), widget.Text,
            background, ResolveAutoTextColor(background), ParseOpaqueColor(appearance.Accent),
            text => _runtime.UpdateWidgetNoteText(widget, text), dismissed: offset =>
            {
                _noteEditingWidget = null;
                if (_resourcesDisposed || IsDisposed || !_runtime.State.Widgets.Contains(widget)) return;
                _widgetScroll[widget.Id] = (float)offset;
                RequestVisualLayerRender();
            }, verticalOffset: _widgetScroll.GetValueOrDefault(widget.Id),
            cornerRadius: Math.Min(8, WidgetCornerRadius),
            contentPadding: new System.Windows.Thickness(6), showFocusBorder: false, borderThickness: 0, typography: widget);
    }

    internal void DismissWidgetInputs()
    {
        using var widgetView = EnterWidgetView();
        DismissControlPopup();
        DismissControlCenterDeviceMenu();
        DismissMicrophone();
        DismissControlCenterMixer();
        DismissTodoDeleteConfirmation();
        DismissWidgetTranslationInput();
        DismissWidgetNoteInput();
        DismissWidgetTodoInput();
    }

    protected void DismissWidgetNoteInput()
    {
        using var widgetView = EnterWidgetView();
        _noteEditingWidget = null;
        if (_noteInput is { IsDisposed: false }) _noteInput.Dismiss();
    }

    protected void RefreshWidgetNoteInput()
    {
        using var widgetView = EnterWidgetView();
        foreach (var id in _noteTextVisuals.Keys.Where(id => !_runtime.State.Widgets.Any(widget => widget.Id == id)).ToArray())
        { _noteTextVisuals[id].Dispose(); _noteTextVisuals.Remove(id); }
        if (_noteEditingWidget is not { } widget) return;
        if (!_runtime.State.Widgets.Contains(widget) || !OwnsActiveWidget(widget) ||
            GetWidgetBounds(widget) != _noteEditingBounds || !HostHasActiveWidgets)
            DismissWidgetNoteInput();
    }
}
