using System.IO;
using System.Runtime.InteropServices;
using CrabDesk.Core;
using Microsoft.Win32.SafeHandles;

namespace CrabDesk.Native;

public static class DesktopIconPositionService
{
    private const uint LvmFirst = 0x1000;
    private const uint LvmGetNextItem = LvmFirst + 12;
    private const uint LvmSetItemPosition = LvmFirst + 15;
    private const uint LvmGetItemPosition = LvmFirst + 16;
    private const int LvniSelected = 0x0002;
    private const uint LvmGetItemCount = LvmFirst + 4;
    private const uint LvmGetItemTextW = LvmFirst + 115;
    private const uint LvmSetItemPosition32 = LvmFirst + 49;
    private const uint LvmHitTest = LvmFirst + 18;
    private const uint ProcessVmOperation = 0x0008;
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessVmWrite = 0x0020;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;
    private const uint PageReadWrite = 0x04;
    private const int TextBytes = 1024;

    public static void MoveSelectedIcons(IntPtr listView, int screenX, int screenY)
    {
        if (listView == IntPtr.Zero || !NativeMethods.IsWindow(listView))
        {
            return;
        }

        var origin = new NativeMethods.Point { X = screenX, Y = screenY };
        if (!NativeMethods.ScreenToClient(listView, ref origin))
        {
            return;
        }

        var index = -1;
        var offset = 0;
        while (true)
        {
            index = NativeMethods.SendMessage(
                listView,
                LvmGetNextItem,
                new IntPtr(index),
                new IntPtr(LvniSelected)).ToInt32();
            if (index < 0)
            {
                break;
            }

            var x = Math.Max(0, origin.X + (offset % 5) * 84);
            var y = Math.Max(0, origin.Y + (offset / 5) * 92);
            var packed = new IntPtr((y << 16) | (x & 0xFFFF));
            NativeMethods.SendMessage(listView, LvmSetItemPosition, new IntPtr(index), packed);
            offset++;
        }
    }

    public static IReadOnlyList<DesktopIconPositionSnapshot> CaptureItemPositions(
        IntPtr listView,
        IEnumerable<string> displayNames)
    {
        var names = BuildNameSet(displayNames);
        if (names.Count == 0 || !RemoteListViewSession.TryCreate(listView, out var session))
        {
            return [];
        }

        using (session)
        {
            var positions = new List<DesktopIconPositionSnapshot>();
            for (var index = 0; index < session.ItemCount; index++)
            {
                var text = session.ReadText(index);
                if (text is null || !names.Contains(NormalizeName(text)) ||
                    !session.TryGetPosition(index, out var point))
                {
                    continue;
                }

                positions.Add(new DesktopIconPositionSnapshot(text, point.X, point.Y));
            }
            return positions;
        }
    }

    public static int RestoreItemPositions(
        IntPtr listView,
        IEnumerable<DesktopIconPositionSnapshot> positions)
    {
        var byName = positions
            .GroupBy(position => NormalizeName(position.DisplayName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        if (byName.Count == 0 || !RemoteListViewSession.TryCreate(listView, out var session))
        {
            return 0;
        }

        using (session)
        {
            var restored = 0;
            for (var index = 0; index < session.ItemCount; index++)
            {
                var text = session.ReadText(index);
                if (text is null || !byName.TryGetValue(NormalizeName(text), out var position))
                {
                    continue;
                }

                session.SetPosition(index, new NativePoint { X = position.X, Y = position.Y });
                restored++;
            }
            return restored;
        }
    }

    public static int MoveItemsUnderBox(IntPtr listView, IEnumerable<string> displayNames, int screenX, int screenY)
    {
        var names = BuildNameSet(displayNames);
        if (names.Count == 0 || !RemoteListViewSession.TryCreate(listView, out var session))
        {
            return 0;
        }

        using (session)
        {
            var origin = new NativeMethods.Point { X = screenX, Y = screenY };
            if (!NativeMethods.ScreenToClient(listView, ref origin))
            {
                return 0;
            }

            var moved = 0;
            for (var index = 0; index < session.ItemCount; index++)
            {
                var text = session.ReadText(index);
                if (text is null || !names.Contains(NormalizeName(text)))
                {
                    continue;
                }

                session.SetPosition(index, new NativePoint
                {
                    X = Math.Max(0, origin.X + (moved % 5) * 84),
                    Y = Math.Max(0, origin.Y + (moved / 5) * 92)
                });
                moved++;
            }
            return moved;
        }
    }

    public static int MoveItemsUnderBox(
        IntPtr listView,
        IEnumerable<DesktopIconPlacement> placements)
    {
        var placementQueues = new Dictionary<string, Queue<DesktopIconPlacement>>(StringComparer.OrdinalIgnoreCase);
        foreach (var placement in placements)
        {
            foreach (var name in placement.DisplayNames
                         .Where(name => !string.IsNullOrWhiteSpace(name))
                         .SelectMany(name => new[] { name, Path.GetFileNameWithoutExtension(name) })
                         .Select(NormalizeName)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!placementQueues.TryGetValue(name, out var queue))
                {
                    queue = new Queue<DesktopIconPlacement>();
                    placementQueues[name] = queue;
                }
                queue.Enqueue(placement);
            }
        }
        if (placementQueues.Count == 0 || !RemoteListViewSession.TryCreate(listView, out var session))
        {
            return 0;
        }

        using (session)
        {
            var moved = 0;
            for (var index = 0; index < session.ItemCount; index++)
            {
                var text = session.ReadText(index);
                if (text is null ||
                    !placementQueues.TryGetValue(NormalizeName(text), out var queue) ||
                    queue.Count == 0)
                {
                    continue;
                }

                var placement = queue.Dequeue();
                var point = new NativeMethods.Point { X = placement.ScreenX, Y = placement.ScreenY };
                if (!NativeMethods.ScreenToClient(listView, ref point))
                {
                    continue;
                }
                session.SetPosition(index, new NativePoint
                {
                    X = Math.Max(0, point.X),
                    Y = Math.Max(0, point.Y)
                });
                moved++;
            }
            return moved;
        }
    }

    public static bool IsEmptyPoint(IntPtr listView, int clientX, int clientY)
    {
        if (!RemoteListViewSession.TryCreate(listView, out var session))
        {
            return false;
        }
        using (session)
        {
            return session.HitTest(new NativePoint { X = clientX, Y = clientY }) < 0;
        }
    }

    private static HashSet<string> BuildNameSet(IEnumerable<string> displayNames) => displayNames
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .SelectMany(name => new[] { name, Path.GetFileNameWithoutExtension(name) })
        .Select(NormalizeName)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string NormalizeName(string value) => value.Trim().TrimEnd('.');

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
    private struct ListViewHitTestInfo
    {
        internal NativePoint Point;
        internal uint Flags;
        internal int Item;
        internal int SubItem;
        internal int Group;
    }

    private sealed class RemoteListViewSession : IDisposable
    {
        private readonly IntPtr _listView;
        private readonly SafeProcessHandle _process;
        private readonly IntPtr _remoteItem;
        private readonly IntPtr _remoteText;
        private readonly IntPtr _remotePoint;

        private RemoteListViewSession(
            IntPtr listView,
            SafeProcessHandle process,
            IntPtr remoteItem,
            IntPtr remoteText,
            IntPtr remotePoint)
        {
            _listView = listView;
            _process = process;
            _remoteItem = remoteItem;
            _remoteText = remoteText;
            _remotePoint = remotePoint;
            ItemCount = NativeMethods.SendMessage(listView, LvmGetItemCount, IntPtr.Zero, IntPtr.Zero).ToInt32();
        }

        internal int ItemCount { get; }

        internal static bool TryCreate(IntPtr listView, out RemoteListViewSession session)
        {
            session = null!;
            if (listView == IntPtr.Zero || !NativeMethods.IsWindow(listView))
            {
                return false;
            }

            GetWindowThreadProcessId(listView, out var processId);
            var process = OpenProcess(
                ProcessVmOperation | ProcessVmRead | ProcessVmWrite | ProcessQueryInformation,
                false,
                processId);
            if (process.IsInvalid)
            {
                process.Dispose();
                return false;
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
                process.Dispose();
                return false;
            }

            session = new RemoteListViewSession(listView, process, remoteItem, remoteText, remotePoint);
            return true;
        }

        internal string? ReadText(int index)
        {
            var item = new ListViewItem
            {
                Item = index,
                SubItem = 0,
                Text = _remoteText,
                TextMax = TextBytes / 2
            };
            WriteStructure(_process, _remoteItem, item);
            NativeMethods.SendMessage(_listView, LvmGetItemTextW, new IntPtr(index), _remoteItem);

            var textBuffer = new byte[TextBytes];
            if (!ReadProcessMemory(_process, _remoteText, textBuffer, textBuffer.Length, out _))
            {
                return null;
            }

            var text = System.Text.Encoding.Unicode.GetString(textBuffer);
            var terminator = text.IndexOf('\0');
            return terminator >= 0 ? text[..terminator] : text;
        }

        internal bool TryGetPosition(int index, out NativePoint point)
        {
            point = default;
            if (NativeMethods.SendMessage(_listView, LvmGetItemPosition, new IntPtr(index), _remotePoint) == IntPtr.Zero)
            {
                return false;
            }

            var buffer = new byte[Marshal.SizeOf<NativePoint>()];
            if (!ReadProcessMemory(_process, _remotePoint, buffer, buffer.Length, out _))
            {
                return false;
            }

            var local = Marshal.AllocHGlobal(buffer.Length);
            try
            {
                Marshal.Copy(buffer, 0, local, buffer.Length);
                point = Marshal.PtrToStructure<NativePoint>(local);
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(local);
            }
        }

        internal void SetPosition(int index, NativePoint point)
        {
            WriteStructure(_process, _remotePoint, point);
            NativeMethods.SendMessage(_listView, LvmSetItemPosition32, new IntPtr(index), _remotePoint);
        }

        internal int HitTest(NativePoint point)
        {
            WriteStructure(_process, _remoteItem, new ListViewHitTestInfo { Point = point });
            return NativeMethods.SendMessage(_listView, LvmHitTest, IntPtr.Zero, _remoteItem).ToInt32();
        }

        public void Dispose()
        {
            Free(_process, _remoteItem);
            Free(_process, _remoteText);
            Free(_process, _remotePoint);
            _process.Dispose();
        }
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

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

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

public sealed record DesktopIconPlacement(
    IReadOnlyList<string> DisplayNames,
    int ScreenX,
    int ScreenY);
