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
        var panel = PanelFill(background, text);

        foreach (var toggle in Enum.GetValues<ControlCenterToggle>())
        {
            var cell = MonitorRectangle(widget, ControlCenterPresentation.Toggle(body, (int)toggle));
            DrawControlCenterToggle(graphics, cell, toggle, snapshot, text, muted, accent, panel, widget);
        }

        var sliders = MonitorRectangle(widget, ControlCenterPresentation.SliderArea(body));
        var half = (sliders.Width - 14) / 2;
        DrawControlCenterSlider(graphics, new RectangleF(sliders.X, sliders.Y, half, sliders.Height),
            "音量", ControlCenterPresentation.VolumeText(snapshot), snapshot?.VolumePercent, text, muted, accent);
        DrawControlCenterSlider(graphics, new RectangleF(sliders.X + half + 14, sliders.Y, half, sliders.Height),
            "亮度", ControlCenterPresentation.BrightnessText(snapshot), snapshot?.BrightnessPercent, text, muted, accent);

        var mediaArea = MonitorRectangle(widget, ControlCenterPresentation.MediaArea(body));
        var outputRow = MonitorRectangle(widget, ControlCenterPresentation.OutputRow(
            ControlCenterPresentation.MediaArea(body)));
        var outputHovered = IsControlHovered(widget, ControlCenterPresentation.ControlCenterTarget.OutputRow);
        var outputPressed = IsControlPressed(widget, ControlCenterPresentation.ControlCenterTarget.OutputRow);
        DrawControlCenterDevice(graphics, outputRow, snapshot, text, muted,
            Mix(panel, text, outputPressed ? 0.22 : outputHovered ? 0.18 : 0.14));
        var panelBounds = MonitorRectangle(widget, ControlCenterPresentation.MediaPanel(
            ControlCenterPresentation.MediaArea(body)));
        DrawControlCenterMedia(graphics, panelBounds, snapshot, text, muted, accent, Mix(background, text, 0.10), widget);

        // Contrast switch, top right. The card sits over a wallpaper it does not
        // control, so the automatic light/dark choice can still read badly; this
        // lets the ink be set by hand.
        var toggleBounds = ControlCenterInkToggleRectangle(widget);
        using (var togglePath = RoundedRectangle(toggleBounds, 6))
        using (var toggleBrush = new SolidBrush(Mix(background, text, 0.18)))
        {
            graphics.FillPath(toggleBrush, togglePath);
        }
        LucideRuntimeIcons.Draw(graphics,
            widget.ControlCenterLightText is true ? LucideRuntimeIcon.Sun : LucideRuntimeIcon.Moon,
            new RectangleF(toggleBounds.X + (toggleBounds.Width - 13) / 2,
                toggleBounds.Y + (toggleBounds.Height - 13) / 2, 13, 13), text, 13);
    }

    /// <summary>Hit area of the ink-contrast button, shared by drawing and clicking.</summary>
    private static RectangleF ControlCenterInkToggleRectangle(DesktopWidget widget)
    {
        var bounds = WidgetRectangle(widget);
        return new RectangleF(bounds.Right - 52, bounds.Y + 6, 22, 20);
    }

    private void DrawControlCenterToggle(Graphics graphics, RectangleF cell, ControlCenterToggle toggle,
        ControlCenterSnapshot? snapshot, Color text, Color muted, Color accent, Color panel, DesktopWidget widgetRef)
    {
        var available = ControlCenterPresentation.ToggleAvailable(toggle, snapshot);
        var on = toggle switch
        {
            ControlCenterToggle.WiFi => snapshot?.WiFiEnabled is true,
            ControlCenterToggle.Bluetooth => snapshot?.BluetoothEnabled is true,
            _ => snapshot?.NightLightEnabled is true
        };
        using var path = RoundedRectangle(cell, 8);
        // An on tile is filled with the accent; an unavailable one is faded so it
        // reads as disabled before the label is even read.
        // Pressed sinks a shade below hovered; an unavailable tile does not react
        // at all, since clicking it does nothing.
        var hovered = available && IsControlHovered(widgetRef, ControlCenterToggleTarget(toggle));
        var pressed = available && IsControlPressed(widgetRef, ControlCenterToggleTarget(toggle));
        var fill = on
            ? (pressed ? Mix(accent, Color.Black, 0.18) : hovered ? Mix(accent, Color.White, 0.12) : accent)
            : Mix(panel, text, available ? (pressed ? 0.26 : hovered ? 0.21 : 0.16) : 0.06);
        using var brush = new SolidBrush(fill);
        graphics.FillPath(brush, path);

        var labelInk = !available ? muted : on ? ResolveAutoTextColor(accent) : text;
        // The glyph carries the meaning at a glance; the words underneath say
        // which switch it is and what state it is in.
        var icon = ControlCenterToggleIcon(toggle, on);
        var iconSize = 18f;
        LucideRuntimeIcons.Draw(graphics, icon,
            new RectangleF(cell.X + (cell.Width - iconSize) / 2, cell.Y + 5, iconSize, iconSize),
            labelInk, iconSize);
        WidgetText(graphics, ControlCenterPresentation.ToggleLabel(toggle),
            new RectangleF(cell.X, cell.Y + 27, cell.Width, 15), labelInk, 11f, bold: true, centered: true);
        WidgetText(graphics, ControlCenterPresentation.ToggleStateText(toggle, snapshot),
            new RectangleF(cell.X, cell.Y + 42, cell.Width, 13), labelInk, 9f, centered: true);
    }

    private static void DrawControlCenterSlider(Graphics graphics, RectangleF cell, string label, string value,
        double? percent, Color text, Color muted, Color accent)
    {
        WidgetText(graphics, label, new RectangleF(cell.X, cell.Y, cell.Width / 2, 16), muted, 11);
        WidgetText(graphics, value, new RectangleF(cell.X + cell.Width / 2, cell.Y, cell.Width / 2, 16), muted, 11, rightAligned: true);
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
        ControlCenterSnapshot? snapshot, Color text, Color muted, Color rowFill)
    {
        var name = snapshot?.OutputDeviceName is { Length: > 0 } device ? device : "输出设备不可用";
        // A filled row with body-coloured text and a speaker glyph: the previous
        // accent-on-transparent version was unreadable over a light or acrylic
        // backdrop, and gave no hint that the row is clickable.
        using var path = RoundedRectangle(bounds, 6);
        using var brush = new SolidBrush(rowFill);
        graphics.FillPath(brush, path);
        var speaker = new RectangleF(bounds.X + 8, bounds.Y + (bounds.Height - 14) / 2, 14, 14);
        LucideRuntimeIcons.Draw(graphics, LucideRuntimeIcon.MonitorSpeaker, speaker, text, 14);
        var hasChoice = snapshot?.OutputDevices.Count > 1;
        var textWidth = Math.Max(0, bounds.Width - 30 - (hasChoice ? 16 : 0));
        WidgetText(graphics, WidgetMonitorMetrics.HardwareLabel(name, 24),
            new RectangleF(bounds.X + 28, bounds.Y, textWidth, bounds.Height), text, 10.5f);
        if (!hasChoice) return;
        // Signals that the row cycles through devices rather than just labelling.
        LucideRuntimeIcons.Draw(graphics, LucideRuntimeIcon.ChevronsUpDown,
            new RectangleF(bounds.Right - 20, bounds.Y + (bounds.Height - 13) / 2, 13, 13), muted, 13);
    }

    private void DrawControlCenterMedia(Graphics graphics, RectangleF bounds,
        ControlCenterSnapshot? snapshot, Color text, Color muted, Color accent, Color fill, DesktopWidget widgetRef)
    {
        using var path = RoundedRectangle(bounds, 8);
        using var brush = new SolidBrush(fill);
        graphics.FillPath(brush, path);

        var media = snapshot?.Media;
        WidgetText(graphics, ControlCenterPresentation.MediaSummary(snapshot),
            new RectangleF(bounds.X + 10, bounds.Y + 8, bounds.Width - 20, 18), text, 11.5f);

        // Progress: the bar advances from the sampled position plus elapsed wall
        // clock, so it moves smoothly instead of stepping once per poll.
        if (media is { Duration.TotalSeconds: > 0 })
        {
            var now = DateTimeOffset.Now;
            var track = new RectangleF(bounds.X + 10, bounds.Y + 33, bounds.Width - 20, 4);
            using (var trackPath = RoundedRectangle(track, 2))
            using (var trackBrush = new SolidBrush(Color.FromArgb(30, text)))
            {
                graphics.FillPath(trackBrush, trackPath);
            }
            var ratio = ControlCenterPresentation.ClampPercent(
                media.PositionNow(now).TotalSeconds / media.Duration.TotalSeconds * 100) / 100;
            if (ratio > 0)
            {
                var filled = track; filled.Width = (float)(track.Width * ratio);
                using var fillPath = RoundedRectangle(filled, 2);
                using var fillBrush = new SolidBrush(accent);
                graphics.FillPath(fillBrush, fillPath);
            }
            WidgetText(graphics, ControlCenterPresentation.MediaTimeText(media, now),
                new RectangleF(bounds.X + 10, bounds.Y + 39, bounds.Width - 20, 14), muted, 9.5f, rightAligned: true);
        }

        // Three transport buttons, drawn as glyphs: the card ships no icon font.
        var buttons = ControlCenterMediaButtons(bounds);
        var enabled = media is not null;
        DrawControlCenterMediaButton(graphics, buttons[0], LucideRuntimeIcon.SkipBack,
            enabled && media!.CanPrevious, muted, accent);
        DrawControlCenterMediaButton(graphics, buttons[1],
            media?.IsPlaying is true ? LucideRuntimeIcon.Pause : LucideRuntimeIcon.Play,
            enabled && (media?.CanPlayPause ?? false), muted, accent);
        DrawControlCenterMediaButton(graphics, buttons[2], LucideRuntimeIcon.SkipForward,
            enabled && media!.CanNext, muted, accent);
    }

    private static void DrawControlCenterMediaButton(Graphics graphics, RectangleF bounds,
        LucideRuntimeIcon icon, bool enabled, Color muted, Color accent)
    {
        var ink = enabled ? accent : Color.FromArgb(90, muted);
        const float size = 15f;
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
        const float buttonWidth = 34f;
        var startX = mediaBounds.X + (mediaBounds.Width - buttonWidth * 3) / 2;
        var top = mediaBounds.Y + mediaBounds.Height - 22;
        return
        [
            new RectangleF(startX, top, buttonWidth, 22),
            new RectangleF(startX + buttonWidth, top, buttonWidth, 22),
            new RectangleF(startX + buttonWidth * 2, top, buttonWidth, 22)
        ];
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
        if (ControlCenterInkToggleRectangle(widget).Contains(point))
            return ControlCenterPresentation.ControlCenterTarget.InkToggle;
        var bounds = WidgetRectangle(widget);
        var body = WidgetMonitorPresentation.Body(bounds.Width, bounds.Height,
            _runtime.State.Settings.Appearance.CornerRadius);
        return ControlCenterPresentation.TargetAt(body, point.X - bounds.X, point.Y - bounds.Y);
    }

    private bool BeginControlCenterPress(DesktopWidget widget, PointF point)
    {
        var target = ControlCenterTargetAt(widget, point);
        if (target == ControlCenterPresentation.ControlCenterTarget.None) return false;
        _controlPressedWidgetId = widget.Id;
        _controlPressedTarget = target;
        RequestVisualLayerRender();
        return true;
    }

    private void EndControlCenterPress()
    {
        if (_controlPressedWidgetId is null) return;
        _controlPressedWidgetId = null;
        _controlPressedTarget = ControlCenterPresentation.ControlCenterTarget.None;
        RequestVisualLayerRender();
    }

    private bool HandleControlCenterClick(DesktopWidget widget, PointF point)
    {
        if (ControlCenterInkToggleRectangle(widget).Contains(point))
        {
            // Two states only: white ink, or dark ink. The first click takes the
            // card off the automatic choice rather than trying to cycle back into
            // it, which would be a third state the button cannot show.
            widget.ControlCenterLightText = widget.ControlCenterLightText != true;
            _runtime.RefreshWidgetPosition(widget, save: true);
            return true;
        }
        var body = MonitorBody(widget);
        var snapshot = widget.Controls;

        foreach (var toggle in Enum.GetValues<ControlCenterToggle>())
        {
            var cell = MonitorRectangle(widget, ControlCenterPresentation.Toggle(body, (int)toggle));
            if (!cell.Contains(point)) continue;
            // An unavailable toggle is still consumed: falling through would
            // start a drag, which is not what clicking a button should do.
            if (!ControlCenterPresentation.ToggleAvailable(toggle, snapshot)) return true;
            var current = toggle switch
            {
                ControlCenterToggle.WiFi => snapshot?.WiFiEnabled is true,
                ControlCenterToggle.Bluetooth => snapshot?.BluetoothEnabled is true,
                _ => snapshot?.NightLightEnabled is true
            };
            _ = _runtime.ToggleControlAsync(widget, toggle, !current);
            return true;
        }

        var sliders = ControlCenterPresentation.SliderArea(body);
        var half = (sliders.Width - 14) / 2;
        // Only the caption and track respond; the padding below them is dead space
        // and clicking it used to change the level by accident.
        var volumeColumn = new LayoutRect(sliders.X, sliders.Y, half, sliders.Height);
        if (ControlCenterPresentation.SliderHitArea(volumeColumn).Contains(point.X, point.Y) &&
            snapshot?.VolumePercent is not null)
        {
            _runtime.SetControlVolume(PercentFromPoint(volumeColumn, point));
            return true;
        }
        var brightnessColumn = new LayoutRect(sliders.X + half + 14, sliders.Y, half, sliders.Height);
        if (ControlCenterPresentation.SliderHitArea(brightnessColumn).Contains(point.X, point.Y) &&
            snapshot?.BrightnessPercent is not null)
        {
            _runtime.SetControlBrightness(PercentFromPoint(brightnessColumn, point));
            return true;
        }

        var mediaArea = MonitorRectangle(widget, ControlCenterPresentation.MediaArea(body));
        var outputRow = MonitorRectangle(widget, ControlCenterPresentation.OutputRow(
            ControlCenterPresentation.MediaArea(body)));
        if (outputRow.Contains(point))
        {
            // Anchor the list under the row. outputRow is card-local, so the card's
            // own position is added first - the weather menu's anchor already
            // includes it, which is why that one worked and this did not.
            var anchor = WidgetRectangle(widget);
            ShowControlCenterOutputMenu(widget, new Point(
                ToPixel(anchor.X + outputRow.X), ToPixel(anchor.Y + outputRow.Y + outputRow.Height)));
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

    /// <summary>
    /// The output-device picker, opened from the card's menu. A card cannot host a
    /// popup list, so the choice is made from the widget menu instead.
    /// </summary>
    private void ShowControlCenterOutputMenu(DesktopWidget widget, Point location)
    {
        if (widget.Controls is null) return;
        // Reuses the same themed menu the widget menu uses, so the picker looks
        // like the rest of the shell rather than a raw WinForms list.
        var menu = BuildControlCenterOutputMenu(widget.Controls, (float)_scale);
        if (menu.Items.Count == 0) { menu.Dispose(); return; }
        // Same lifecycle the other widget menus use: theme on open, and dispose
        // deferred so the menu's own close animation is not torn down underneath
        // it. Disposing inline from inside Closed threw
        // "Cannot access a disposed object: FluentContextMenuStrip".
        menu.Opening += (_, _) => _runtime.ApplyContextMenuTheme(menu);
        menu.Closed += (_, _) => { if (!IsDisposed && IsHandleCreated) BeginInvoke((Action)menu.Dispose); else menu.Dispose(); };
        menu.Show(this, location);
    }

    /// <summary>
    /// Builds the endpoint list. Split out from the popup so the contents can be
    /// tested without a window to host it.
    /// </summary>
    internal static FluentContextMenuStrip BuildControlCenterOutputMenu(ControlCenterSnapshot snapshot,
        float dpiScale = 1f)
    {
        var menu = new FluentContextMenuStrip { PreferredDpiScale = dpiScale };
        foreach (var device in snapshot.OutputDevices)
        {
            // The tick marks what the card is showing, which during a switch can
            // briefly differ from what the OS reports as default.
            var current = string.Equals(device.Id, snapshot.OutputDeviceId, StringComparison.OrdinalIgnoreCase);
            var item = new ToolStripMenuItem(device.Name) { Checked = current };
            var id = device.Id;
            menu.Items.Add(item);
        }
        return menu;
    }
}
