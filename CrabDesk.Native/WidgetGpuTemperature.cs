using System.Runtime.InteropServices;

namespace CrabDesk.Native;

/// <summary>
/// Reads the GPU temperature through NVML, the same library nvidia-smi wraps.
///
/// Calling nvidia-smi as a child process costs roughly 140 ms, which is far too
/// slow for a once-per-second sampler, and it would put another process on the
/// desktop's critical path. NVML is exported by nvml.dll in System32 and answers
/// in microseconds, so it is loaded directly and every entry point is optional:
/// a machine without an NVIDIA adapter simply reports no temperature.
/// </summary>
internal sealed partial class WidgetGpuTemperature : IDisposable
{
    private const string Library = "nvml.dll";

    private IntPtr _device;
    private bool _attempted;
    private bool _disposed;
    // NVML resolves handlers by symbol; caching the ones we need avoids a
    // Marshal.GetDelegateForFunctionPointer call on every sample.
    private NvmlInit? _init;
    private NvmlShutdown? _shutdown;
    private NvmlDeviceGetHandleByIndex? _deviceByIndex;
    private NvmlDeviceGetTemperature? _temperature;

    /// <summary>
    /// Temperature of the first GPU in Celsius, or null when NVML is missing,
    /// has no device, or the driver refuses the query. Never throws: a missing
    /// sensor must not disturb the rest of the sample.
    /// </summary>
    public double? Read()
    {
        if (_disposed || !Initialize()) return null;
        try
        {
            uint celsius = 0;
            // 0 is NVML_TEMPERATURE_GPU; the edge sensor is the one reported by
            // nvidia-smi and the one that tracks load.
            return _temperature!(_device, 0, ref celsius) == 0 && celsius is > 0 and < 200 ? celsius : null;
        }
        catch (Exception)
        {
            // A driver that unloads or a call that faults must degrade to "no
            // reading" rather than take the performance card down with it.
            return null;
        }
    }

    private bool Initialize()
    {
        if (_attempted) return _device != IntPtr.Zero;
        _attempted = true;
        try
        {
            if (!NativeLibrary.TryLoad(Library, out var library)) return false;
            _init = Bind<NvmlInit>(library, "nvmlInit_v2");
            _shutdown = Bind<NvmlShutdown>(library, "nvmlShutdown");
            _deviceByIndex = Bind<NvmlDeviceGetHandleByIndex>(library, "nvmlDeviceGetHandleByIndex_v2");
            _temperature = Bind<NvmlDeviceGetTemperature>(library, "nvmlDeviceGetTemperature");
            if (_init is null || _deviceByIndex is null || _temperature is null || _init() != 0) return false;
            var device = IntPtr.Zero;
            if (_deviceByIndex(0, ref device) != 0 || device == IntPtr.Zero) return false;
            _device = device;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static T? Bind<T>(IntPtr library, string name) where T : Delegate =>
        NativeLibrary.TryGetExport(library, name, out var address) && address != IntPtr.Zero
            ? Marshal.GetDelegateForFunctionPointer<T>(address) : null;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_device != IntPtr.Zero)
        {
            try { _shutdown?.Invoke(); } catch (Exception) { /* teardown must not throw */ }
            _device = IntPtr.Zero;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlInit();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlShutdown();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlDeviceGetHandleByIndex(uint index, ref IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlDeviceGetTemperature(IntPtr device, int sensor, ref uint celsius);
}
