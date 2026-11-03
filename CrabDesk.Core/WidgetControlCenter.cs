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
    string Title, string Artist, string Album, bool IsPlaying, bool CanPlayPause, bool CanNext, bool CanPrevious);

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
        new(body.X, body.Y + 64, body.Width, 76);

    public static LayoutRect MediaArea(LayoutRect body) =>
        new(body.X, body.Y + 148, body.Width, body.Height - 148);

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
