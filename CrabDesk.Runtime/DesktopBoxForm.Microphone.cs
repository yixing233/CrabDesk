using System.Drawing;
using CrabDesk.Core;
using CrabDesk.Native;

namespace CrabDesk.Runtime;

internal sealed partial class DesktopBoxForm
{
    private MicrophonePopup? _microphonePopup;
    private DesktopWidget? _microphoneWidget;
    private LayoutRect _microphoneBounds;

    private void DrawMicrophoneButton(Graphics graphics, DesktopWidget widget, Color text, Color accent, Color background)
    {
        var bounds = MonitorRectangle(widget, ControlCenterPresentation.MicrophoneButton(MonitorBody(widget)));
        var target = ControlCenterPresentation.ControlCenterTarget.Microphone;
        var pressed = IsControlPressed(widget, target);
        var hovered = IsControlHovered(widget, target);
        var ink = ResolveAutoTextColor(background);
        var solid = widget.BackgroundMaterial == WidgetBackgroundMaterial.Solid;
        var fill = Mix(PanelFill(background, ink, solid), ink, solid
            ? pressed ? .12 : hovered || _microphoneWidget == widget ? .08 : .04
            : pressed ? .22 : hovered || _microphoneWidget == widget ? .18 : .14);
        using var path = RoundedRectangle(bounds, 6);
        using var brush = new SolidBrush(fill);
        graphics.FillPath(brush, path);
        var size = pressed ? 13f : 14f;
        LucideRuntimeIcons.Draw(graphics, LucideRuntimeIcon.Mic,
            new(bounds.X + (bounds.Width - size) / 2, bounds.Y + (bounds.Height - size) / 2, size, size), text, size);
    }

    private void ShowMicrophone(DesktopWidget widget)
    {
        if (_microphonePopup is { IsDisposed: false } && _microphoneWidget == widget) { DismissMicrophone(); return; }
        DismissWidgetInputs();
        var appearance = WidgetBoxAppearance(widget, out var acrylic);
        var background = ParseOpaqueColor(appearance.Background);
        var popup = new MicrophonePopup(new MicrophoneController(new Microphone()), UsesLightText(background),
            ParseOpaqueColor(appearance.Accent), _scale, _runtime.ApplyContextMenuTheme, background,
            _runtime.State.Settings.Appearance.AnimationEnabled);
        popup.ApplyPopupMaterial(appearance, _runtime.State.Settings.Appearance, acrylic);
        _microphonePopup = popup;
        _microphoneWidget = widget;
        _microphoneBounds = widget.Bounds;
        popup.FormClosed += (_, _) =>
        {
            if (!ReferenceEquals(_microphonePopup, popup)) return;
            _microphonePopup = null;
            _microphoneWidget = null;
            if (!_resourcesDisposed && !IsDisposed) RequestVisualLayerRender();
        };
        var bounds = MonitorRectangle(widget, ControlCenterPresentation.MicrophoneButton(MonitorBody(widget)));
        popup.ShowAt(this, new Rectangle(PointToScreen(new Point(ToPixel(bounds.X), ToPixel(bounds.Y))),
            new Size(ToPixel(bounds.Width), ToPixel(bounds.Height))));
        RequestVisualLayerRender();
    }

    private void DismissMicrophone()
    {
        _microphonePopup?.Dismiss();
        _microphonePopup = null;
        _microphoneWidget = null;
    }

    private void RefreshMicrophone()
    {
        if (_microphoneWidget is { } widget && (_runtime.IsPaused || !_runtime.State.Widgets.Contains(widget) ||
            widget.MonitorId != _monitor.Id || widget.Bounds != _microphoneBounds)) DismissMicrophone();
    }
}
