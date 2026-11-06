using CrabDesk.Core;

namespace CrabDesk.Native;

public interface IAudioMixer
{
    AudioMixerSnapshot Read();
    bool SetVolume(string deviceId, string? applicationId, double percent);
    bool SetMuted(string deviceId, string? applicationId, bool muted);
}

public sealed class AudioMixer : IAudioMixer
{
    public AudioMixerSnapshot Read() => AudioSession.ReadMixer();
    public bool SetVolume(string deviceId, string? applicationId, double percent) =>
        AudioSession.WriteMixer(deviceId, applicationId, ControlCenterPresentation.ClampPercent(percent), null);
    public bool SetMuted(string deviceId, string? applicationId, bool muted) =>
        AudioSession.WriteMixer(deviceId, applicationId, null, muted);
}
