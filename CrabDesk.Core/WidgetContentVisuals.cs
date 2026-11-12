namespace CrabDesk.Core;

/// <summary>Shared semantic colors and filled symbols for widget contents.</summary>
public static class WidgetContentVisuals
{
    public static string PerformanceAsset(int index) => index switch
    { 0 => "content-cpu", 1 => "content-gpu", 2 => "content-memory", _ => "content-disk" };

    public static string ControlAsset(ControlCenterQuickAction action, ControlCenterSnapshot? snapshot) => action switch
    {
        ControlCenterQuickAction.WiFi => snapshot?.WiFiEnabled is false ? "content-wifi-off" : "content-wifi",
        ControlCenterQuickAction.Bluetooth => "content-bluetooth",
        ControlCenterQuickAction.NightLight => "content-moon",
        ControlCenterQuickAction.Display => "content-display",
        ControlCenterQuickAction.Cast => "content-cast",
        ControlCenterQuickAction.Microphone => "content-microphone",
        ControlCenterQuickAction.ApplicationVolume => "content-mixer",
        ControlCenterQuickAction.ScreenCapture => "content-capture",
        ControlCenterQuickAction.NetworkAdapters => "content-network",
        _ => "content-settings"
    };

    public static string Tone(string asset, bool lightText) => asset switch
    {
        "content-gpu" or "content-upload" or "content-bluetooth" or "content-capture" or "content-translation" or "content-music" or "lyrics"
            => lightText ? "#B6A0F2" : "#795CC4",
        "content-memory" or "content-check" or "content-task" or "content-microphone" or "content-headphones"
            => lightText ? "#76D6BA" : "#24886E",
        "content-disk" or "content-brightness" or "content-source" or "note" or "file-shelf"
            => lightText ? "#F3C46E" : "#A97620",
        "content-muted" or "content-wifi-off" => lightText ? "#F2A3A4" : "#BE6267",
        _ => lightText ? "#83B7F4" : "#397AC4"
    };
}
