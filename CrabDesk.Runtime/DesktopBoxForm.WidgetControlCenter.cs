using System.Drawing;
using CrabDesk.Core;
using CrabDesk.Native;
using ToolStripMenuItem = System.Windows.Forms.ToolStripMenuItem;

namespace CrabDesk.Runtime;

internal sealed partial class DesktopBoxForm
{
    /// <summary>
    /// Draws the control centre: three toggle tiles, two sliders and a media
    /// strip.
    ///
    /// Every control renders as unavailable when the machine offers no reading,
    /// which is why the snapshot's fields are nullable. A toggle drawn "off" on
    /// a machine that cannot toggle it would send the user clicking a dead
    /// target.
    /// </summary>
    private void DrawControlCenterWidget(Graphics graphics, DesktopWidget widget, Color text, Color muted, Color accent)
    {
        var body = MonitorBody(widget);
        var snapshot = widget.Controls;

        foreach (var toggle in Enum.GetValues<ControlCenterToggle>())
        {
            var cell = MonitorRectangle(widget, ControlCenterPresentation.Toggle(body, (int)toggle));
            DrawControlCenterToggle(graphics, cell, toggle, snapshot, text, muted, accent);
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
        DrawControlCenterDevice(graphics, outputRow, snapshot, text, muted, accent);
        var panel = MonitorRectangle(widget, ControlCenterPresentation.MediaPanel(
            ControlCenterPresentation.MediaArea(body)));
        DrawControlCenterMedia(graphics, panel, snapshot, text, muted, accent);
    }

    private void DrawControlCenterToggle(Graphics graphics, RectangleF cell, ControlCenterToggle toggle,
        ControlCenterSnapshot? snapshot, Color text, Color muted, Color accent)
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
        var fill = !available ? Color.FromArgb(16, text) : on ? accent : Color.FromArgb(26, text);
        using var brush = new SolidBrush(fill);
        graphics.FillPath(brush, path);

        var labelInk = !available ? muted : on ? ResolveAutoTextColor(accent) : text;
        WidgetText(graphics, ControlCenterPresentation.ToggleLabel(toggle),
            new RectangleF(cell.X, cell.Y + 10, cell.Width, 18), labelInk, 12.5f, bold: true, centered: true);
        WidgetText(graphics, ControlCenterPresentation.ToggleStateText(toggle, snapshot),
            new RectangleF(cell.X, cell.Y + 30, cell.Width, 16), labelInk, 10f, centered: true);
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
        ControlCenterSnapshot? snapshot, Color text, Color muted, Color accent)
    {
        var name = snapshot?.OutputDeviceName is { Length: > 0 } device ? device : "输出设备不可用";
        using var path = RoundedRectangle(bounds, 6);
        using var brush = new SolidBrush(Color.FromArgb(20, text));
        graphics.FillPath(brush, path);
        var microphone = snapshot?.OutputDevices.Count > 1 ? "  ⇄" : "";
        WidgetText(graphics, "输出：" + WidgetMonitorMetrics.HardwareLabel(name, 24) + microphone,
            new RectangleF(bounds.X + 8, bounds.Y, bounds.Width - 16, bounds.Height), accent, 10.5f);
    }

    private void DrawControlCenterMedia(Graphics graphics, RectangleF bounds,
        ControlCenterSnapshot? snapshot, Color text, Color muted, Color accent)
    {
        using var path = RoundedRectangle(bounds, 8);
        using var brush = new SolidBrush(Color.FromArgb(20, text));
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
        DrawControlCenterMediaButton(graphics, buttons[0], "◀◀", enabled && media!.CanPrevious, muted, accent);
        DrawControlCenterMediaButton(graphics, buttons[1], media?.IsPlaying is true ? "❚❚" : "▶",
            enabled && (media?.CanPlayPause ?? false), muted, accent);
        DrawControlCenterMediaButton(graphics, buttons[2], "▶▶", enabled && media!.CanNext, muted, accent);
    }

    private static void DrawControlCenterMediaButton(Graphics graphics, RectangleF bounds, string glyph,
        bool enabled, Color muted, Color accent)
    {
        var ink = enabled ? accent : Color.FromArgb(90, muted);
        WidgetText(graphics, glyph, bounds, ink, 11, bold: true, centered: true);
    }

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
    private bool HandleControlCenterClick(DesktopWidget widget, PointF point)
    {
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

        var sliders = MonitorRectangle(widget, ControlCenterPresentation.SliderArea(body));
        var half = (sliders.Width - 14) / 2;
        // A click anywhere in a slider's column sets that level; the hit area is
        // taller than the visible bar so the target is comfortable.
        var volumeArea = new RectangleF(sliders.X, sliders.Y, half, sliders.Height);
        if (volumeArea.Contains(point) && snapshot?.VolumePercent is not null)
        {
            _runtime.SetControlVolume(PercentFromPoint(volumeArea, point));
            return true;
        }
        var brightnessArea = new RectangleF(sliders.X + half + 14, sliders.Y, half, sliders.Height);
        if (brightnessArea.Contains(point) && snapshot?.BrightnessPercent is not null)
        {
            _runtime.SetControlBrightness(PercentFromPoint(brightnessArea, point));
            return true;
        }

        var mediaArea = MonitorRectangle(widget, ControlCenterPresentation.MediaArea(body));
        var outputRow = MonitorRectangle(widget, ControlCenterPresentation.OutputRow(
            ControlCenterPresentation.MediaArea(body)));
        if (outputRow.Contains(point))
        {
            // Cycle to the next endpoint; the row shows a ⇄ when there is a choice.
            if (ControlCenterPresentation.NextOutputDevice(snapshot) is { } next)
                _ = _runtime.SetControlOutputAsync(next);
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
    private static double PercentFromPoint(RectangleF area, PointF point)
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
        var snapshot = widget.Controls;
        if (snapshot is null || snapshot.OutputDevices.Count == 0) return;
        // Reuses the same themed menu the widget menu uses, so the picker looks
        // like the rest of the shell rather than a raw WinForms list.
        var menu = new FluentContextMenuStrip();
        foreach (var device in snapshot.OutputDevices)
        {
            var item = new ToolStripMenuItem(device.Name) { Checked = device.IsDefault };
            var id = device.Id;
            item.Click += (_, _) => _ = _runtime.SetControlOutputAsync(id);
            menu.Items.Add(item);
        }
        menu.Closed += (_, _) => menu.Dispose();
        _runtime.ApplyContextMenuTheme(menu);
        menu.Show(this, location);
    }
}
