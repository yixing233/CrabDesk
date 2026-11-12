using System.Drawing;
using CrabDesk.Core;
using CrabDesk.Native;

namespace CrabDesk.Runtime;

internal sealed partial class DesktopBoxForm
{
    private AudioMixerPopup? _controlMixerPopup;
    private DesktopWidget? _controlMixerWidget;
    private LayoutRect _controlMixerBounds;

    private void DrawControlCenterMixerButton(Graphics graphics, DesktopWidget widget, RectangleF bounds,
        Color text, Color accent, Color background)
    {
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
        LucideRuntimeIcons.Draw(graphics, LucideRuntimeIcon.SlidersHorizontal,
            new(bounds.X + (bounds.Width - size) / 2, bounds.Y + (bounds.Height - size) / 2, size, size), text, size);
    }

    private void ShowControlCenterMixer(DesktopWidget widget)
    {
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
        _controlMixerBounds = widget.Bounds;
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
        var anchor = new Rectangle(PointToScreen(new Point(ToPixel(bounds.X), ToPixel(bounds.Y))),
            new Size(ToPixel(bounds.Width), ToPixel(bounds.Height)));
        popup.ShowAt(this, anchor);
        RequestVisualLayerRender();
    }

    private void DismissControlCenterMixer()
    {
        _controlMixerPopup?.Dismiss();
        _controlMixerPopup = null;
        _controlMixerWidget = null;
    }

    private void RefreshControlCenterMixer()
    {
        if (_controlMixerWidget is { } widget && (_runtime.IsPaused || !_runtime.State.Widgets.Contains(widget) ||
            widget.MonitorId != _monitor.Id || widget.Bounds != _controlMixerBounds)) DismissControlCenterMixer();
    }
}
