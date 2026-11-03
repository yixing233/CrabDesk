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
/// WiFi, and the night-light interface is undocumented enough that reading it
/// can fail. A null means "no reading", never "off" — the card draws those
/// controls as unavailable instead of pretending they are switched off.
/// </summary>
public sealed record ControlCenterSnapshot
{
    public bool? WiFiEnabled { get; init; }
    public bool? BluetoothEnabled { get; init; }
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
            if (elapsed > TimeSpan.Zero) position += elapsed;
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

    private const int ToggleCount = 3;
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

    public static LayoutRect SliderArea(LayoutRect body) =>
        new(body.X, body.Y + 64, body.Width, 46);

    /// <summary>
    /// The part of a slider column that responds to clicks: the caption and the
    /// track. The area below the track is padding between controls, and treating
    /// it as a hit target meant a click in empty space changed the volume.
    /// </summary>
    public static LayoutRect SliderHitArea(LayoutRect sliderColumn) =>
        new(sliderColumn.X, sliderColumn.Y, sliderColumn.Width, 32);

    public static LayoutRect MediaArea(LayoutRect body) =>
        new(body.X, body.Y + 148, body.Width, body.Height - 148);

    /// <summary>
    /// The output-device row inside the card. It is a button: clicking cycles to
    /// the next active endpoint, so switching devices does not require opening a
    /// menu.
    /// </summary>
    public static LayoutRect OutputRow(LayoutRect mediaArea) =>
        new(mediaArea.X, mediaArea.Y, mediaArea.Width, 26);

    /// <summary>The media panel under the output row.</summary>
    public static LayoutRect MediaPanel(LayoutRect mediaArea) =>
        new(mediaArea.X, mediaArea.Y + 34, mediaArea.Width, Math.Max(0, mediaArea.Height - 34));

    /// <summary>Progress bar inside the media panel, above the transport buttons.</summary>
    public static LayoutRect MediaProgress(LayoutRect mediaPanel) =>
        new(mediaPanel.X + 10, mediaPanel.Y + mediaPanel.Height - 46, mediaPanel.Width - 20, 4);

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
        None, InkToggle,
        ToggleWiFi, ToggleBluetooth, ToggleNightLight,
        VolumeSlider, BrightnessSlider, OutputRow,
        MediaPrevious, MediaPlayPause, MediaNext
    }

    public static ControlCenterTarget TargetAt(LayoutRect body, double x, double y)
    {
        foreach (var toggle in Enum.GetValues<ControlCenterToggle>())
        {
            if (!Toggle(body, (int)toggle).Contains(x, y)) continue;
            return toggle switch
            {
                ControlCenterToggle.WiFi => ControlCenterTarget.ToggleWiFi,
                ControlCenterToggle.Bluetooth => ControlCenterTarget.ToggleBluetooth,
                _ => ControlCenterTarget.ToggleNightLight
            };
        }
        var sliders = SliderArea(body);
        var half = (sliders.Width - 14) / 2;
        if (SliderHitArea(new(sliders.X, sliders.Y, half, sliders.Height)).Contains(x, y)) return ControlCenterTarget.VolumeSlider;
        if (SliderHitArea(new(sliders.X + half + 14, sliders.Y, half, sliders.Height)).Contains(x, y)) return ControlCenterTarget.BrightnessSlider;
        var mediaArea = MediaArea(body);
        if (OutputRow(mediaArea).Contains(x, y)) return ControlCenterTarget.OutputRow;
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
        const double buttonWidth = 34, height = 22;
        var startX = mediaPanel.X + (mediaPanel.Width - buttonWidth * 3) / 2;
        var top = mediaPanel.Y + mediaPanel.Height - 22;
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
        _ => "夜间"
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
