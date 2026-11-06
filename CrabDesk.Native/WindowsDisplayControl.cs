using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CrabDesk.Core;
using DisplayPath = CrabDesk.Native.ActiveDisplayPaths.PathInfo;

namespace CrabDesk.Native;

public sealed class WindowsDisplayControl : IDisplayControl
{
    internal const string SettingsUri = "ms-settings:display";
    private const uint AllPaths = 1, DatabaseCurrent = 4, PathActive = 1, Apply = 0x80;

    public ControlDisplaySnapshot Read()
    {
        if (!TryReadPaths(AllPaths, out var paths, out _)) return new(null, 0);
        TryReadPaths(DatabaseCurrent, out var savedPaths, out var topology);
        return DescribeTopology(paths, savedPaths, topology);
    }

    public ControlOperationResult SetMode(ControlDisplayMode mode) =>
        ApplyMode(mode, flags => SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, flags));

    internal static ControlOperationResult ApplyMode(ControlDisplayMode mode, Func<uint, int> apply)
    {
        var topology = mode switch
        {
            ControlDisplayMode.Internal => 1u,
            ControlDisplayMode.Clone => 2u,
            ControlDisplayMode.Extend => 4u,
            ControlDisplayMode.External => 8u,
            _ => 0u
        };
        if (topology == 0) return new(false, "无效的投影模式。");

        // SDC_APPLY with exactly one SDC_TOPOLOGY_* applies Windows' best
        // configuration for that topology and reports the actual API result.
        var status = apply(Apply | topology);
        if (status == 0) return ControlOperationResult.Success;
        var reason = status switch
        {
            5 => "当前桌面或远程会话不允许切换投影模式。",
            50 => "当前显示驱动不支持 Windows 投影配置。",
            87 => "Windows 拒绝了此投影配置。",
            1610 => "当前连接的屏幕不支持此投影模式。",
            _ => new Win32Exception(status).Message
        };
        return new(false, $"切换投影模式失败：{reason}（Windows 错误 {status}）");
    }

    public ControlOperationResult OpenCast()
    {
        // Microsoft's documented sender-side Cast entry point is Win+K.
        // Preserve any keys the user is holding instead of releasing them.
        foreach (var key in new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C, 0x4B })
            if ((NativeMethods.GetAsyncKeyState(key) & 0x8000) != 0)
                return new(false, "请松开键盘上的修饰键和 K 键后重试。");

        var inputs = CreateCastShortcut();
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<ShortcutInput>());
        if (sent == inputs.Length) return ControlOperationResult.Success;
        var error = Marshal.GetLastWin32Error();
        if (sent > 0)
        {
            // A partially accepted sequence must not leave our keys pressed.
            var releases = inputs.Skip(2).ToArray();
            SendInput((uint)releases.Length, releases, Marshal.SizeOf<ShortcutInput>());
        }
        return new(false, error == 0 ? "Windows 未接受打开无线投屏选择器的快捷键。" :
            $"无法打开无线投屏选择器：{new Win32Exception(error).Message}（Windows 错误 {error}）");
    }

    public ControlOperationResult OpenSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo(SettingsUri) { UseShellExecute = true })?.Dispose();
            return ControlOperationResult.Success;
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            return new(false, $"无法打开显示设置：{error.Message}");
        }
    }

    internal static ControlDisplaySnapshot DescribeTopology(IReadOnlyList<DisplayPath> paths,
        IReadOnlyList<DisplayPath> savedPaths, uint savedTopology)
    {
        // QDC_ALL_PATHS can contain several source routes to the same target,
        // including connected screens that are disabled in the active layout.
        var available = paths.Where(path => path.Target.Available != 0).ToArray();
        var count = available.Select(TargetKey).Distinct().Count();
        var active = available.Where(path => (path.Flags & PathActive) != 0).ToArray();
        if (active.Length == 0) return new(null, count);

        // QDC_DATABASE_CURRENT describes persistence, not temporary modes.
        // Only use its topology label when its paths match the actual layout.
        var savedActive = savedPaths.Where(path => (path.Flags & PathActive) != 0 && path.Target.Available != 0);
        if (active.Select(PathKey).ToHashSet().SetEquals(savedActive.Select(PathKey)))
        {
            ControlDisplayMode? savedMode = savedTopology switch
            {
                1 => ControlDisplayMode.Internal,
                2 => ControlDisplayMode.Clone,
                4 => ControlDisplayMode.Extend,
                8 => ControlDisplayMode.External,
                _ => null
            };
            if (savedMode is not null) return new(savedMode, count);
        }

        ControlDisplayMode? mode;
        if (active.Select(TargetKey).Distinct().Count() > 1)
            mode = active.Select(SourceKey).Distinct().Count() == 1 ? ControlDisplayMode.Clone : ControlDisplayMode.Extend;
        else if (IsEmbedded(active[0].Target.Technology)) mode = ControlDisplayMode.Internal;
        else if (available.Any(path => IsEmbedded(path.Target.Technology))) mode = ControlDisplayMode.External;
        else mode = null; // A desktop's sole external panel can be its PC screen.
        return new(mode, count);
    }

    private static bool IsEmbedded(uint technology) => technology is 6 or 11 or 13 or 0x80000000;
    private static (uint, int, uint) TargetKey(DisplayPath path) =>
        (path.Target.Adapter.Low, path.Target.Adapter.High, path.Target.Id);
    private static (uint, int, uint) SourceKey(DisplayPath path) =>
        (path.Source.Adapter.Low, path.Source.Adapter.High, path.Source.Id);
    private static ((uint, int, uint), (uint, int, uint)) PathKey(DisplayPath path) => (SourceKey(path), TargetKey(path));

    private static bool TryReadPaths(uint flags, out DisplayPath[] paths, out uint topology)
    {
        paths = [];
        topology = 0;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (GetDisplayConfigBufferSizes(flags, out var pathCount, out var modeCount) != 0 ||
                pathCount > 4096 || modeCount > 16384) return false;
            var buffer = new DisplayPath[pathCount];
            // DISPLAYCONFIG_MODE_INFO is 64 bytes, matching ActiveDisplayPaths.
            var modes = Marshal.AllocHGlobal(checked((int)Math.Max(modeCount, 1) * 64));
            try
            {
                var status = flags == DatabaseCurrent
                    ? QueryDatabaseDisplayConfig(flags, ref pathCount, buffer, ref modeCount, modes, out topology)
                    : QueryDisplayConfig(flags, ref pathCount, buffer, ref modeCount, modes, IntPtr.Zero);
                if (status == 122) continue; // Topology changed while sizing.
                if (status != 0) return false;
                paths = buffer.Take((int)pathCount).ToArray();
                return true;
            }
            finally { Marshal.FreeHGlobal(modes); }
        }
        return false;
    }

    internal static ShortcutInput[] CreateCastShortcut() =>
        [Key(0x5B, false), Key(0x4B, false), Key(0x4B, true), Key(0x5B, true)];

    private static ShortcutInput Key(ushort key, bool released) => new()
    {
        Type = 1,
        Data = new() { Keyboard = new() { VirtualKey = key, Flags = released ? 2u : 0u } }
    };

    [StructLayout(LayoutKind.Sequential)]
    internal struct ShortcutInput { public uint Type; public InputData Data; }
    [StructLayout(LayoutKind.Explicit)]
    internal struct InputData
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardInput
    {
        public ushort VirtualKey, ScanCode;
        public uint Flags, Time;
        public UIntPtr ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseInput
    {
        public int X, Y;
        public uint MouseData, Flags, Time;
        public UIntPtr ExtraInfo;
    }

    [DllImport("user32.dll")]
    private static extern int SetDisplayConfig(uint pathCount, IntPtr paths, uint modeCount, IntPtr modes, uint flags);
    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint paths, [Out] DisplayPath[] pathArray,
        ref uint modes, IntPtr modeArray, IntPtr topology);
    [DllImport("user32.dll", EntryPoint = "QueryDisplayConfig")]
    private static extern int QueryDatabaseDisplayConfig(uint flags, ref uint paths, [Out] DisplayPath[] pathArray,
        ref uint modes, IntPtr modeArray, out uint topology);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, [In] ShortcutInput[] inputs, int size);
}
