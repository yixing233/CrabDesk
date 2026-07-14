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

try
{
    var recovery = ReadRecoveryState(markerPath, previousHidden);
    if (recovery.IconPositions.Count > 0)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            if (ExplorerIcons.RestorePositions(recovery.IconPositions) >= recovery.IconPositions.Count)
            {
                break;
            }
            Thread.Sleep(500);
        }
    }
    ExplorerIcons.SetHidden(recovery.PreviousHidden);
}
catch
{
    // The next CrabDesk startup also processes the recovery marker.
}

try
{
    File.Delete(markerPath);
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
}

internal readonly record struct RecoveryIconPosition(string DisplayName, int X, int Y);

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
    private const uint ProcessVmOperation = 0x0008;
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessVmWrite = 0x0020;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;
    private const uint PageReadWrite = 0x04;
    private const int TextBytes = 1024;

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
            var count = SendMessage(listView, LvmGetItemCount, IntPtr.Zero, IntPtr.Zero).ToInt32();
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
                SendMessage(listView, LvmGetItemTextW, new IntPtr(index), remoteItem);
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
                SendMessage(listView, LvmSetItemPosition32, new IntPtr(index), remotePoint);
                restored++;
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

    internal static void SetHidden(bool hidden)
    {
        if (GetHidden() == hidden)
        {
            return;
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
    }

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

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? title);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? title);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

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
