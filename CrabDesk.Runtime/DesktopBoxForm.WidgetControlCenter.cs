using System.Drawing;
using CrabDesk.Core;
using CrabDesk.Native;
using ToolStripMenuItem = System.Windows.Forms.ToolStripMenuItem;

namespace CrabDesk.Runtime;

internal sealed partial class DesktopBoxForm
{
    // Transient interaction state. Pressed is separate from hovered so a click can
    // darken the control under the pointer while the button is held down, which
    // is the only feedback a card without real widgets can give.
    private Guid? _controlHoverWidgetId;
    private ControlCenterPresentation.ControlCenterTarget _controlHoverTarget;
    private ControlCenterPresentation.ControlCenterTarget _controlPressedTarget;
    private Guid? _controlPressedWidgetId;
    private FluentContextMenuStrip? _controlDeviceMenu;
    private DesktopWidget? _controlDeviceWidget;
    private ControlCenterPresentation.ControlCenterTarget _controlDeviceTarget;
    private LayoutRect _controlDeviceBounds;

    /// <summary>Recomputes which control is under the pointer and repaints on change.</summary>
    private void UpdateControlCenterHover(DesktopWidget? widget, PointF point)
    {
        var target = widget?.Kind == DesktopWidgetKind.ControlCenter
            ? ControlCenterTargetAt(widget, point)
            : ControlCenterPresentation.ControlCenterTarget.None;
        if (_controlHoverWidgetId == widget?.Id && _controlHoverTarget == target) return;
        _controlHoverWidgetId = widget?.Id;
        _controlHoverTarget = target;
        RequestVisualLayerRender();
    }

    /// <summary>Hit target that corresponds to a switch.</summary>
    private static ControlCenterPresentation.ControlCenterTarget ControlCenterToggleTarget(ControlCenterToggle toggle) => toggle switch
    {
        ControlCenterToggle.WiFi => ControlCenterPresentation.ControlCenterTarget.ToggleWiFi,
        ControlCenterToggle.Bluetooth => ControlCenterPresentation.ControlCenterTarget.ToggleBluetooth,
        _ => ControlCenterPresentation.ControlCenterTarget.ToggleNightLight
    };

    private bool IsControlHovered(DesktopWidget widget, ControlCenterPresentation.ControlCenterTarget target) =>
        _controlHoverWidgetId == widget.Id && _controlHoverTarget == target;

    private bool IsControlPressed(DesktopWidget widget, ControlCenterPresentation.ControlCenterTarget target) =>
        _controlPressedWidgetId == widget.Id && _controlPressedTarget == target;

    /// <summary>
    /// Draws the control centre: three toggle tiles, two sliders and a media
    /// strip.
    ///
    /// Every control renders as unavailable when the machine offers no reading,
    /// which is why the snapshot's fields are nullable. A toggle drawn "off" on
    /// a machine that cannot toggle it would send the user clicking a dead
    /// target.
    /// </summary>
    private void DrawControlCenterWidget(Graphics graphics, DesktopWidget widget, Color text, Color muted, Color accent,
        Color background)
    {
        var body = MonitorBody(widget);
        var snapshot = widget.Controls;
        // Inner panels paint an opaque shade of the card colour. They used to be
        // a translucent white, which let the wallpaper through and turned a light
        // backdrop into a pale surface that white text could not be read on.
        var surfaceInk = ResolveAutoTextColor(background);
        var panel = PanelFill(background, surfaceInk);

        var actions = ControlQuickActions.For(widget);
        for (var index = 0; index < actions.Count; index++)
        {
            var cell = MonitorRectangle(widget, ControlCenterPresentation.QuickAction(body, index, actions.Count));
            DrawControlCenterQuickAction(graphics, cell, actions[index], snapshot, text, muted, accent, panel, surfaceInk, widget);
        }

        var sliders = MonitorRectangle(widget, ControlCenterPresentation.SliderArea(body));
        var half = (sliders.Width - 14) / 2;
        DrawControlCenterSlider(graphics, new RectangleF(sliders.X, sliders.Y, half, sliders.Height),
            "音量", ControlCenterPresentation.VolumeText(snapshot), snapshot?.VolumePercent, surfaceInk, muted, accent, labelInset: 26, valueInset: 26);
        DrawControlCenterMuteButton(graphics, widget,
            MonitorRectangle(widget, ControlCenterPresentation.VolumeMuteButton(body)), text, muted, accent, background);
        DrawControlCenterMixerButton(graphics, widget,
            MonitorRectangle(widget, ControlCenterPresentation.VolumeMixerButton(body)), text, accent, background);
        DrawControlCenterSlider(graphics, new RectangleF(sliders.X + half + 14, sliders.Y, half, sliders.Height),
            "亮度", ControlCenterPresentation.BrightnessText(snapshot), snapshot?.BrightnessPercent, surfaceInk, muted, accent);

        var displayTarget = ControlCenterPresentation.ControlCenterTarget.BrightnessDisplayRow;
        DrawControlCenterDevice(graphics, MonitorRectangle(widget, ControlCenterPresentation.BrightnessDisplayRow(body)),
            new ControlCenterSnapshot { OutputDeviceName = "亮度 · " + _runtime.ControlBrightnessDisplayLabel(widget) }, text, muted,
            Mix(panel, surfaceInk, IsControlPressed(widget, displayTarget) ? .22 : IsControlHovered(widget, displayTarget) ? .18 : .14),
            leadingIcon: LucideRuntimeIcon.Monitor, forceChoice: true);

        var mediaArea = MonitorRectangle(widget, ControlCenterPresentation.MediaArea(body));
        var outputRow = MonitorRectangle(widget, ControlCenterPresentation.OutputRow(
            ControlCenterPresentation.MediaArea(body)));
        var outputHovered = IsControlHovered(widget, ControlCenterPresentation.ControlCenterTarget.OutputRow);
        var outputPressed = IsControlPressed(widget, ControlCenterPresentation.ControlCenterTarget.OutputRow);
        DrawControlCenterDevice(graphics, outputRow, snapshot, text, muted,
            Mix(panel, surfaceInk, outputPressed ? 0.22 : outputHovered ? 0.18 : 0.14));
        DrawMicrophoneButton(graphics, widget, text, accent, background);
        var panelBounds = MonitorRectangle(widget, ControlCenterPresentation.MediaPanel(
            ControlCenterPresentation.MediaArea(body)));
        DrawControlCenterMedia(graphics, panelBounds, snapshot, text, muted, accent, panel, surfaceInk, widget);
    }

    private void DrawControlCenterQuickAction(Graphics graphics, RectangleF cell, ControlCenterQuickAction action,
        ControlCenterSnapshot? snapshot, Color text, Color muted, Color accent, Color panel, Color surfaceInk, DesktopWidget widgetRef)
    {
        var toggle = ControlQuickActions.Toggle(action);
        var available = toggle is null || ControlCenterPresentation.ToggleAvailable(toggle.Value, snapshot);
        var on = toggle switch
        {
            ControlCenterToggle.WiFi => snapshot?.WiFiEnabled is true,
            ControlCenterToggle.Bluetooth => snapshot?.BluetoothEnabled is true,
            ControlCenterToggle.NightLight => snapshot?.NightLightEnabled is true,
            _ => false
        };
        using var path = RoundedRectangle(cell, 8);
        // An on tile is filled with the accent; an unavailable one is faded so it
        // reads as disabled before the label is even read.
        // Pressed sinks a shade below hovered; an unavailable tile does not react
        // at all, since clicking it does nothing.
        var target = ControlQuickActions.Target(action);
        var details = action == ControlCenterQuickAction.WiFi ? ControlCenterPresentation.ControlCenterTarget.WiFiDetails :
            action == ControlCenterQuickAction.Bluetooth ? ControlCenterPresentation.ControlCenterTarget.BluetoothDetails : ControlCenterPresentation.ControlCenterTarget.None;
        var hovered = available && IsControlHovered(widgetRef, target);
        var pressed = available && IsControlPressed(widgetRef, target);
        var fill = on
            ? (pressed ? Mix(accent, Color.Black, 0.18) : hovered ? Mix(accent, Color.White, 0.12) : accent)
            : Mix(panel, surfaceInk, available ? (pressed ? 0.26 : hovered ? 0.21 : 0.16) : 0.06);
        using var brush = new SolidBrush(fill);
        graphics.FillPath(brush, path);

        var labelInk = !available ? muted : on && widgetRef.ControlCenterLightText is null ? ResolveAutoTextColor(accent) : text;
        // The glyph carries the meaning at a glance; the words underneath say
        // which switch it is and what state it is in.
        var icon = ControlQuickActionIcon(action, snapshot);
        var iconSize = 18f;
        LucideRuntimeIcons.Draw(graphics, icon,
            new RectangleF(cell.X + (cell.Width - iconSize) / 2 - (ControlQuickActions.HasDetails(action) ? 6 : 0), cell.Y + 5, iconSize, iconSize),
            labelInk, iconSize);
        WidgetText(graphics, ControlQuickActions.Label(action),
            new RectangleF(cell.X, cell.Y + 27, cell.Width, 15), labelInk, 11f, bold: true, centered: true);
        WidgetText(graphics, ControlQuickActions.Status(action, snapshot),
            new RectangleF(cell.X + 3, cell.Y + 42, cell.Width - 6, 13), labelInk, 9f, centered: true);
        if (ControlQuickActions.HasDetails(action))
        {
            var bounds = MediaRectangle(ControlCenterPresentation.QuickActionDetails(new(cell.X, cell.Y, cell.Width, cell.Height)));
            if (IsControlHovered(widgetRef, details) || IsControlPressed(widgetRef, details))
            {
                using var hoverPath = RoundedRectangle(RectangleF.Inflate(bounds, -2, -2), 5);
                using var hoverBrush = new SolidBrush(Mix(fill, on ? ResolveAutoTextColor(accent) : surfaceInk, .16));
                graphics.FillPath(hoverBrush, hoverPath);
            }
            LucideRuntimeIcons.Draw(graphics, LucideRuntimeIcon.ChevronRight,
                new(bounds.X + 5, bounds.Y + 6, 10, 12), labelInk, 12);
        }
    }

    private void DrawControlCenterMuteButton(Graphics graphics, DesktopWidget widget, RectangleF bounds,
        Color text, Color muted, Color accent, Color background)
    {
        var enabled = widget.Controls?.VolumeMuted is not null;
        var isMuted = widget.Controls?.VolumeMuted is true;
        var target = ControlCenterPresentation.ControlCenterTarget.VolumeMute;
        var hovered = enabled && IsControlHovered(widget, target);
        var pressed = enabled && IsControlPressed(widget, target);
        if (hovered || pressed)
        {
            var fill = Mix(background, ResolveAutoTextColor(background), pressed ? 0.34 : 0.28);
            using var path = RoundedRectangle(bounds, 6);
            using var brush = new SolidBrush(fill);
            using var border = new Pen(Mix(fill, accent, pressed ? 0.45 : 0.30), 1f);
            graphics.FillPath(brush, path);
            graphics.DrawPath(border, path);
        }
        var size = pressed ? 13f : 14f;
        LucideRuntimeIcons.Draw(graphics, isMuted ? LucideRuntimeIcon.VolumeX : LucideRuntimeIcon.Volume2,
            new RectangleF(bounds.X + (bounds.Width - size) / 2, bounds.Y + (bounds.Height - size) / 2, size, size),
            enabled ? text : Color.FromArgb(90, muted), size);
    }

    private static void DrawControlCenterSlider(Graphics graphics, RectangleF cell, string label, string value,
        double? percent, Color text, Color muted, Color accent, float labelInset = 0, float valueInset = 0)
    {
        WidgetText(graphics, label, new RectangleF(cell.X + labelInset, cell.Y, cell.Width / 2 - labelInset, 16), muted, 11);
        WidgetText(graphics, value, new RectangleF(cell.X + cell.Width / 2, cell.Y, cell.Width / 2 - valueInset, 16), muted, 11, rightAligned: true);
        var track = new RectangleF(cell.X, cell.Y + 24, cell.Width, 5);
        using var trackPath = RoundedRectangle(track, 2.5f);
        using var trackBrush = new SolidBrush(Color.FromArgb(28, text));
        graphics.FillPath(trackBrush, trackPath);
        if (percent is not { } level || !double.IsFinite(level)) return;
        var clamped = (float)ControlCenterPresentation.ClampPercent(level) / 100;
        if (clamped <= 0) return;
        var filled = track; filled.Width *= clamped;
        using var fillPath = RoundedRectangle(filled, 2.5f);
        using var fillBrush = new SolidBrush(accent);
        graphics.FillPath(fillBrush, fillPath);
        // Knob marks the exact position; the bar alone is hard to read at 5 px.
        using var knob = new SolidBrush(accent);
        graphics.FillEllipse(knob, filled.Right - 5, track.Y - 2.5f, 10, 10);
    }

    /// <summary>
    /// The output row doubles as the device switch: clicking it advances to the
    /// next endpoint, so changing output does not need a menu.
    /// </summary>
    private static void DrawControlCenterDevice(Graphics graphics, RectangleF bounds,
        ControlCenterSnapshot? snapshot, Color text, Color muted, Color rowFill, LucideRuntimeIcon? leadingIcon = null, bool forceChoice = false)
    {
        var name = snapshot?.OutputDeviceName is { Length: > 0 } device ? device : "输出设备不可用";
        // A filled row with body-coloured text and a speaker glyph: the previous
        // accent-on-transparent version was unreadable over a light or acrylic
        // backdrop, and gave no hint that the row is clickable.
        using var path = RoundedRectangle(bounds, 6);
        using var brush = new SolidBrush(rowFill);
        graphics.FillPath(brush, path);
        var speaker = new RectangleF(bounds.X + 8, bounds.Y + (bounds.Height - 14) / 2, 14, 14);
        var icon = leadingIcon ?? (name.Contains("耳机") || name.Contains("headphone", StringComparison.OrdinalIgnoreCase) || name.Contains("headset", StringComparison.OrdinalIgnoreCase)
            ? LucideRuntimeIcon.Headphones : LucideRuntimeIcon.Volume2);
        LucideRuntimeIcons.Draw(graphics, icon, speaker, text, 14);
        if (leadingIcon == LucideRuntimeIcon.Monitor)
        {
            using var badge = new SolidBrush(rowFill);
            graphics.FillEllipse(badge, bounds.X + 17, bounds.Y + 13, 10, 10);
            LucideRuntimeIcons.Draw(graphics, LucideRuntimeIcon.Sun, new(bounds.X + 18, bounds.Y + 14, 8, 8), text, 8);
        }
        var hasChoice = forceChoice || snapshot?.OutputDevices.Count > 1;
        var textWidth = Math.Max(0, bounds.Width - 30 - (hasChoice ? 16 : 0));
        WidgetText(graphics, name,
            new RectangleF(bounds.X + 28, bounds.Y, textWidth, bounds.Height), text, 10.5f);
        if (!hasChoice) return;
        // Signals that the row cycles through devices rather than just labelling.
        LucideRuntimeIcons.Draw(graphics, LucideRuntimeIcon.ChevronsUpDown,
            new RectangleF(bounds.Right - 20, bounds.Y + (bounds.Height - 13) / 2, 13, 13), muted, 13);
    }

    private void DrawControlCenterMedia(Graphics graphics, RectangleF bounds,
        ControlCenterSnapshot? snapshot, Color text, Color muted, Color accent, Color fill, Color surfaceInk, DesktopWidget widgetRef)
    {
        using var path = RoundedRectangle(bounds, 8);
        using var brush = new SolidBrush(fill);
        graphics.FillPath(brush, path);

        var media = snapshot?.Media;
        var layout = new LayoutRect(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        DrawControlArtwork(graphics, media?.Artwork, MediaRectangle(ControlCenterPresentation.MediaArtwork(layout)),
            Mix(fill, surfaceInk, .08), muted);
        var title = media is null ? "没有正在播放的媒体" : string.IsNullOrWhiteSpace(media.Title) ? "未知曲目" : media.Title;
        WidgetText(graphics, title, MediaRectangle(ControlCenterPresentation.MediaTitle(layout)), text, 11.5f);
        WidgetText(graphics, media is null ? "" : string.IsNullOrWhiteSpace(media.Artist) ? media.Album : media.Artist,
            MediaRectangle(ControlCenterPresentation.MediaArtist(layout)), muted, 10);

        // Progress: the bar advances from the sampled position plus elapsed wall
        // clock, so it moves smoothly instead of stepping once per poll.
        if (media is { Duration.TotalSeconds: > 0 })
        {
            var now = DateTimeOffset.Now;
            var track = MediaRectangle(ControlCenterPresentation.MediaProgress(layout));
            var displayMedia = _controlSeekWidget == widgetRef.Id && _controlSeekMedia?.TrackId == media.TrackId
                ? media with { Position = _controlSeekPosition, IsPlaying = false } : media;
            using (var trackPath = RoundedRectangle(track, 2))
            using (var trackBrush = new SolidBrush(Color.FromArgb(30, surfaceInk)))
            {
                graphics.FillPath(trackBrush, trackPath);
            }
            var ratio = ControlCenterPresentation.ClampPercent(
                displayMedia.PositionNow(now).TotalSeconds / media.Duration.TotalSeconds * 100) / 100;
            if (ratio > 0)
            {
                var filled = track; filled.Width = (float)(track.Width * ratio);
                using var fillPath = RoundedRectangle(filled, 2);
                using var fillBrush = new SolidBrush(accent);
                graphics.FillPath(fillBrush, fillPath);
            }
            if (media.CanSeek)
            {
                var size = IsControlHovered(widgetRef, ControlCenterPresentation.ControlCenterTarget.MediaProgress) ||
                    IsControlPressed(widgetRef, ControlCenterPresentation.ControlCenterTarget.MediaProgress) ? 8f : 6f;
                using var knob = new SolidBrush(accent);
                graphics.FillEllipse(knob, track.X + (float)(track.Width * ratio) - size / 2, track.Y + 2 - size / 2, size, size);
            }
            WidgetText(graphics, ControlCenterPresentation.MediaTimeText(displayMedia, now),
                MediaRectangle(ControlCenterPresentation.MediaTime(layout)), muted, 9, rightAligned: true);
        }

        // Three transport buttons, drawn as glyphs: the card ships no icon font.
        var buttons = ControlCenterMediaButtons(bounds);
        var enabled = media is not null;
        DrawControlCenterMediaButton(graphics, buttons[0], LucideRuntimeIcon.SkipBack,
            enabled && media!.CanPrevious, text, muted, accent, fill, surfaceInk, widgetRef,
            ControlCenterPresentation.ControlCenterTarget.MediaPrevious);
        DrawControlCenterMediaButton(graphics, buttons[1],
            media?.IsPlaying is true ? LucideRuntimeIcon.Pause : LucideRuntimeIcon.Play,
            enabled && (media?.CanPlayPause ?? false), text, muted, accent, fill, surfaceInk, widgetRef,
            ControlCenterPresentation.ControlCenterTarget.MediaPlayPause);
        DrawControlCenterMediaButton(graphics, buttons[2], LucideRuntimeIcon.SkipForward,
            enabled && media!.CanNext, text, muted, accent, fill, surfaceInk, widgetRef,
            ControlCenterPresentation.ControlCenterTarget.MediaNext);
    }

    private void DrawControlCenterMediaButton(Graphics graphics, RectangleF bounds,
        LucideRuntimeIcon icon, bool enabled, Color text, Color muted, Color accent, Color fill, Color surfaceInk,
        DesktopWidget widgetRef, ControlCenterPresentation.ControlCenterTarget target)
    {
        var hovered = enabled && IsControlHovered(widgetRef, target);
        var pressed = enabled && IsControlPressed(widgetRef, target);
        if (hovered || pressed)
        {
            // Center a pill inside the hit target, with room between buttons.
            var surface = RectangleF.Inflate(bounds, -3, -1);
            var surfaceFill = Mix(fill, surfaceInk, pressed ? 0.18 : 0.10);
            using var path = RoundedRectangle(surface, surface.Height / 2);
            using var brush = new SolidBrush(surfaceFill);
            using var border = new Pen(Mix(surfaceFill, accent, pressed ? 0.45 : 0.30), 1f);
            graphics.FillPath(brush, path);
            graphics.DrawPath(border, path);
        }
        var iconInk = widgetRef.ControlCenterLightText is not null ? text : accent;
        var ink = !enabled ? Color.FromArgb(90, muted)
            : hovered || pressed ? Mix(iconInk, text, 0.20) : iconInk;
        var size = pressed ? 14f : 15f;
        LucideRuntimeIcons.Draw(graphics, icon,
            new RectangleF(bounds.X + (bounds.Width - size) / 2, bounds.Y + (bounds.Height - size) / 2, size, size),
            ink, size);
    }

    /// <summary>Opaque shade of the card colour, used as the surface for inner panels.</summary>
    private static Color PanelFill(Color background, Color text) => Mix(background, text, 0.10);

    /// <summary>Linear blend of two colours, always opaque.</summary>
    private static Color Mix(Color from, Color to, double amount)
    {
        var t = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(255,
            (int)Math.Round(from.R + (to.R - from.R) * t),
            (int)Math.Round(from.G + (to.G - from.G) * t),
            (int)Math.Round(from.B + (to.B - from.B) * t));
    }

    /// <summary>Icon for a switch, matched to its state so an off radio reads as off.</summary>
    private static LucideRuntimeIcon ControlCenterToggleIcon(ControlCenterToggle toggle, bool on) => toggle switch
    {
        ControlCenterToggle.WiFi => on ? LucideRuntimeIcon.Wifi : LucideRuntimeIcon.WifiOff,
        // Bluetooth has no off variant in the font, so the same glyph serves both
        // states and the tile's fill carries the difference.
        ControlCenterToggle.Bluetooth => LucideRuntimeIcon.Bluetooth,
        _ => LucideRuntimeIcon.Moon
    };

    /// <summary>
    /// Hit rectangles of the three transport buttons. Shared with the click
    /// handler so the drawn glyph and its target cannot drift apart.
    /// </summary>
    private static RectangleF[] ControlCenterMediaButtons(RectangleF mediaBounds)
    {
        return ControlCenterPresentation.MediaButtonRects(new(mediaBounds.X, mediaBounds.Y, mediaBounds.Width, mediaBounds.Height))
            .Select(MediaRectangle).ToArray();
    }

    /// <summary>
    /// Routes a click inside the control centre to the control under the pointer.
    /// Returns true when the click was consumed, so it does not fall through to
    /// dragging the card.
    /// </summary>
    /// <summary>
    /// Records which control is being held down, so it can render a pressed state
    /// between mouse-down and mouse-up.
    /// </summary>
    /// <summary>
    /// Which control a screen point is over. The body grid is laid out in
    /// card-local coordinates, so the card origin is removed once here and the
    /// same helper serves drawing, hover and clicks.
    /// </summary>
    private ControlCenterPresentation.ControlCenterTarget ControlCenterTargetAt(DesktopWidget widget, PointF point)
    {
        var bounds = WidgetRectangle(widget);
        var body = WidgetMonitorPresentation.Body(bounds.Width, bounds.Height,
            _runtime.State.Settings.Appearance.CornerRadius);
        return ControlCenterPresentation.TargetAt(body, point.X - bounds.X, point.Y - bounds.Y, ControlQuickActions.For(widget));
    }

    private bool BeginControlCenterPress(DesktopWidget widget, PointF point)
    {
        var target = ControlCenterTargetAt(widget, point);
        if (target == ControlCenterPresentation.ControlCenterTarget.None) return false;
        _controlPressedWidgetId = widget.Id;
        _controlPressedTarget = target;
        if (target == ControlCenterPresentation.ControlCenterTarget.MediaProgress) BeginControlSeek(widget, point);
        RequestVisualLayerRender();
        return true;
    }

    /// <summary>
    /// True for controls that capture the pointer instead of dragging the card:
    /// the volume and brightness sliders.
    /// </summary>
    private bool IsControlCenterDragTarget(DesktopWidget widget, PointF point)
    {
        var target = ControlCenterTargetAt(widget, point);
        return target is ControlCenterPresentation.ControlCenterTarget.VolumeSlider
            or ControlCenterPresentation.ControlCenterTarget.BrightnessSlider or ControlCenterPresentation.ControlCenterTarget.MediaProgress;
    }

    /// <summary>
    /// Continues a slider drag. Called for every mouse move while the button is
    /// held, so the level follows the pointer instead of only jumping on click.
    /// </summary>
    private bool UpdateControlCenterDrag(DesktopWidget widget, PointF point)
    {
        var target = _controlPressedWidgetId == widget.Id
            ? _controlPressedTarget
            : ControlCenterTargetAt(widget, point);
        if (target == ControlCenterPresentation.ControlCenterTarget.MediaProgress) { UpdateControlSeek(widget, point); return true; }
        if (target is not (ControlCenterPresentation.ControlCenterTarget.VolumeSlider
            or ControlCenterPresentation.ControlCenterTarget.BrightnessSlider)) return false;
        var body = WidgetMonitorPresentation.Body(
            WidgetRectangle(widget).Width, WidgetRectangle(widget).Height, _runtime.State.Settings.Appearance.CornerRadius);
        var sliders = ControlCenterPresentation.SliderArea(body);
        var half = (sliders.Width - 14) / 2;
        var bounds = WidgetRectangle(widget);
        var local = new PointF(point.X - bounds.X, point.Y - bounds.Y);
        if (target == ControlCenterPresentation.ControlCenterTarget.VolumeSlider)
        {
            _runtime.SetControlVolume(PercentFromPoint(new LayoutRect(sliders.X, sliders.Y, half, sliders.Height), local));
            return true;
        }
        _runtime.SetControlBrightness(PercentFromPoint(
            new LayoutRect(sliders.X + half + 14, sliders.Y, half, sliders.Height), local));
        return true;
    }

    private void EndControlCenterPress()
    {
        _controlSeekMedia = null;
        _controlSeekWidget = null;
        if (_controlPressedWidgetId is null) return;
        _controlPressedWidgetId = null;
        _controlPressedTarget = ControlCenterPresentation.ControlCenterTarget.None;
        RequestVisualLayerRender();
    }

    private bool HandleControlCenterClick(DesktopWidget widget, PointF point)
    {
        if (ConsumeClosedControlPopupClick(widget, point)) return true;
        if (HandleControlQuickAction(widget, point)) return true;
        var displayRow = MonitorRectangle(widget, ControlCenterPresentation.BrightnessDisplayRow(MonitorBody(widget)));
        if (displayRow.Contains(point))
        {
            ToggleControlCenterDeviceMenu(widget, ControlCenterPresentation.ControlCenterTarget.BrightnessDisplayRow, displayRow);
            return true;
        }
        var body = MonitorBody(widget);
        var snapshot = widget.Controls;

        if (MonitorRectangle(widget, ControlCenterPresentation.MicrophoneButton(body)).Contains(point))
        {
            ShowMicrophone(widget);
            return true;
        }

        if (MonitorRectangle(widget, ControlCenterPresentation.VolumeMixerButton(body)).Contains(point))
        {
            ShowControlCenterMixer(widget);
            return true;
        }

        if (MonitorRectangle(widget, ControlCenterPresentation.VolumeMuteButton(body)).Contains(point))
        {
            if (snapshot?.VolumeMuted is { } isMuted) _runtime.SetControlMuted(!isMuted);
            return true;
        }

        var sliders = ControlCenterPresentation.SliderArea(body);
        var half = (sliders.Width - 14) / 2;
        // Only the caption and track respond; the padding below them is dead space
        // and clicking it used to change the level by accident.
        var localPoint = new PointF(point.X - (float)widget.Bounds.X, point.Y - (float)widget.Bounds.Y);
        var volumeColumn = new LayoutRect(sliders.X, sliders.Y, half, sliders.Height);
        if (ControlCenterPresentation.SliderHitArea(volumeColumn).Contains(localPoint.X, localPoint.Y) &&
            snapshot?.VolumePercent is not null)
        {
            _runtime.SetControlVolume(PercentFromPoint(volumeColumn, localPoint));
            return true;
        }
        var brightnessColumn = new LayoutRect(sliders.X + half + 14, sliders.Y, half, sliders.Height);
        if (ControlCenterPresentation.SliderHitArea(brightnessColumn).Contains(localPoint.X, localPoint.Y) &&
            snapshot?.BrightnessPercent is not null)
        {
            _runtime.SetControlBrightness(PercentFromPoint(brightnessColumn, localPoint));
            return true;
        }

        var mediaArea = MonitorRectangle(widget, ControlCenterPresentation.MediaArea(body));
        var outputRow = MonitorRectangle(widget, ControlCenterPresentation.OutputRow(
            ControlCenterPresentation.MediaArea(body)));
        if (outputRow.Contains(point))
        {
            ToggleControlCenterDeviceMenu(widget, ControlCenterPresentation.ControlCenterTarget.OutputRow, outputRow);
            return true;
        }
        var mediaBounds = MonitorRectangle(widget, ControlCenterPresentation.MediaPanel(
            ControlCenterPresentation.MediaArea(body)));
        if (snapshot?.Media is { } media)
        {
            var buttons = ControlCenterMediaButtons(mediaBounds);
            for (var index = 0; index < buttons.Length; index++)
            {
                if (!buttons[index].Contains(point)) continue;
                var command = index switch
                {
                    0 => ControlCenterMediaCommand.Previous,
                    1 => ControlCenterMediaCommand.PlayPause,
                    _ => ControlCenterMediaCommand.Next
                };
                var allowed = command switch
                {
                    ControlCenterMediaCommand.Previous => media.CanPrevious,
                    ControlCenterMediaCommand.Next => media.CanNext,
                    _ => media.CanPlayPause
                };
                if (allowed) _ = _runtime.SendControlMediaCommandAsync(command);
                return true;
            }
        }
        return false;
    }

    /// <summary>Maps a point on a slider column to a 0-100 level.</summary>
    private static double PercentFromPoint(LayoutRect area, PointF point)
    {
        if (area.Width <= 0) return 0;
        return ControlCenterPresentation.ClampPercent((point.X - area.X) / area.Width * 100);
    }

    internal ToolStripMenuItem BuildControlCenterInkMenu(DesktopWidget widget)
    {
        var appearance = _runtime.State.Boxes.FirstOrDefault()?.Appearance ?? _runtime.State.Settings.DefaultBoxAppearance;
        var lightText = widget.ControlCenterLightText ?? UsesLightText(ParseOpaqueColor(appearance.Background));
        var menu = new FluentToolStripMenuItem("文字和图标颜色");
        foreach (var light in new[] { true, false })
        {
            var item = new FluentToolStripMenuItem(light ? "浅色" : "深色") { Checked = lightText == light };
            item.Click += (_, _) =>
            {
                widget.ControlCenterLightText = light;
                foreach (ToolStripMenuItem choice in menu.DropDownItems) choice.Checked = ReferenceEquals(choice, item);
                _runtime.RefreshWidgetPosition(widget, save: true);
            };
            menu.DropDownItems.Add(item);
        }
        return menu;
    }

    private void ToggleControlCenterDeviceMenu(DesktopWidget widget,
        ControlCenterPresentation.ControlCenterTarget target, RectangleF anchor)
    {
        var closingCurrent = _controlDeviceMenu is { IsDisposed: false, Visible: true } &&
            _controlDeviceWidget == widget && _controlDeviceTarget == target;
        DismissControlCenterDeviceMenu();
        if (closingCurrent) return;

        var isDisplay = target == ControlCenterPresentation.ControlCenterTarget.BrightnessDisplayRow;
        var choices = isDisplay ? new ControlCenterSnapshot
        {
            OutputDeviceId = string.IsNullOrEmpty(widget.ControlBrightnessDisplayId) ? widget.MonitorId : widget.ControlBrightnessDisplayId,
            OutputDevices = _runtime.ControlBrightnessDisplays.Select(display => new ControlCenterOutputDevice(display.Id, display.Label, false)).ToArray()
        } : widget.Controls;
        if (choices is null) return;
        var menu = BuildControlCenterOutputMenu(choices, (float)_scale,
            isDisplay ? id => _runtime.SelectControlBrightnessDisplayAsync(widget, id) : _runtime.SetControlOutputAsync);
        if (menu.Items.Count == 0) { menu.Dispose(); return; }
        _controlDeviceMenu = menu;
        _controlDeviceWidget = widget;
        _controlDeviceTarget = target;
        _controlDeviceBounds = widget.Bounds;
        // The outside-click hook consumes a second press on this row before it
        // can reach the surface and reopen the menu on mouse-up.
        menu.ToggleSourceBounds = new Rectangle(PointToScreen(new Point(ToPixel(anchor.X), ToPixel(anchor.Y))),
            new Size(ToPixel(anchor.Width), ToPixel(anchor.Height)));
        menu.Opening += (_, _) => _runtime.ApplyContextMenuTheme(menu);
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_controlDeviceMenu, menu))
            {
                _controlDeviceMenu = null;
                _controlDeviceWidget = null;
            }
            // Dispose after the close animation has unwound.
            if (!IsDisposed && IsHandleCreated) BeginInvoke((Action)menu.Dispose);
            else menu.Dispose();
        };
        menu.Show(this, new Point(ToPixel(anchor.X), ToPixel(anchor.Bottom)));
    }

    private void DismissControlCenterDeviceMenu()
    {
        var menu = _controlDeviceMenu;
        _controlDeviceMenu = null;
        _controlDeviceWidget = null;
        if (menu is not { IsDisposed: false }) return;
        menu.ToggleSourceBounds = null;
        menu.Close();
    }

    private void RefreshControlCenterDeviceMenu()
    {
        if (_controlDeviceWidget is { } widget && (_runtime.IsPaused || !_runtime.State.Widgets.Contains(widget) ||
            widget.MonitorId != _monitor.Id || widget.Bounds != _controlDeviceBounds)) DismissControlCenterDeviceMenu();
    }

    /// <summary>
    /// Builds the endpoint list. Split out from the popup so the contents can be
    /// tested without a window to host it.
    /// </summary>
    internal static FluentContextMenuStrip BuildControlCenterOutputMenu(ControlCenterSnapshot snapshot,
        float dpiScale = 1f, Func<string, Task>? selectOutput = null)
    {
        var menu = new FluentContextMenuStrip { PreferredDpiScale = dpiScale };
        foreach (var device in snapshot.OutputDevices)
        {
            // The tick marks what the card is showing, which during a switch can
            // briefly differ from what the OS reports as default.
            var current = string.Equals(device.Id, snapshot.OutputDeviceId, StringComparison.OrdinalIgnoreCase);
            var item = new ToolStripMenuItem(device.Name) { Checked = current };
            var id = device.Id;
            item.Click += async (_, _) => { if (selectOutput is not null) await selectOutput(id); };
            menu.Items.Add(item);
        }
        return menu;
    }
}
