using System.Runtime.InteropServices;

namespace CrabDesk.Native;

internal static class ActiveDisplayPaths
{
    internal sealed record Target(string DevicePath, string FriendlyName);

    public static IReadOnlyDictionary<string, Target> Read()
    {
        var result = new Dictionary<string, Target>(StringComparer.OrdinalIgnoreCase);
        // Retry when a display change makes the buffer sizes stale.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (GetDisplayConfigBufferSizes(2, out var pathCount, out var modeCount) != 0 || pathCount > 4096 || modeCount > 16384) return result;
            var paths = new PathInfo[pathCount];
            var modes = Marshal.AllocHGlobal(checked((int)Math.Max(modeCount, 1) * 64));
            try
            {
                var status = QueryDisplayConfig(2, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
                if (status == 122) continue;
                if (status != 0) return result;
                foreach (var path in paths.Take((int)pathCount))
                {
                    var source = new SourceName { Header = new() { Type = 1, Size = (uint)Marshal.SizeOf<SourceName>(), Adapter = path.Source.Adapter, Id = path.Source.Id } };
                    var target = new TargetName { Header = new() { Type = 2, Size = (uint)Marshal.SizeOf<TargetName>(), Adapter = path.Target.Adapter, Id = path.Target.Id } };
                    if (GetSourceName(ref source) != 0 || GetTargetName(ref target) != 0 || string.IsNullOrEmpty(source.Name) || string.IsNullOrEmpty(target.DevicePath)) continue;
                    result.TryAdd(source.Name, new(target.DevicePath, target.FriendlyName ?? ""));
                }
                return result;
            }
            finally { Marshal.FreeHGlobal(modes); }
        }
        return result;
    }

    [StructLayout(LayoutKind.Sequential)] internal struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] internal struct SourceInfo { public Luid Adapter; public uint Id, ModeIndex, Status; }
    [StructLayout(LayoutKind.Sequential)] internal struct TargetInfo
    {
        public Luid Adapter;
        public uint Id, ModeIndex, Technology, Rotation, Scaling, RefreshNumerator, RefreshDenominator, ScanLineOrdering;
        public int Available;
        public uint Status;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct PathInfo { public SourceInfo Source; public TargetInfo Target; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] internal struct Header { public uint Type, Size; public Luid Adapter; public uint Id; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct SourceName
    {
        public Header Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct TargetName
    {
        public Header Header;
        public uint Flags, Technology;
        public ushort Manufacturer, Product;
        public uint Connector;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string FriendlyName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DevicePath;
    }
    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint paths, [Out] PathInfo[] pathArray, ref uint modes, IntPtr modeArray, IntPtr topology);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] private static extern int GetSourceName(ref SourceName request);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] private static extern int GetTargetName(ref TargetName request);
}
