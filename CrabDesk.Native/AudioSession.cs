using System.Runtime.InteropServices;
using CrabDesk.Core;

namespace CrabDesk.Native;

/// <summary>
/// Volume and endpoint routing through Core Audio.
///
/// Windows exposes no managed API for either, so the COM interfaces are declared
/// here. Only the members actually used are declared; the vtable slots before
/// them are still reserved with placeholders, because a COM interface is
/// positional — skipping a slot would call the wrong method.
/// </summary>
internal static partial class AudioSession
{
    private const int ClsCtxAll = 23;

    public readonly record struct Reading(double? Volume, bool? Muted, string DeviceId, string DeviceName);

    /// <summary>Volume of the default output endpoint.</summary>
    public static Reading Read() => Read(EDataFlow.Render);

    private static Reading Read(EDataFlow flow)
    {
        IMMDevice? device = null;
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            try
            {
                if (enumerator.GetDefaultAudioEndpoint(flow, ERole.Multimedia, out device) != 0 || device is null)
                    return new(null, null, "", "");
                var id = device.GetId();
                var name = device.GetFriendlyName();
                var volume = device.ActivateVolume();
                if (volume is null) return new(null, null, id, name);
                try
                {
                    var levelRead = volume.GetMasterVolumeLevelScalar(out var level) >= 0;
                    var muteRead = volume.GetMute(out var muted) >= 0;
                    return new(levelRead ? Math.Clamp(level * 100, 0, 100) : null, muteRead ? muted != 0 : null, id, name);
                }
                finally { Release(volume); }
            }
            finally { Release(enumerator); }
        }
        catch (Exception)
        {
            return new(null, null, "", "");
        }
        finally { Release(device); }
    }

    /// <summary>Active output endpoints, with the default marked.</summary>
    public static IReadOnlyList<ControlCenterOutputDevice> ReadOutputs() => ReadEndpoints(EDataFlow.Render);

    private static IReadOnlyList<ControlCenterOutputDevice> ReadEndpoints(EDataFlow flow)
    {
        IMMDeviceCollection? collection = null;
        IMMDeviceEnumerator? enumerator = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            var defaultId = "";
            if (enumerator.GetDefaultAudioEndpoint(flow, ERole.Multimedia, out var current) == 0 && current is not null)
            {
                try { defaultId = current.GetId(); } finally { Release(current); }
            }
            if (enumerator.EnumAudioEndpoints(flow, DeviceState.Active, out collection) != 0 || collection is null)
                return [];
            collection.GetCount(out var count);
            var list = new List<ControlCenterOutputDevice>((int)count);
            for (uint index = 0; index < count; index++)
            {
                IMMDevice? device = null;
                try
                {
                    if (collection.Item(index, out device) != 0 || device is null) continue;
                    var id = device.GetId();
                    // A nameless endpoint is not useful in a picker.
                    var name = device.GetFriendlyName();
                    if (id.Length == 0 || name.Length == 0) continue;
                    list.Add(new(id, name, string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase)));
                }
                catch (Exception) { /* skip the unreadable endpoint */ }
                finally { Release(device); }
            }
            return list;
        }
        catch (Exception)
        {
            return [];
        }
        finally
        {
            Release(collection);
            Release(enumerator);
        }
    }

    public static bool SetVolume(double percent)
    {
        IMMDevice? device = null;
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            try
            {
                if (enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out device) != 0 || device is null) return false;
                var volume = device.ActivateVolume();
                if (volume is null) return false;
                try
                {
                    var context = Guid.Empty;
                    return volume.SetMasterVolumeLevelScalar((float)(ControlCenterPresentation.ClampPercent(percent) / 100), ref context) >= 0;
                }
                finally { Release(volume); }
            }
            finally { Release(enumerator); }
        }
        catch (Exception) { return false; }
        finally { Release(device); }
    }

    public static bool SetMuted(bool muted)
    {
        IMMDevice? device = null;
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            try
            {
                if (enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out device) != 0 || device is null) return false;
                var volume = device.ActivateVolume();
                if (volume is null) return false;
                try { var context = Guid.Empty; return volume.SetMute(muted ? 1 : 0, ref context) >= 0; }
                finally { Release(volume); }
            }
            finally { Release(enumerator); }
        }
        catch (Exception) { return false; }
        finally { Release(device); }
    }

    /// <summary>
    /// Makes an endpoint the system default for every role.
    ///
    /// IPolicyConfig is undocumented, which is why it is used through a CLSID and
    /// not a contract: there is no supported way to change the default endpoint
    /// before Windows 11's audio switcher, and this machine exposes none.
    /// All three roles are set so playback, communications and the legacy role
    /// agree; setting only Multimedia leaves communications on the old device.
    /// </summary>
    public static bool SetDefaultOutput(string deviceId)
    {
        object? policy = null;
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9"));
            if (type is null) return false;
            policy = Activator.CreateInstance(type);
            if (policy is null) return false;
            var config = (IPolicyConfig)policy;
            var ok = true;
            foreach (var role in new[] { ERole.Console, ERole.Multimedia, ERole.Communications })
            {
                // 0 means success; a failure on any role means the switch did not
                // fully take effect.
                if (config.SetDefaultEndpoint(deviceId, role) != 0) ok = false;
            }
            return ok;
        }
        catch (Exception) { return false; }
        finally
        {
            if (policy is not null && Marshal.IsComObject(policy)) Marshal.ReleaseComObject(policy);
        }
    }

    private static void Release(object? instance)
    {
        if (instance is not null && Marshal.IsComObject(instance))
        {
            try { Marshal.ReleaseComObject(instance); } catch (Exception) { /* release must not throw */ }
        }
    }

    private static IAudioEndpointVolume? ActivateVolume(this IMMDevice device)
    {
        var iid = typeof(IAudioEndpointVolume).GUID;
        // Activate is PreserveSig, so the HRESULT is returned rather than thrown.
        return device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out var activated) == 0
            ? activated as IAudioEndpointVolume : null;
    }

    private static string GetId(this IMMDevice device) =>
        device.GetId(out var id) == 0 && id is not null ? id : "";

    /// <summary>Endpoint friendly name, read from the device property store.</summary>
    private static string GetFriendlyName(this IMMDevice device)
    {
        IPropertyStore? store = null;
        try
        {
            if (device.OpenPropertyStore(0, out store) != 0 || store is null) return "";
            // PKEY_Device_FriendlyName
            var key = new PropertyKey(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
            if (store.GetValue(ref key, out var value) != 0) return "";
            try { return value.GetString(); }
            finally { value.Clear(); }
        }
        catch (Exception) { return ""; }
        finally { Release(store); }
    }

    private enum EDataFlow { Render, Capture, All }
    private enum ERole { Console, Multimedia, Communications }
    [Flags] private enum DeviceState { Active = 1, Disabled = 2, NotPresent = 4, Unplugged = 8, All = 15 }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, DeviceState stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object? instance);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out DeviceState state);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid formatId, int propertyId)
    {
        public Guid FormatId = formatId;
        public int PropertyId = propertyId;
    }

    /// <summary>
    /// Minimal PROPVARIANT. Only the string case is read, and the buffer must be
    /// freed with PropVariantClear, so the size and layout match the native type.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort ValueType;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public IntPtr Pointer;
        public IntPtr Pointer2;

        public readonly string GetString() => ValueType == 31 && Pointer != IntPtr.Zero
            ? Marshal.PtrToStringUni(Pointer) ?? "" : "";

        public void Clear() => PropVariantClear(ref this);
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float level, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float level);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float level, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float level);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute(int mute, ref Guid context);
        [PreserveSig] int GetMute(out int mute);
    }

    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out IntPtr format);
        int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int defaultFormat, out IntPtr format);
        int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr endpoint, IntPtr mix);
        int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int defaultPeriod, out long period, out long minimum);
        int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref long period);
        int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out IntPtr mode);
        int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref IntPtr mode);
        int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref PropertyKey key, out PropVariant value);
        int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref PropertyKey key, ref PropVariant value);
        int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);
        int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
    }
}
