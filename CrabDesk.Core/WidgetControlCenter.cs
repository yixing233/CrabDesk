namespace CrabDesk.Core;

/// <summary>
/// The switches the control centre offers. Airplane mode is deliberately absent:
/// Windows exposes no API for it, and faking it by dropping the radios would look
/// identical to switching them off individually while claiming something else.
/// </summary>
public enum ControlCenterToggle { WiFi, Bluetooth, NightLight }

/// <summary>
/// State of the machine's quick settings, as one immutable snapshot.
///
/// Every field is nullable because the corresponding capability can be missing:
/// a desktop has no brightness slider, a machine without a radio reports no
/// WiFi, and system preferences may be inaccessible. A null means "no reading",
/// never "off" — the card draws those
/// controls as unavailable instead of pretending they are switched off.
/// </summary>
public sealed record ControlCenterSnapshot
{
    public bool? WiFiEnabled { get; init; }
    public bool? BluetoothEnabled { get; init; }
    public string WiFiConnectionName { get; init; } = "";
    public string BluetoothConnectionName { get; init; } = "";
    /// <summary>Windows dark mode selected by the Night quick action.</summary>
    public bool? NightLightEnabled { get; init; }
    public double? VolumePercent { get; init; }
    public bool? VolumeMuted { get; init; }
    public double? BrightnessPercent { get; init; }
    public string OutputDeviceId { get; init; } = "";
    public string OutputDeviceName { get; init; } = "";
    public IReadOnlyList<ControlCenterOutputDevice> OutputDevices { get; init; } = [];
    public ControlCenterMedia? Media { get; init; }
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.Now;

    public static ControlCenterSnapshot Empty { get; } = new();
}

/// <summary>One selectable audio output endpoint.</summary>
public sealed record ControlCenterOutputDevice(string Id, string Name, bool IsDefault);

/// <summary>What the media session reports, or null when nothing is playing.</summary>
public sealed record ControlCenterMedia(
    string Title, string Artist, string Album, bool IsPlaying, bool CanPlayPause, bool CanNext, bool CanPrevious)
{
    public string TrackId { get; init; } = "";
    public string SourceAppId { get; init; } = "";
    public byte[]? Artwork { get; init; }
    public bool CanSeek { get; init; }
    public TimeSpan SeekMinimum { get; init; }
    public TimeSpan? SeekMaximum { get; init; }
    public double PlaybackRate { get; init; } = 1;
    /// <summary>Track length; zero when the player reports none (a live stream).</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Playback position at <see cref="CapturedAt"/>.</summary>
    public TimeSpan Position { get; init; }

    /// <summary>
    /// When Position was read. The card advances the bar from here rather than
    /// re-querying the player, so the progress moves smoothly between polls.
    /// </summary>
    public DateTimeOffset CapturedAt { get; init; }

    /// <summary>
    /// Position now. A progress bar that only moved when polled would step
    /// visibly once every couple of seconds, so the elapsed wall clock is added
    /// while playing. Clamped to the track length so it cannot overshoot.
    /// </summary>
    public TimeSpan PositionNow(DateTimeOffset now)
    {
        if (Duration <= TimeSpan.Zero) return TimeSpan.Zero;
        var position = Position;
        if (IsPlaying && CapturedAt != default)
        {
            var elapsed = now - CapturedAt;
            if (elapsed > TimeSpan.Zero && double.IsFinite(PlaybackRate)) position += elapsed * Math.Max(0, PlaybackRate);
        }
        if (position < TimeSpan.Zero) return TimeSpan.Zero;
        return position > Duration ? Duration : position;
    }
}



public static class ControlCenterPresentation
{
    public const double CardWidth = 372;

    /// <summary>
    /// The card needs more height than the monitor widgets: two sliders and a
    /// media strip do not fit in the 180 used by the performance card without
    /// shrinking everything to the point of being unreadable.
    /// </summary>
    public const double CardHeight = 292;

    /// <summary>Uniform card insets for the control grid.</summary>
    public static LayoutRect Body(double width, double height, double cornerRadius)
    {
        var inset = WidgetMonitorPresentation.Body(width, height, cornerRadius).X;
        return new(inset, inset, width - inset * 2, height - inset * 2);
    }

    private const int ToggleCount = 4;
    private const double ToggleGap = 8;

    public static LayoutRect ToggleRow(LayoutRect body)
    {
        var width = (body.Width - (ToggleCount - 1) * ToggleGap) / ToggleCount;
        return new(body.X, body.Y, width, 56);
    }

    public static LayoutRect Toggle(LayoutRect body, int index) => ToggleRow(body) with
    {
        X = body.X + index * (ToggleRow(body).Width + ToggleGap)
    };

    public static LayoutRect QuickAction(LayoutRect body, int index, int count)
    {
        var columns = Math.Clamp(count, 1, ControlQuickActions.MaximumCount);
        var width = (body.Width - (columns - 1) * ToggleGap) / columns;
        return new(body.X + index * (width + ToggleGap), body.Y, width, 56);
    }

    public static LayoutRect QuickActionDetails(LayoutRect tile)
    {
        // Keep the centre available for the main action even with six tiles.
        var width = Math.Min(28, tile.Width / 2 - 1);
        return new(tile.X + tile.Width - width, tile.Y, width, 32);
    }

    public static LayoutRect SliderArea(LayoutRect body) =>
        new(body.X, body.Y + 64, body.Width, 46);

    public static LayoutRect VolumeMuteButton(LayoutRect body)
    {
        var sliders = SliderArea(body);
        return new(sliders.X, sliders.Y - 2, 22, 20);
    }

    public static LayoutRect VolumeMixerButton(LayoutRect body)
    {
        var sliders = SliderArea(body);
        return new(sliders.X + (sliders.Width - 14) / 2 - 22, sliders.Y - 2, 22, 20);
    }

    /// <summary>
    /// The part of a slider column that responds to clicks: the caption and the
    /// track. The area below the track is padding between controls, and treating
    /// it as a hit target meant a click in empty space changed the volume.
    /// </summary>
    public static LayoutRect SliderHitArea(LayoutRect sliderColumn) =>
        new(sliderColumn.X, sliderColumn.Y, sliderColumn.Width, 32);

    public static LayoutRect MediaArea(LayoutRect body) =>
        new(body.X, body.Y + 148, body.Width, body.Height - 148);

    public static LayoutRect BrightnessDisplayRow(LayoutRect body) => new(body.X, body.Y + 112, body.Width, 26);

    /// <summary>
    /// The output-device row inside the card. It is a button: clicking cycles to
    /// the next active endpoint, so switching devices does not require opening a
    /// menu.
    /// </summary>
    public static LayoutRect OutputRow(LayoutRect mediaArea) =>
        new(mediaArea.X, mediaArea.Y, mediaArea.Width - 34, 26);

    public static LayoutRect MicrophoneButton(LayoutRect body)
    {
        var media = MediaArea(body);
        return new(media.X + media.Width - 28, media.Y, 28, 26);
    }

    /// <summary>The media panel under the output row.</summary>
    public static LayoutRect MediaPanel(LayoutRect mediaArea) =>
        new(mediaArea.X, mediaArea.Y + 34, mediaArea.Width, Math.Max(0, mediaArea.Height - 34));

    /// <summary>Progress bar inside the media panel, above the transport buttons.</summary>
    public static LayoutRect MediaProgress(LayoutRect mediaPanel) =>
        new(mediaPanel.X + 10, mediaPanel.Y + mediaPanel.Height - 10, Math.Max(0, mediaPanel.Width - 108), 4);

    public static LayoutRect MediaProgressHitArea(LayoutRect panel)
    {
        var track = MediaProgress(panel);
        return new(track.X - 4, track.Y - 6, track.Width + 8, 16);
    }

    public static LayoutRect MediaArtwork(LayoutRect panel) => new(panel.X + 8, panel.Y + 6, 36, 36);
    public static LayoutRect MediaTitle(LayoutRect panel) => new(panel.X + 54, panel.Y + 3, panel.Width - 64, 17);
    public static LayoutRect EmptyMediaArtwork(LayoutRect panel) => new(panel.X + 12, panel.Y + (panel.Height - 36) / 2, 36, 36);
    public static LayoutRect EmptyMediaTitle(LayoutRect panel) => new(panel.X + 60, panel.Y + (panel.Height - 24) / 2, Math.Max(0, panel.Width - 72), 24);
    public static LayoutRect MediaArtist(LayoutRect panel) => new(panel.X + 54, panel.Y + 25, Math.Max(0, panel.Width - 148), 16);
    public static LayoutRect MediaTime(LayoutRect panel) => new(panel.X + panel.Width - 92, panel.Y + panel.Height - 16, 82, 14);

    public static TimeSpan ClampMediaPosition(ControlCenterMedia media, TimeSpan position)
    {
        var duration = Math.Max(0, media.Duration.Ticks);
        var minimum = Math.Clamp(media.SeekMinimum.Ticks, 0, duration);
        var maximum = Math.Clamp((media.SeekMaximum ?? media.Duration).Ticks, minimum, duration);
        return TimeSpan.FromTicks(Math.Clamp(position.Ticks, minimum, maximum));
    }

    /// <summary>Elapsed and total time, e.g. "0:37 / 3:10".</summary>
    public static string MediaTimeText(ControlCenterMedia? media, DateTimeOffset now)
    {
        if (media is null || media.Duration <= TimeSpan.Zero) return "";
        var position = media.PositionNow(now);
        return $"{Format(position)} / {Format(media.Duration)}";

        static string Format(TimeSpan value) =>
            value.TotalHours >= 1 ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
                : $"{value.Minutes}:{value.Seconds:00}";
    }

    /// <summary>
    /// Which control sits under a point. Drawing and hit-testing both use this,
    /// so a button's highlight can never disagree with what a click does.
    /// </summary>
    public enum ControlCenterTarget
    {
        None,
        ToggleWiFi, ToggleBluetooth, ToggleNightLight,
        VolumeSlider, BrightnessSlider, OutputRow, BrightnessDisplayRow,
        MediaPrevious, MediaPlayPause, MediaNext, VolumeMute, VolumeMixer, Microphone, MediaProgress,
        DisplayControls, Cast, ScreenCapture, SystemSettings, WiFiDetails, BluetoothDetails, NetworkAdapters
    }

    public static ControlCenterTarget TargetAt(LayoutRect body, double x, double y, IReadOnlyList<ControlCenterQuickAction>? actions = null,
        bool hasMedia = true)
    {
        actions ??= ControlQuickActions.Defaults;
        for (var index = 0; index < actions.Count; index++)
        {
            var tile = QuickAction(body, index, actions.Count);
            if (!tile.Contains(x, y)) continue;
            var action = actions[index];
            if (ControlQuickActions.HasDetails(action) && QuickActionDetails(tile).Contains(x, y))
                return action == ControlCenterQuickAction.WiFi ? ControlCenterTarget.WiFiDetails : ControlCenterTarget.BluetoothDetails;
            return ControlQuickActions.Target(action);
        }
        var sliders = SliderArea(body);
        var half = (sliders.Width - 14) / 2;
        if (VolumeMuteButton(body).Contains(x, y)) return ControlCenterTarget.VolumeMute;
        if (VolumeMixerButton(body).Contains(x, y)) return ControlCenterTarget.VolumeMixer;
        if (SliderHitArea(new(sliders.X, sliders.Y, half, sliders.Height)).Contains(x, y)) return ControlCenterTarget.VolumeSlider;
        if (SliderHitArea(new(sliders.X + half + 14, sliders.Y, half, sliders.Height)).Contains(x, y)) return ControlCenterTarget.BrightnessSlider;
        var mediaArea = MediaArea(body);
        if (BrightnessDisplayRow(body).Contains(x, y)) return ControlCenterTarget.BrightnessDisplayRow;
        if (OutputRow(mediaArea).Contains(x, y)) return ControlCenterTarget.OutputRow;
        if (MicrophoneButton(body).Contains(x, y)) return ControlCenterTarget.Microphone;
        if (!hasMedia) return ControlCenterTarget.None;
        if (MediaProgressHitArea(MediaPanel(mediaArea)).Contains(x, y)) return ControlCenterTarget.MediaProgress;
        var buttons = MediaButtonRects(MediaPanel(mediaArea));
        for (var index = 0; index < buttons.Length; index++)
        {
            if (!buttons[index].Contains(x, y)) continue;
            return index switch
            {
                0 => ControlCenterTarget.MediaPrevious,
                1 => ControlCenterTarget.MediaPlayPause,
                _ => ControlCenterTarget.MediaNext
            };
        }
        return ControlCenterTarget.None;
    }

    /// <summary>
    /// The three transport buttons, in draw order. Shared so the glyph and its
    /// clickable area stay together.
    /// </summary>
    public static LayoutRect[] MediaButtonRects(LayoutRect mediaPanel)
    {
        const double buttonWidth = 26, height = 22;
        var startX = mediaPanel.X + mediaPanel.Width - 8 - buttonWidth * 3;
        var top = mediaPanel.Y + Math.Min(22, mediaPanel.Height - 38);
        return
        [
            new(startX, top, buttonWidth, height),
            new(startX + buttonWidth, top, buttonWidth, height),
            new(startX + buttonWidth * 2, top, buttonWidth, height)
        ];
    }

    /// <summary>Short label for a toggle, used on the button face.</summary>
    public static string ToggleLabel(ControlCenterToggle toggle) => toggle switch
    {
        ControlCenterToggle.WiFi => "WiFi",
        ControlCenterToggle.Bluetooth => "蓝牙",
        _ => "系统主题"
    };

    /// <summary>
    /// A toggle reads its own state, so an unavailable control is described as
    /// such rather than as "off" — the distinction matters because a user who
    /// sees "off" will try to switch it on and get nothing.
    /// </summary>
    public static string ToggleStateText(ControlCenterToggle toggle, ControlCenterSnapshot? snapshot)
    {
        var state = toggle switch
        {
            ControlCenterToggle.WiFi => snapshot?.WiFiEnabled,
            ControlCenterToggle.Bluetooth => snapshot?.BluetoothEnabled,
            _ => snapshot?.NightLightEnabled
        };
        return state switch { true => "已开启", false => "已关闭", null => "不可用" };
    }

    public static bool ToggleAvailable(ControlCenterToggle toggle, ControlCenterSnapshot? snapshot) => toggle switch
    {
        ControlCenterToggle.WiFi => snapshot?.WiFiEnabled is not null,
        ControlCenterToggle.Bluetooth => snapshot?.BluetoothEnabled is not null,
        _ => snapshot?.NightLightEnabled is not null
    };

    public static string VolumeText(ControlCenterSnapshot? snapshot) => snapshot?.VolumePercent is { } volume
        ? snapshot.VolumeMuted is true ? "已静音" : $"{volume:0}%"
        : "不可用";

    public static string BrightnessText(ControlCenterSnapshot? snapshot) => snapshot?.BrightnessPercent is { } value
        ? $"{value:0}%" : "不可用";

    public static string MediaSummary(ControlCenterSnapshot? snapshot)
    {
        if (snapshot?.Media is not { } media) return "没有正在播放的媒体";
        var title = media.Title.Trim();
        var artist = media.Artist.Trim();
        if (title.Length == 0 && artist.Length == 0) return "未知曲目";
        if (artist.Length == 0) return title;
        if (title.Length == 0) return artist;
        return $"{title} · {artist}";
    }

    /// <summary>
    /// Clamps a requested level into the range the card can send. Percentage
    /// input arrives from a slider, so it can overshoot at the edges.
    /// </summary>
    public static double ClampPercent(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0, 100) : 0;
}
