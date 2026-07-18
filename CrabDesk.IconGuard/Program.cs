using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

if (args.Length < 3 || !int.TryParse(args[0], out var processId) || !bool.TryParse(args[1], out var previousHidden))
{
    return;
}

var markerPath = args[2];
try
{
    using var process = System.Diagnostics.Process.GetProcessById(processId);
    process.WaitForExit();
}
catch (ArgumentException)
{
}
catch (InvalidOperationException)
{
}

var recoveryCompleted = false;
try
{
    var recovery = ReadRecoveryState(markerPath, previousHidden);
    var inputRestored = ExplorerIcons.EnsureDesktopInputEnabled();
    var workAreasRestored = recovery.WorkAreas is null || ExplorerIcons.RestoreWorkAreas(recovery.WorkAreas);
    var attributesRestored = ExplorerIcons.RestoreFileAttributes(recovery.FileAttributes);
    var positionsRestored = recovery.IconPositions.Count == 0;
    if (recovery.IconPositions.Count > 0)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            if (ExplorerIcons.RestorePositions(recovery.IconPositions) >= recovery.IconPositions.Count)
            {
                positionsRestored = true;
                break;
            }
            Thread.Sleep(500);
        }
    }
    var visibilityRestored = ExplorerIcons.SetHidden(recovery.PreviousHidden);
    recoveryCompleted = attributesRestored && workAreasRestored && positionsRestored && visibilityRestored && inputRestored;
}
catch
{
    // The next CrabDesk startup also processes the recovery marker.
}

try
{
    if (recoveryCompleted)
    {
        File.Delete(markerPath);
    }
}
catch
{
}

static RecoveryState ReadRecoveryState(string path, bool fallbackHidden)
{
    try
    {
        return JsonSerializer.Deserialize(File.ReadAllText(path), GuardJsonContext.Default.RecoveryState)
            ?? new RecoveryState { PreviousHidden = fallbackHidden };
    }
    catch
    {
        return new RecoveryState { PreviousHidden = fallbackHidden };
    }
}

internal sealed class RecoveryState
{
    public bool PreviousHidden { get; set; }
    public List<RecoveryIconPosition> IconPositions { get; set; } = [];
    public List<RecoveryWorkArea>? WorkAreas { get; set; }
    public List<RecoveryFileAttribute> FileAttributes { get; set; } = [];
}

internal readonly record struct RecoveryIconPosition(string DisplayName, int X, int Y);
internal readonly record struct RecoveryWorkArea(int Left, int Top, int Right, int Bottom);
internal readonly record struct RecoveryFileAttribute(string Path, int Attributes);

[JsonSerializable(typeof(RecoveryState))]
internal partial class GuardJsonContext : JsonSerializerContext;

internal static class ExplorerIcons
{
    private const string AdvancedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const uint WmCommand = 0x0111;
    private const uint SmtoAbortIfHung = 0x0002;
    private const int ToggleIconsCommand = 0x7402;
    private const uint LvmFirst = 0x1000;
    private const uint LvmGetItemCount = LvmFirst + 4;
    private const uint LvmGetItemTextW = LvmFirst + 115;
    private const uint LvmSetItemPosition32 = LvmFirst + 49;
    private const uint LvmSetWorkAreas = LvmFirst + 65;
    private const uint ProcessVmOperation = 0x0008;
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessVmWrite = 0x0020;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;
    private const uint PageReadWrite = 0x04;
    private const int TextBytes = 1024;
    private const uint MessageTimeoutMilliseconds = 500;

    internal static int RestorePositions(IReadOnlyList<RecoveryIconPosition> positions)
    {
        var listView = FindWindowEx(FindDesktopView(), IntPtr.Zero, "SysListView32", "FolderView");
        if (listView == IntPtr.Zero || positions.Count == 0)
        {
            return 0;
        }

        var byName = positions
            .GroupBy(position => NormalizeName(position.DisplayName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        GetWindowThreadProcessId(listView, out var processId);
        using var process = OpenProcess(
            ProcessVmOperation | ProcessVmRead | ProcessVmWrite | ProcessQueryInformation,
            false,
            processId);
        if (process.IsInvalid)
        {
            return 0;
        }

        var remoteItem = VirtualAllocEx(
            process,
            IntPtr.Zero,
            (nuint)Marshal.SizeOf<ListViewItem>(),
            MemCommit | MemReserve,
            PageReadWrite);
        var remoteText = VirtualAllocEx(process, IntPtr.Zero, TextBytes, MemCommit | MemReserve, PageReadWrite);
        var remotePoint = VirtualAllocEx(
            process,
            IntPtr.Zero,
            (nuint)Marshal.SizeOf<NativePoint>(),
            MemCommit | MemReserve,
            PageReadWrite);
        if (remoteItem == IntPtr.Zero || remoteText == IntPtr.Zero || remotePoint == IntPtr.Zero)
        {
            Free(process, remoteItem);
            Free(process, remoteText);
            Free(process, remotePoint);
            return 0;
        }

        try
        {
            var restored = 0;
            if (!TrySendMessage(
                    listView,
                    LvmGetItemCount,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    out var countResult))
            {
                return 0;
            }
            var count = countResult.ToInt32();
            for (var index = 0; index < count; index++)
            {
                var item = new ListViewItem
                {
                    Item = index,
                    SubItem = 0,
                    Text = remoteText,
                    TextMax = TextBytes / 2
                };
                WriteStructure(process, remoteItem, item);
                if (!TrySendMessage(
                        listView,
                        LvmGetItemTextW,
                        new IntPtr(index),
                        remoteItem,
                        out _))
                {
                    return restored;
                }
                var textBuffer = new byte[TextBytes];
                if (!ReadProcessMemory(process, remoteText, textBuffer, textBuffer.Length, out _))
                {
                    continue;
                }

                var text = System.Text.Encoding.Unicode.GetString(textBuffer);
                var terminator = text.IndexOf('\0');
                if (terminator >= 0)
                {
                    text = text[..terminator];
                }
                if (!byName.TryGetValue(NormalizeName(text), out var position))
                {
                    continue;
                }

                WriteStructure(process, remotePoint, new NativePoint { X = position.X, Y = position.Y });
                if (!TrySendMessage(
                        listView,
                        LvmSetItemPosition32,
                        new IntPtr(index),
                        remotePoint,
                        out var positionResult))
                {
                    return restored;
                }
                if (positionResult != IntPtr.Zero)
                {
                    restored++;
                }
            }
            return restored;
        }
        finally
        {
            Free(process, remoteItem);
            Free(process, remoteText);
            Free(process, remotePoint);
        }
    }

    internal static bool RestoreFileAttributes(IReadOnlyList<RecoveryFileAttribute> snapshots)
    {
        var complete = true;
        foreach (var snapshot in snapshots)
        {
            try
            {
                if (File.Exists(snapshot.Path) || Directory.Exists(snapshot.Path))
                {
                    File.SetAttributes(snapshot.Path, (FileAttributes)snapshot.Attributes);
                }
            }
            catch (IOException)
            {
                complete = false;
            }
            catch (UnauthorizedAccessException)
            {
                complete = false;
            }
        }
        return complete;
    }

    internal static bool RestoreWorkAreas(IReadOnlyList<RecoveryWorkArea> workAreas)
    {
        var listView = FindWindowEx(FindDesktopView(), IntPtr.Zero, "SysListView32", "FolderView");
        if (listView == IntPtr.Zero)
        {
            return false;
        }
        if (workAreas.Count == 0)
        {
            return TrySendMessage(
                listView,
                LvmSetWorkAreas,
                IntPtr.Zero,
                IntPtr.Zero,
                out _);
        }

        GetWindowThreadProcessId(listView, out var processId);
        using var process = OpenProcess(
            ProcessVmOperation | ProcessVmRead | ProcessVmWrite | ProcessQueryInformation,
            false,
            processId);
        if (process.IsInvalid)
        {
            return false;
        }

        var rectangles = workAreas.Select(area => new NativeRectangle
        {
            Left = area.Left,
            Top = area.Top,
            Right = area.Right,
            Bottom = area.Bottom
        }).ToArray();
        var size = Marshal.SizeOf<NativeRectangle>();
        var byteCount = checked(size * rectangles.Length);
        var local = Marshal.AllocHGlobal(byteCount);
        var remote = IntPtr.Zero;
        try
        {
            for (var index = 0; index < rectangles.Length; index++)
            {
                Marshal.StructureToPtr(rectangles[index], local + index * size, false);
            }
            var buffer = new byte[byteCount];
            Marshal.Copy(local, buffer, 0, byteCount);
            remote = VirtualAllocEx(
                process,
                IntPtr.Zero,
                (nuint)byteCount,
                MemCommit | MemReserve,
                PageReadWrite);
            return remote != IntPtr.Zero &&
                WriteProcessMemory(process, remote, buffer, buffer.Length, out var written) &&
                written == (nuint)buffer.Length &&
                TrySendMessage(
                    listView,
                    LvmSetWorkAreas,
                    new IntPtr(rectangles.Length),
                    remote,
                    out _);
        }
        finally
        {
            Marshal.FreeHGlobal(local);
            Free(process, remote);
        }
    }

    internal static bool SetHidden(bool hidden)
    {
        if (GetHidden() == hidden)
        {
            return true;
        }

        var view = FindDesktopView();
        if (view != IntPtr.Zero)
        {
            SendMessageTimeout(view, WmCommand, new IntPtr(ToggleIconsCommand), IntPtr.Zero, SmtoAbortIfHung, 1500, out _);
        }

        if (GetHidden() != hidden)
        {
            using var key = Registry.CurrentUser.CreateSubKey(AdvancedKey);
            key.SetValue("HideIcons", hidden ? 1 : 0, RegistryValueKind.DWord);
        }
        return GetHidden() == hidden;
    }

    internal static bool EnsureDesktopInputEnabled()
    {
        var view = FindDesktopView();
        var desktopParent = view == IntPtr.Zero ? IntPtr.Zero : GetParent(view);
        if (desktopParent == IntPtr.Zero)
        {
            return false;
        }
        if (IsWindowEnabled(desktopParent))
        {
            return true;
        }

        EnableWindow(desktopParent, true);
        return IsWindowEnabled(desktopParent);
    }

    private static bool TrySendMessage(
        IntPtr window,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        out IntPtr result) =>
        SendMessageTimeout(
            window,
            message,
            wParam,
            lParam,
            SmtoAbortIfHung,
            MessageTimeoutMilliseconds,
            out result) != IntPtr.Zero;

    private static bool GetHidden()
    {
        using var key = Registry.CurrentUser.OpenSubKey(AdvancedKey);
        return Convert.ToInt32(key?.GetValue("HideIcons", 0) ?? 0) != 0;
    }

    private static string NormalizeName(string value) => value.Trim().TrimEnd('.');

    private static IntPtr FindDesktopView()
    {
        var progman = FindWindow("Progman", "Program Manager");
        var view = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (view != IntPtr.Zero)
        {
            return view;
        }

        IntPtr found = IntPtr.Zero;
        EnumWindows((window, _) =>
        {
            var child = FindWindowEx(window, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (child == IntPtr.Zero)
            {
                return true;
            }
            found = child;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    private static void WriteStructure<T>(SafeProcessHandle process, IntPtr destination, T value)
        where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var local = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(value, local, false);
            var buffer = new byte[size];
            Marshal.Copy(local, buffer, 0, size);
            WriteProcessMemory(process, destination, buffer, buffer.Length, out _);
        }
        finally
        {
            Marshal.FreeHGlobal(local);
        }
    }

    private static void Free(SafeProcessHandle process, IntPtr address)
    {
        if (address != IntPtr.Zero)
        {
            VirtualFreeEx(process, address, 0, MemRelease);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ListViewItem
    {
        internal uint Mask;
        internal int Item;
        internal int SubItem;
        internal uint State;
        internal uint StateMask;
        internal IntPtr Text;
        internal int TextMax;
        internal int Image;
        internal IntPtr Parameter;
        internal int Indent;
        internal int GroupId;
        internal uint ColumnCount;
        internal IntPtr Columns;
        internal IntPtr ColumnFormats;
        internal int Group;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? title);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? title);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnableWindow(IntPtr window, bool enable);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hwnd,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        uint flags,
        uint timeout,
        out IntPtr result);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(
        SafeProcessHandle process,
        IntPtr address,
        nuint size,
        uint allocationType,
        uint protection);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualFreeEx(SafeProcessHandle process, IntPtr address, nuint size, uint freeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteProcessMemory(
        SafeProcessHandle process,
        IntPtr address,
        byte[] buffer,
        int size,
        out nuint written);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(
        SafeProcessHandle process,
        IntPtr address,
        byte[] buffer,
        int size,
        out nuint read);
}
