using CrabDesk.Core;

namespace CrabDesk.Native;

public interface IMicrophone
{
    MicrophoneSnapshot Read();
    bool SetDevice(string deviceId);
    bool SetVolume(string deviceId, double percent);
    bool SetMuted(string deviceId, bool muted);
}

public sealed class Microphone : IMicrophone
{
    public MicrophoneSnapshot Read() => AudioSession.ReadMicrophone();
    public bool SetDevice(string deviceId) => AudioSession.SetDefaultInput(deviceId);
    public bool SetVolume(string deviceId, double percent) =>
        AudioSession.WriteMicrophone(deviceId, ControlCenterPresentation.ClampPercent(percent), null);
    public bool SetMuted(string deviceId, bool muted) => AudioSession.WriteMicrophone(deviceId, null, muted);
}
