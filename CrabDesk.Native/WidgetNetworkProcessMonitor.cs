using System.Diagnostics;
using System.Net;
using CrabDesk.Core;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace CrabDesk.Native;

/// <summary>Actual TCP/UDP traffic across all interfaces; never substitutes process disk I/O.</summary>
internal sealed class WidgetNetworkProcessMonitor : IDisposable
{
    private readonly object _gate = new();
    private readonly WidgetNetworkProcessAccumulator _traffic = new();
    private TraceEventSession? _session;
    private bool _started, _disposed;
    private NetworkProcessStatus _status = NetworkProcessStatus.WarmingUp;
    private int _eventsLost;

    public WidgetNetworkProcesses Sample()
    {
        lock (_gate)
        {
            if (_disposed) return new(NetworkProcessStatus.Unavailable, []);
            if (!_started) Start();
            if (_status is NetworkProcessStatus.PermissionRequired or NetworkProcessStatus.Unavailable)
                return new(_status, []);
            if (_session is { } session && session.Source.EventsLost != _eventsLost)
            {
                _eventsLost = session.Source.EventsLost;
                _traffic.Reset();
            }
            return _traffic.Sample(Stopwatch.GetTimestamp());
        }
    }

    private void Start()
    {
        _started = true;
        if (TraceEventSession.IsElevated() != true)
        {
            _status = NetworkProcessStatus.PermissionRequired;
            return;
        }
        TraceEventSession? session = null;
        try
        {
            // Unique ownership: never attach to or stop another program's kernel session.
            session = new TraceEventSession($"CrabDesk.Network.{Environment.ProcessId}.{Guid.NewGuid():N}")
            { BufferSizeMB = 8, StopOnDispose = true };
            // Source starts the session itself, so enable the kernel provider first.
            session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);
            var source = session.Source;
            var parser = source.Kernel;
            parser.TcpIpRecv += data => _traffic.Record(data.ProcessID, data.size, false, data.saddr, data.daddr);
            parser.TcpIpSend += data => _traffic.Record(data.ProcessID, data.size, true, data.saddr, data.daddr);
            parser.TcpIpRecvIPV6 += data => _traffic.Record(data.ProcessID, data.size, false, data.saddr, data.daddr);
            parser.TcpIpSendIPV6 += data => _traffic.Record(data.ProcessID, data.size, true, data.saddr, data.daddr);
            parser.UdpIpRecv += data => _traffic.Record(data.ProcessID, data.size, false, data.saddr, data.daddr);
            parser.UdpIpSend += data => _traffic.Record(data.ProcessID, data.size, true, data.saddr, data.daddr);
            parser.UdpIpRecvIPV6 += data => _traffic.Record(data.ProcessID, data.size, false, data.saddr, data.daddr);
            parser.UdpIpSendIPV6 += data => _traffic.Record(data.ProcessID, data.size, true, data.saddr, data.daddr);
            _session = session;
            _ = Task.Run(() =>
            {
                try { source.Process(); }
                catch (Exception)
                {
                    lock (_gate)
                        if (!_disposed) _status = NetworkProcessStatus.Unavailable;
                }
                finally
                {
                    lock (_gate)
                        if (!_disposed) _status = NetworkProcessStatus.Unavailable;
                }
            });
        }
        catch (Exception exception)
        {
            session?.Dispose();
            _status = exception is UnauthorizedAccessException ||
                exception is System.ComponentModel.Win32Exception { NativeErrorCode: 5 }
                ? NetworkProcessStatus.PermissionRequired : NetworkProcessStatus.Unavailable;
        }
    }

    public void Dispose()
    {
        TraceEventSession? session;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            session = _session;
            _session = null;
        }
        // Stop outside the lock: the reader reports its completion under the same lock.
        session?.Dispose();
    }
}

internal sealed class WidgetNetworkProcessAccumulator
{
    private readonly object _gate = new();
    private readonly Dictionary<int, (long Download, long Upload)> _bytes = [];
    private readonly Func<int, string> _name;
    private long? _lastSample;

    public WidgetNetworkProcessAccumulator(Func<int, string>? name = null) => _name = name ?? ProcessName;

    public void Record(int processId, int bytes, bool upload, IPAddress source, IPAddress destination)
    {
        if (processId <= 0 || bytes <= 0 || IsLoopback(source) || IsLoopback(destination)) return;
        lock (_gate)
        {
            var previous = _bytes.GetValueOrDefault(processId);
            _bytes[processId] = upload ? (previous.Download, previous.Upload + bytes) : (previous.Download + bytes, previous.Upload);
        }
    }

    public WidgetNetworkProcesses Sample(long timestamp)
    {
        KeyValuePair<int, (long Download, long Upload)>[] bytes;
        double elapsed;
        lock (_gate)
        {
            elapsed = _lastSample is { } previous ? (timestamp - previous) / (double)Stopwatch.Frequency : 0;
            if (elapsed == 0 && _lastSample is not null) return WidgetNetworkProcesses.Waiting;
            _lastSample = timestamp;
            bytes = _bytes.ToArray();
            _bytes.Clear();
        }
        if (elapsed <= 0 || elapsed > 5) return WidgetNetworkProcesses.Waiting;
        return new(NetworkProcessStatus.Ready, bytes.Select(pair =>
            new WidgetNetworkProcess(pair.Key, _name(pair.Key), pair.Value.Download / elapsed, pair.Value.Upload / elapsed)).ToArray());
    }

    public void Reset()
    {
        lock (_gate) { _bytes.Clear(); _lastSample = null; }
    }

    private static bool IsLoopback(IPAddress address) => IPAddress.IsLoopback(
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);

    private static string ProcessName(int id)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            return process.ProcessName;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return $"PID {id}"; }
    }
}
