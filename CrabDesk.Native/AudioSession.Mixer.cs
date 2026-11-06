using System.Diagnostics;
using System.Runtime.InteropServices;
using CrabDesk.Core;

namespace CrabDesk.Native;

internal static partial class AudioSession
{
    internal static AudioMixerSnapshot ReadMixer()
    {
        IMMDeviceEnumerator? devices = null;
        IMMDevice? device = null;
        IAudioEndpointVolume? endpoint = null;
        try
        {
            devices = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (devices.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out device) < 0) return AudioMixerSnapshot.Empty;
            endpoint = device.ActivateVolume();
            double? volume = endpoint is not null && endpoint.GetMasterVolumeLevelScalar(out var level) >= 0 ? level * 100d : null;
            bool? muted = endpoint is not null && endpoint.GetMute(out var mute) >= 0 ? mute != 0 : null;
            var applications = new List<AudioMixerApplication>();
            VisitMixerSessions(device, (control, simpleVolume, identity) =>
            {
                if (simpleVolume.GetMasterVolume(out var value) < 0 || simpleVolume.GetMute(out var silent) < 0) return;
                applications.Add(new(identity.Id, identity.Name, identity.Path, Math.Clamp(value * 100d, 0, 100), silent));
            });
            return new(device.GetId(), device.GetFriendlyName(), volume, muted, AudioMixerPresentation.GroupApplications(applications));
        }
        finally { Release(endpoint); Release(device); Release(devices); }
    }

    internal static bool WriteMixer(string deviceId, string? applicationId, double? percent, bool? muted)
    {
        if (string.IsNullOrEmpty(deviceId)) return false;
        IMMDeviceEnumerator? devices = null;
        IMMDevice? device = null;
        IAudioEndpointVolume? endpoint = null;
        try
        {
            devices = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (devices.GetDevice(deviceId, out device) < 0) return false;
            var context = Guid.Empty;
            if (applicationId is null)
            {
                endpoint = device.ActivateVolume();
                if (endpoint is null) return false;
                return (percent is not { } value || endpoint.SetMasterVolumeLevelScalar((float)(value / 100), ref context) >= 0)
                    && (muted is not { } mute || endpoint.SetMute(mute ? 1 : 0, ref context) >= 0);
            }
            var matched = false;
            var succeeded = true;
            VisitMixerSessions(device, (_, simpleVolume, identity) =>
            {
                if (!string.Equals(identity.Id, applicationId, StringComparison.OrdinalIgnoreCase)) return;
                matched = true;
                if (percent is { } value && simpleVolume.SetMasterVolume((float)(value / 100), ref context) < 0) succeeded = false;
                if (muted is { } mute && simpleVolume.SetMute(mute, ref context) < 0) succeeded = false;
            });
            return matched && succeeded;
        }
        catch (COMException) { return false; }
        finally { Release(endpoint); Release(device); Release(devices); }
    }

    private sealed record MixerIdentity(string Id, string Name, string Path);

    private static void VisitMixerSessions(IMMDevice device, Action<IAudioSessionControl2, ISimpleAudioVolume, MixerIdentity> visit)
    {
        object? activated = null;
        IAudioSessionEnumerator? sessions = null;
        try
        {
            var iid = typeof(IAudioSessionManager2).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out activated));
            var manager = (IAudioSessionManager2)activated!;
            Marshal.ThrowExceptionForHR(manager.GetSessionEnumerator(out sessions));
            Marshal.ThrowExceptionForHR(sessions.GetCount(out var count));
            var identities = new Dictionary<uint, MixerIdentity>();
            for (var index = 0; index < count; index++)
            {
                object? session = null;
                try
                {
                    if (sessions.GetSession(index, out session) < 0 || session is not IAudioSessionControl2 control || session is not ISimpleAudioVolume volume) continue;
                    if (control.GetState(out var state) < 0 || state == 2) continue;
                    var identity = ResolveMixerIdentity(control, identities);
                    visit(control, volume, identity);
                }
                catch (COMException) { /* An application can exit while its session is being read. */ }
                finally { Release(session); }
            }
        }
        finally { Release(sessions); Release(activated); }
    }

    private static MixerIdentity ResolveMixerIdentity(IAudioSessionControl2 control, Dictionary<uint, MixerIdentity> cache)
    {
        if (control.IsSystemSoundsSession() == 0) return new("system-sounds", "系统声音", "");
        Marshal.ThrowExceptionForHR(control.GetProcessId(out var processId));
        if (processId != 0 && cache.TryGetValue(processId, out var cached)) return cached;
        control.GetDisplayName(out var displayName);
        var name = displayName ?? "";
        var path = "";
        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            name = process.ProcessName;
            path = process.MainModule?.FileName ?? "";
            if (!string.IsNullOrEmpty(path))
                name = FileVersionInfo.GetVersionInfo(path).FileDescription is { Length: > 0 } description ? description : name;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or System.IO.IOException or UnauthorizedAccessException) { }
        control.GetSessionInstanceIdentifier(out var sessionId);
        var id = path.Length > 0 ? "exe:" + path.ToUpperInvariant() : processId != 0 ? "pid:" + processId : "session:" + sessionId;
        var result = new MixerIdentity(id, string.IsNullOrWhiteSpace(name) || name.StartsWith('@') ? "音频应用" : name, path);
        if (processId != 0) cache[processId] = result;
        return result;
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, uint flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, uint flags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr context);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr context);
        [PreserveSig] int GetGroupingParam(out Guid group);
        [PreserveSig] int SetGroupingParam(ref Guid group, IntPtr context);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr notification);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr notification);
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetProcessId(out uint processId);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float volume, ref Guid context);
        [PreserveSig] int GetMasterVolume(out float volume);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
    }
}
