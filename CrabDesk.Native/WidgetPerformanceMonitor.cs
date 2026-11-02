using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using CrabDesk.Core;

namespace CrabDesk.Native;

/// <summary>Samples system performance on a serialized background worker.</summary>
public sealed class WidgetPerformanceMonitor : IDisposable
{
    private const uint FormatDouble = 0x00000200;
    private const uint FormatNoCap100 = 0x00008000;
    private const uint MoreData = 0x800007D2;
    private readonly object _gate = new();
    private PdhQueryHandle? _query;
    private bool _initialized;
    private bool _disposed;
    private bool _hasPreviousSample;
    private IntPtr _cpu, _cpuFallback, _gpu, _diskIdle, _diskRead, _diskWrite;
    private long _capacityTimestamp;
    private bool _capacityChecked;
    private long? _diskUsedBytes, _diskTotalBytes;
    private readonly WidgetHardwareInventory _hardware = new();

    public WidgetPerformanceSnapshot Sample()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            InitializeCounters();
            var collected = _query is not null && PdhCollectQueryData(_query) == 0;
            var ready = collected && _hasPreviousSample;
            _hasPreviousSample = collected;

            double? cpu = null, gpu = null, active = null, read = null, write = null;
            if (ready)
            {
                cpu = Percentage(ReadCounter(_cpu) ?? ReadCounter(_cpuFallback));
                gpu = ReadGpu();
                var idle = ReadCounter(_diskIdle);
                active = idle.HasValue ? Percentage(100 - idle.Value) : null;
                read = ReadCounter(_diskRead);
                write = ReadCounter(_diskWrite);
            }

            var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            var memoryAvailable = GlobalMemoryStatusEx(ref memory);
            RefreshCapacity();
            return new WidgetPerformanceSnapshot(DateTimeOffset.Now, cpu, gpu,
                memoryAvailable ? (long)(memory.TotalPhysical - memory.AvailablePhysical) : 0,
                memoryAvailable ? (long)memory.TotalPhysical : 0, active, read, write,
                _diskUsedBytes, _diskTotalBytes)
            {
                // Cached inside the inventory, so this costs nothing after the
                // first sample and never touches the counters above.
                Hardware = _hardware.Read()
            };
        }
    }

    private void InitializeCounters()
    {
        if (_initialized) return;
        _initialized = true;
        if (PdhOpenQueryW(null, UIntPtr.Zero, out var query) != 0) return;
        _query = new PdhQueryHandle(query);
        _cpu = AddCounter(@"\Processor Information(_Total)\% Processor Time");
        _cpuFallback = AddCounter(@"\Processor(_Total)\% Processor Time");
        _gpu = AddCounter(@"\GPU Engine(*)\Utilization Percentage");
        _diskIdle = AddCounter(@"\PhysicalDisk(_Total)\% Idle Time");
        _diskRead = AddCounter(@"\PhysicalDisk(_Total)\Disk Read Bytes/sec");
        _diskWrite = AddCounter(@"\PhysicalDisk(_Total)\Disk Write Bytes/sec");
    }

    private IntPtr AddCounter(string path) =>
        PdhAddEnglishCounterW(_query!, path, UIntPtr.Zero, out var counter) == 0 ? counter : IntPtr.Zero;

    private static bool Valid(uint status) => status is 0 or 1; // VALID_DATA or NEW_DATA

    private static double? Percentage(double? value) => value is { } number && double.IsFinite(number)
        ? Math.Clamp(number, 0, 100) : null;

    private static double? ReadCounter(IntPtr counter)
    {
        if (counter == IntPtr.Zero) return null;
        return PdhGetFormattedCounterValue(counter, FormatDouble | FormatNoCap100, out _, out var value) == 0
            && Valid(value.Status) && double.IsFinite(value.Value) && value.Value >= 0 ? value.Value : null;
    }

    private double? ReadGpu()
    {
        if (_gpu == IntPtr.Zero) return null;
        // PDH expands the instance wildcard when collecting. Read the entire array to
        // include new process/engine instances without recreating and warming the query.
        // https://learn.microsoft.com/windows/win32/api/pdh/nf-pdh-pdhgetformattedcounterarrayw
        for (var attempt = 0; attempt < 2; attempt++)
        {
            uint size = 0;
            var status = PdhGetFormattedCounterArrayW(_gpu, FormatDouble | FormatNoCap100,
                ref size, out _, IntPtr.Zero);
            if (status != MoreData || size == 0 || size > 64 * 1024 * 1024) return null;
            var buffer = Marshal.AllocHGlobal(checked((int)size));
            try
            {
                status = PdhGetFormattedCounterArrayW(_gpu, FormatDouble | FormatNoCap100,
                    ref size, out var count, buffer);
                if (status == MoreData) continue;
                var stride = Marshal.SizeOf<CounterValueItem>();
                if (status != 0 || count > size / stride) return null;
                var samples = new List<(string Engine, double Value)>((int)count);
                for (var i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<CounterValueItem>(IntPtr.Add(buffer, i * stride));
                    if (!Valid(item.Value.Status)) continue;
                    var name = Marshal.PtrToStringUni(item.Name);
                    if (name is not null) samples.Add((name, item.Value.Value));
                }
                return WidgetMonitorMetrics.GpuUtilization(samples);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return null;
    }

    private void RefreshCapacity()
    {
        var now = Stopwatch.GetTimestamp();
        if (_capacityChecked && Stopwatch.GetElapsedTime(_capacityTimestamp, now) < TimeSpan.FromSeconds(30)) return;
        _capacityChecked = true;
        _capacityTimestamp = now;
        long used = 0, total = 0;
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                var size = drive.TotalSize;
                var free = drive.TotalFreeSpace;
                total += size;
                used += Math.Clamp(size - free, 0, size);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (System.Security.SecurityException) { }
        }
        _diskUsedBytes = total > 0 ? used : null;
        _diskTotalBytes = total > 0 ? total : null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _query?.Dispose(); // Closing the query releases all attached counters.
            _query = null;
        }
    }

    private sealed class PdhQueryHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public PdhQueryHandle(IntPtr query) : base(true) => SetHandle(query);
        protected override bool ReleaseHandle() => PdhCloseQuery(handle) == 0;
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct CounterValue
    {
        [FieldOffset(0)] public uint Status;
        [FieldOffset(8)] public double Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterValueItem
    {
        public IntPtr Name;
        public CounterValue Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile;
        public ulong TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhOpenQueryW(string? dataSource, UIntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhAddEnglishCounterW(PdhQueryHandle query, string path, UIntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCollectQueryData(PdhQueryHandle query);
    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out CounterValue value);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint size, out uint count, IntPtr buffer);
    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCloseQuery(IntPtr query);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus memory);
}
