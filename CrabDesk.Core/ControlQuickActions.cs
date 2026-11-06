namespace CrabDesk.Core;

public enum ControlCenterQuickAction { WiFi, Bluetooth, NightLight, Display, Cast, Microphone, ApplicationVolume, ScreenCapture, Settings }

public static class ControlQuickActions
{
    public const int MaximumCount = 6;
    public static IReadOnlyList<ControlCenterQuickAction> Defaults { get; } = Array.AsReadOnly(new[]
        { ControlCenterQuickAction.WiFi, ControlCenterQuickAction.Bluetooth, ControlCenterQuickAction.NightLight, ControlCenterQuickAction.Display });

    public static List<ControlCenterQuickAction> Normalize(IEnumerable<ControlCenterQuickAction>? actions)
    {
        var result = actions?.Where(action => Enum.IsDefined(action)).Distinct().Take(MaximumCount).ToList();
        return result is { Count: > 0 } ? result : Defaults.ToList();
    }

    public static IReadOnlyList<ControlCenterQuickAction> For(DesktopWidget widget) =>
        widget.ControlCenterQuickActions is { Count: > 0 and <= MaximumCount } actions ? actions : Defaults;

    public static string Label(ControlCenterQuickAction action) => action switch
    {
        ControlCenterQuickAction.WiFi => "Wi-Fi", ControlCenterQuickAction.Bluetooth => "蓝牙",
        ControlCenterQuickAction.NightLight => "夜间", ControlCenterQuickAction.Display => "显示",
        ControlCenterQuickAction.Cast => "投屏", ControlCenterQuickAction.Microphone => "麦克风",
        ControlCenterQuickAction.ApplicationVolume => "应用音量", ControlCenterQuickAction.ScreenCapture => "截图", _ => "设置"
    };

    public static ControlCenterToggle? Toggle(ControlCenterQuickAction action) => action switch
    {
        ControlCenterQuickAction.WiFi => ControlCenterToggle.WiFi,
        ControlCenterQuickAction.Bluetooth => ControlCenterToggle.Bluetooth,
        ControlCenterQuickAction.NightLight => ControlCenterToggle.NightLight, _ => null
    };

    public static bool HasDetails(ControlCenterQuickAction action) => action is ControlCenterQuickAction.WiFi or ControlCenterQuickAction.Bluetooth;

    public static string Status(ControlCenterQuickAction action, ControlCenterSnapshot? snapshot)
    {
        if (action == ControlCenterQuickAction.WiFi && snapshot is { WiFiEnabled: true, WiFiConnectionName.Length: > 0 }) return snapshot.WiFiConnectionName;
        if (action == ControlCenterQuickAction.Bluetooth && snapshot is { BluetoothEnabled: true, BluetoothConnectionName.Length: > 0 }) return snapshot.BluetoothConnectionName;
        if (Toggle(action) is { } toggle) return ControlCenterPresentation.ToggleStateText(toggle, snapshot);
        return action switch
        {
            ControlCenterQuickAction.Display => "屏幕布局", ControlCenterQuickAction.Cast => "无线显示",
            ControlCenterQuickAction.Microphone => "输入音量", ControlCenterQuickAction.ApplicationVolume => "独立调节",
            ControlCenterQuickAction.ScreenCapture => "选取区域", _ => "系统设置"
        };
    }

    public static ControlCenterPresentation.ControlCenterTarget Target(ControlCenterQuickAction action) => action switch
    {
        ControlCenterQuickAction.WiFi => ControlCenterPresentation.ControlCenterTarget.ToggleWiFi,
        ControlCenterQuickAction.Bluetooth => ControlCenterPresentation.ControlCenterTarget.ToggleBluetooth,
        ControlCenterQuickAction.NightLight => ControlCenterPresentation.ControlCenterTarget.ToggleNightLight,
        ControlCenterQuickAction.Display => ControlCenterPresentation.ControlCenterTarget.DisplayControls,
        ControlCenterQuickAction.Cast => ControlCenterPresentation.ControlCenterTarget.Cast,
        ControlCenterQuickAction.Microphone => ControlCenterPresentation.ControlCenterTarget.Microphone,
        ControlCenterQuickAction.ApplicationVolume => ControlCenterPresentation.ControlCenterTarget.VolumeMixer,
        ControlCenterQuickAction.ScreenCapture => ControlCenterPresentation.ControlCenterTarget.ScreenCapture,
        _ => ControlCenterPresentation.ControlCenterTarget.SystemSettings
    };
}
