namespace CrabDesk.Core;

public sealed record MicrophoneDevice(string Id, string Name);

public sealed record MicrophoneSnapshot(string DeviceId, string DeviceName, double? VolumePercent,
    bool? Muted, double? PeakPercent, IReadOnlyList<MicrophoneDevice> Devices)
{
    public static MicrophoneSnapshot Empty { get; } = new("", "未找到可用的麦克风", null, null, null, []);
}
