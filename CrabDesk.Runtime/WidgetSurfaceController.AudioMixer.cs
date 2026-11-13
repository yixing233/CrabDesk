using System.Drawing;
using CrabDesk.Core;
using CrabDesk.Native;

namespace CrabDesk.Runtime;

internal abstract partial class WidgetSurfaceController
{
    protected AudioMixerPopup? _controlMixerPopup;
    protected DesktopWidget? _controlMixerWidget;
    protected LayoutRect _controlMixerBounds;

    protected void DrawControlCenterMixerButton(Graphics graphics, DesktopWidget widget, RectangleF bounds,
        Color text, Color accent, Color background)
    {
        using var widgetView = EnterWidgetView();
        var target = ControlCenterPresentation.ControlCenterTarget.VolumeMixer;
        var hovered = IsControlHovered(widget, target);
        var pressed = IsControlPressed(widget, target);
        if (hovered || pressed || _controlMixerWidget == widget)
        {
            var fill = Mix(background, ResolveAutoTextColor(background), pressed ? 0.34 : 0.28);
            using var path = RoundedRectangle(bounds, 6);
            using var brush = new SolidBrush(fill);
            using var border = new Pen(Mix(fill, accent, 0.30), 1f);
            graphics.FillPath(brush, path);
            graphics.DrawPath(border, path);
        }
        var size = pressed ? 13f : 14f;
        TablerRuntimeIcons.Draw(graphics, TablerRuntimeIcon.SlidersHorizontal,
            new(bounds.X + (bounds.Width - size) / 2, bounds.Y + (bounds.Height - size) / 2, size, size), text, size);
    }

    protected void ShowControlCenterMixer(DesktopWidget widget)
    {
        using var widgetView = EnterWidgetView();
        if (_controlMixerPopup is { IsDisposed: false } && _controlMixerWidget == widget)
        {
            DismissControlCenterMixer();
            return;
        }
        DismissWidgetInputs();
        var appearance = WidgetBoxAppearance(widget, out var acrylic);
        var background = ParseOpaqueColor(appearance.Background);
        var popup = new AudioMixerPopup(new AudioMixerController(new AudioMixer()), UsesLightText(background),
            ParseOpaqueColor(appearance.Accent), _scale, background, _runtime.State.Settings.Appearance.AnimationEnabled);
        popup.ApplyPopupMaterial(appearance, _runtime.State.Settings.Appearance, acrylic);
        _controlMixerPopup = popup;
        _controlMixerWidget = widget;
        _controlMixerBounds = GetWidgetBounds(widget);
        popup.FormClosed += (_, _) =>
        {
            if (!ReferenceEquals(_controlMixerPopup, popup)) return;
            _controlMixerPopup = null;
            _controlMixerWidget = null;
            if (_resourcesDisposed || IsDisposed) return;
            RequestVisualLayerRender();
            _ = _runtime.RefreshMonitorWidgetsAsync();
        };
        var bounds = MonitorRectangle(widget, ControlCenterPresentation.VolumeMixerButton(MonitorBody(widget)));
        var anchor = new Rectangle(WidgetPointToScreen(new(bounds.X, bounds.Y)),
            new Size(ToPixel(bounds.Width), ToPixel(bounds.Height)));
        popup.ShowAt(this, anchor);
        RequestVisualLayerRender();
    }

    protected void DismissControlCenterMixer()
    {
        using var widgetView = EnterWidgetView();
        _controlMixerPopup?.Dismiss();
        _controlMixerPopup = null;
        _controlMixerWidget = null;
    }

    protected void RefreshControlCenterMixer()
    {
        using var widgetView = EnterWidgetView();
        if (_controlMixerWidget is { } widget && (!HostHasActiveWidgets || !_runtime.State.Widgets.Contains(widget) ||
            !OwnsActiveWidget(widget) || GetWidgetBounds(widget) != _controlMixerBounds)) DismissControlCenterMixer();
    }
}
