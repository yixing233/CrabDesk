using System.Runtime.InteropServices;
using CrabDesk.Core;

namespace CrabDesk.Native;

internal static partial class AudioSession
{
    internal static MicrophoneSnapshot ReadMicrophone()
    {
        var reading = Read(EDataFlow.Capture);
        var devices = ReadEndpoints(EDataFlow.Capture).Select(device => new MicrophoneDevice(device.Id, device.Name)).ToArray();
        return new(reading.DeviceId, reading.DeviceName, reading.Volume, reading.Muted,
            ReadMicrophonePeak(reading.DeviceId), devices);
    }

    private static double? ReadMicrophonePeak(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return null;
        IMMDeviceEnumerator? devices = null;
        IMMDevice? device = null;
        object? meter = null;
        try
        {
            devices = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (devices.GetDevice(deviceId, out device) < 0) return null;
            var iid = typeof(IAudioMeterInformation).GUID;
            if (device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out meter) < 0 || meter is not IAudioMeterInformation peak) return null;
            return peak.GetPeakValue(out var value) >= 0 ? ControlCenterPresentation.ClampPercent(value * 100d) : null;
        }
        catch (Exception) { return null; }
        finally { Release(meter); Release(device); Release(devices); }
    }

    internal static bool WriteMicrophone(string deviceId, double? volume, bool? muted)
    {
        if (string.IsNullOrEmpty(deviceId)) return false;
        IMMDeviceEnumerator? devices = null;
        IMMDevice? device = null;
        IAudioEndpointVolume? endpoint = null;
        try
        {
            devices = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (devices.GetDevice(deviceId, out device) < 0 || !IsCaptureEndpoint(device)) return false;
            endpoint = device.ActivateVolume();
            if (endpoint is null) return false;
            var context = Guid.Empty;
            return (volume is not { } level || endpoint.SetMasterVolumeLevelScalar((float)(level / 100), ref context) >= 0)
                && (muted is not { } silent || endpoint.SetMute(silent ? 1 : 0, ref context) >= 0);
        }
        catch (Exception) { return false; }
        finally { Release(endpoint); Release(device); Release(devices); }
    }

    internal static bool SetDefaultInput(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return false;
        IMMDeviceEnumerator? devices = null;
        IMMDevice? device = null;
        try
        {
            devices = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            return devices.GetDevice(deviceId, out device) >= 0 && IsCaptureEndpoint(device) && SetDefaultOutput(deviceId);
        }
        catch (Exception) { return false; }
        finally { Release(device); Release(devices); }
    }

    private static bool IsCaptureEndpoint(IMMDevice device) =>
        device is IMMEndpoint endpoint && endpoint.GetDataFlow(out var flow) >= 0 && flow == EDataFlow.Capture &&
        device.GetState(out var state) >= 0 && state == DeviceState.Active;

    [ComImport, Guid("1BE09788-6894-4089-8586-9A2A6C265AC5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMEndpoint
    {
        [PreserveSig] int GetDataFlow(out EDataFlow flow);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
    }
}
