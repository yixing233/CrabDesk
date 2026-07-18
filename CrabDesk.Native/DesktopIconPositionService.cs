using System.IO;
using System.Runtime.InteropServices;
using CrabDesk.Core;
using Microsoft.Win32.SafeHandles;

namespace CrabDesk.Native;

public static class DesktopIconPositionService
{
    private const string DesktopBagPath = @"Software\Microsoft\Windows\Shell\Bags\1\Desktop";
    private const uint LvmFirst = 0x1000;
    private const uint LvmGetNextItem = LvmFirst + 12;
    private const uint LvmSetItemPosition = LvmFirst + 15;
    private const uint LvmGetItemPosition = LvmFirst + 16;
    private const int LvniSelected = 0x0002;
    private const uint LvmGetItemCount = LvmFirst + 4;
    private const uint LvmGetItemTextW = LvmFirst + 115;
    private const uint LvmSetItemPosition32 = LvmFirst + 49;
    private const uint LvmGetItemSpacing = LvmFirst + 51;
    private const uint LvmSetWorkAreas = LvmFirst + 65;
    private const uint LvmGetWorkAreas = LvmFirst + 70;
    private const uint LvmGetNumberOfWorkAreas = LvmFirst + 73;
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
    private const uint MessageTimeoutMilliseconds = 500;
    private const uint WmMouseWheel = 0x020A;
    private const uint MkControl = 0x0008;

    public static int? GetDesktopIconSize()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(DesktopBagPath);
            return key?.GetValue("IconSize") is int value ? Math.Clamp(value, 16, 256) : null;
        }
        catch
        {
            return null;
        }
    }

    public static bool ForwardControlMouseWheel(IntPtr listView, int screenX, int screenY, int delta)
    {
        if (listView == IntPtr.Zero || !NativeMethods.IsWindow(listView) || delta == 0)
        {
            return false;
        }

        var wheel = unchecked((uint)(ushort)(short)delta);
        var keysAndDelta = new IntPtr(unchecked((int)((wheel << 16) | MkControl)));
        var coordinates = unchecked((uint)(ushort)(short)screenX) |
            (unchecked((uint)(ushort)(short)screenY) << 16);
        return NativeMethods.PostMessage(
            listView,
            WmMouseWheel,
            keysAndDelta,
            new IntPtr(unchecked((int)coordinates)));
    }

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
            if (!TrySendListViewMessage(
                    listView,
                    LvmGetNextItem,
                    new IntPtr(index),
                    new IntPtr(LvniSelected),
                    out var nextItem))
            {
                break;
            }
            index = nextItem.ToInt32();
            if (index < 0)
            {
                break;
            }

            var x = Math.Max(0, origin.X + (offset % 5) * 84);
            var y = Math.Max(0, origin.Y + (offset / 5) * 92);
            var packed = new IntPtr((y << 16) | (x & 0xFFFF));
            if (!TrySendListViewMessage(
                    listView,
                    LvmSetItemPosition,
                    new IntPtr(index),
                    packed,
                    out var positioned) ||
                positioned == IntPtr.Zero)
            {
                break;
            }
            offset++;
        }
    }

    public static System.Drawing.Size GetItemSpacing(IntPtr listView)
    {
        if (!TrySendListViewMessage(
                listView,
                LvmGetItemSpacing,
                IntPtr.Zero,
                IntPtr.Zero,
                out var result))
        {
            return new System.Drawing.Size(88, 96);
        }
        var packed = result.ToInt64();
        var horizontal = unchecked((ushort)(packed & 0xffff));
        var vertical = unchecked((ushort)((packed >> 16) & 0xffff));
        return new System.Drawing.Size(
            horizontal > 0 ? horizontal : 88,
            vertical > 0 ? vertical : 96);
    }

    public static DesktopGridAlignmentResult AlignItemsToGrid(IntPtr listView)
    {
        var spacing = GetItemSpacing(listView);
        if (!RemoteListViewSession.TryCreate(listView, out var session))
        {
            return new DesktopGridAlignmentResult(0, 0, spacing.Width, spacing.Height);
        }

        using (session)
        {
            var entries = new List<(int Index, DesktopIconPositionSnapshot Position)>();
            for (var index = 0; index < session.ItemCount; index++)
            {
                var text = session.ReadText(index);
                if (text is null || !session.TryGetPosition(index, out var point))
                {
                    continue;
                }
                entries.Add((index, new DesktopIconPositionSnapshot(text, point.X, point.Y)));
            }

            var aligned = DesktopIconGridLayout.Align(
                entries.Select(entry => entry.Position),
                spacing.Width,
                spacing.Height);
            var requested = 0;
            var applied = 0;
            for (var index = 0; index < entries.Count; index++)
            {
                var current = entries[index];
                var target = aligned[index];
                if (current.Position.X == target.X && current.Position.Y == target.Y)
                {
                    continue;
                }
                requested++;
                if (session.SetPosition(current.Index, new NativePoint { X = target.X, Y = target.Y }))
                {
                    applied++;
                }
            }
            return new DesktopGridAlignmentResult(requested, applied, spacing.Width, spacing.Height);
        }
    }

    public static IReadOnlyList<System.Drawing.Rectangle> GetWorkAreas(IntPtr listView)
    {
        if (!RemoteListViewSession.TryCreate(listView, out var session))
        {
            return [];
        }
        using (session)
        {
            return session.GetWorkAreas();
        }
    }

    public static bool SetWorkAreas(
        IntPtr listView,
        IReadOnlyCollection<System.Drawing.Rectangle> workAreas)
    {
        if (!RemoteListViewSession.TryCreate(listView, out var session))
        {
            return false;
        }
        using (session)
        {
            return session.SetWorkAreas(workAreas);
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

    public static IReadOnlyList<DesktopIconPositionSnapshot> CaptureAllItemPositions(IntPtr listView)
    {
        if (!RemoteListViewSession.TryCreate(listView, out var session))
        {
            return [];
        }

        using (session)
        {
            var positions = new List<DesktopIconPositionSnapshot>(session.ItemCount);
            for (var index = 0; index < session.ItemCount; index++)
            {
                var text = session.ReadText(index);
                if (text is not null && session.TryGetPosition(index, out var point))
                {
                    positions.Add(new DesktopIconPositionSnapshot(text, point.X, point.Y));
                }
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
            .ToDictionary(
                group => group.Key,
                group => new Queue<DesktopIconPositionSnapshot>(group),
                StringComparer.OrdinalIgnoreCase);
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
                if (text is null ||
                    !byName.TryGetValue(NormalizeName(text), out var queue) ||
                    queue.Count == 0)
                {
                    continue;
                }

                var position = queue.Dequeue();
                if (session.SetPosition(index, new NativePoint { X = position.X, Y = position.Y }))
                {
                    restored++;
                }
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

                if (session.SetPosition(index, new NativePoint
                {
                    X = Math.Max(0, origin.X + (moved % 5) * 84),
                    Y = Math.Max(0, origin.Y + (moved / 5) * 92)
                }))
                {
                    moved++;
                }
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
                if (session.SetPosition(index, new NativePoint
                {
                    X = point.X,
                    Y = point.Y
                }))
                {
                    moved++;
                }
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
            return session.TryHitTest(new NativePoint { X = clientX, Y = clientY }, out var item) && item < 0;
        }
    }

    private static bool TrySendListViewMessage(
        IntPtr listView,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        out IntPtr result) =>
        NativeMethods.SendMessageTimeout(
            listView,
            message,
            wParam,
            lParam,
            NativeMethods.SmtoAbortIfHung,
            MessageTimeoutMilliseconds,
            out result) != IntPtr.Zero;

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
    private struct NativeRectangle
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
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
        private bool _messageFailed;

        private RemoteListViewSession(
            IntPtr listView,
            SafeProcessHandle process,
            IntPtr remoteItem,
            IntPtr remoteText,
            IntPtr remotePoint,
            int itemCount)
        {
            _listView = listView;
            _process = process;
            _remoteItem = remoteItem;
            _remoteText = remoteText;
            _remotePoint = remotePoint;
            ItemCount = itemCount;
        }

        internal int ItemCount { get; }

        internal static bool TryCreate(IntPtr listView, out RemoteListViewSession session)
        {
            session = null!;
            if (listView == IntPtr.Zero || !NativeMethods.IsWindow(listView))
            {
                return false;
            }

            if (!TrySendListViewMessage(
                    listView,
                    LvmGetItemCount,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    out var itemCountResult))
            {
                return false;
            }
            var itemCount = itemCountResult.ToInt32();
            if (itemCount < 0)
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

            session = new RemoteListViewSession(listView, process, remoteItem, remoteText, remotePoint, itemCount);
            return true;
        }

        internal string? ReadText(int index)
        {
            if (_messageFailed)
            {
                return null;
            }
            var item = new ListViewItem
            {
                Item = index,
                SubItem = 0,
                Text = _remoteText,
                TextMax = TextBytes / 2
            };
            if (!WriteStructure(_process, _remoteItem, item) ||
                !TrySendListViewMessage(
                    _listView,
                    LvmGetItemTextW,
                    new IntPtr(index),
                    _remoteItem,
                    out _))
            {
                _messageFailed = true;
                return null;
            }

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
            if (_messageFailed ||
                !TrySendListViewMessage(
                    _listView,
                    LvmGetItemPosition,
                    new IntPtr(index),
                    _remotePoint,
                    out var result) ||
                result == IntPtr.Zero)
            {
                _messageFailed = true;
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

        internal bool SetPosition(int index, NativePoint point)
        {
            if (_messageFailed || !WriteStructure(_process, _remotePoint, point) ||
                !TrySendListViewMessage(
                    _listView,
                    LvmSetItemPosition32,
                    new IntPtr(index),
                    _remotePoint,
                    out var result) ||
                result == IntPtr.Zero)
            {
                _messageFailed = true;
                return false;
            }
            return true;
        }

        internal bool TryHitTest(NativePoint point, out int item)
        {
            item = -1;
            if (_messageFailed ||
                !WriteStructure(_process, _remoteItem, new ListViewHitTestInfo { Point = point }) ||
                !TrySendListViewMessage(
                    _listView,
                    LvmHitTest,
                    IntPtr.Zero,
                    _remoteItem,
                    out var result))
            {
                _messageFailed = true;
                return false;
            }
            item = result.ToInt32();
            return true;
        }

        internal IReadOnlyList<System.Drawing.Rectangle> GetWorkAreas()
        {
            if (_messageFailed ||
                !TrySendListViewMessage(
                    _listView,
                    LvmGetNumberOfWorkAreas,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    out var countResult))
            {
                _messageFailed = true;
                return [];
            }
            var count = countResult.ToInt32();
            if (count <= 0)
            {
                return [];
            }

            var size = Marshal.SizeOf<NativeRectangle>();
            var byteCount = checked(size * count);
            var remote = VirtualAllocEx(
                _process,
                IntPtr.Zero,
                (nuint)byteCount,
                MemCommit | MemReserve,
                PageReadWrite);
            if (remote == IntPtr.Zero)
            {
                return [];
            }
            try
            {
                if (!TrySendListViewMessage(
                        _listView,
                        LvmGetWorkAreas,
                        new IntPtr(count),
                        remote,
                        out _))
                {
                    _messageFailed = true;
                    return [];
                }
                var buffer = new byte[byteCount];
                if (!ReadProcessMemory(_process, remote, buffer, buffer.Length, out var read) ||
                    read != (nuint)buffer.Length)
                {
                    return [];
                }
                var result = new List<System.Drawing.Rectangle>(count);
                var local = Marshal.AllocHGlobal(byteCount);
                try
                {
                    Marshal.Copy(buffer, 0, local, byteCount);
                    for (var index = 0; index < count; index++)
                    {
                        var rectangle = Marshal.PtrToStructure<NativeRectangle>(local + index * size);
                        result.Add(System.Drawing.Rectangle.FromLTRB(
                            rectangle.Left,
                            rectangle.Top,
                            rectangle.Right,
                            rectangle.Bottom));
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(local);
                }
                return result;
            }
            finally
            {
                Free(_process, remote);
            }
        }

        internal bool SetWorkAreas(IReadOnlyCollection<System.Drawing.Rectangle> workAreas)
        {
            if (_messageFailed)
            {
                return false;
            }
            if (workAreas.Count == 0)
            {
                return TrySendListViewMessage(
                    _listView,
                    LvmSetWorkAreas,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    out _);
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
                    _process,
                    IntPtr.Zero,
                    (nuint)byteCount,
                    MemCommit | MemReserve,
                    PageReadWrite);
                return remote != IntPtr.Zero &&
                    WriteProcessMemory(_process, remote, buffer, buffer.Length, out var written) &&
                    written == (nuint)buffer.Length &&
                    TrySendListViewMessage(
                        _listView,
                        LvmSetWorkAreas,
                        new IntPtr(rectangles.Length),
                        remote,
                        out _);
            }
            finally
            {
                Marshal.FreeHGlobal(local);
                Free(_process, remote);
            }
        }

        public void Dispose()
        {
            Free(_process, _remoteItem);
            Free(_process, _remoteText);
            Free(_process, _remotePoint);
            _process.Dispose();
        }
    }

    private static bool WriteStructure<T>(SafeProcessHandle process, IntPtr destination, T value)
        where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var local = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(value, local, false);
            var buffer = new byte[size];
            Marshal.Copy(local, buffer, 0, size);
            return WriteProcessMemory(process, destination, buffer, buffer.Length, out var written) &&
                written == (nuint)buffer.Length;
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

public sealed record DesktopGridAlignmentResult(
    int Requested,
    int Applied,
    int HorizontalSpacing,
    int VerticalSpacing);
