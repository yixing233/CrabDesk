using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using CrabDesk.Core;

namespace CrabDesk.Native;

/// <summary>Samples one adapter; latency runs independently from the byte counters.</summary>
public sealed class WidgetNetworkMonitor : IDisposable
{
    private static readonly HttpClient PublicIpClient = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;
    private readonly RateSampler _rates = new();
    private string _latencyKey = "";
    private long _latencyGeneration;
    private bool _latencyRunning;
    private long? _latency;
    private string _latencyStatus = "等待测量";

    public static IReadOnlyList<WidgetNetworkAdapter> GetAdapters() => ReadAdapters()
        .OrderBy(adapter => adapter.Name, StringComparer.CurrentCultureIgnoreCase)
        .Select(adapter => new WidgetNetworkAdapter(adapter.Id, adapter.Name, NetworkType(adapter)))
        .ToArray();

    public WidgetNetworkSnapshot Sample(string adapterId, string latencyTarget)
    {
        lock (_gate)
        {
            if (_disposed) return new WidgetNetworkSnapshot(DateTimeOffset.Now);
            var adapter = SelectAdapter(adapterId);
            try
            {
                if (adapter is null || adapter.OperationalStatus != OperationalStatus.Up)
                    return Disconnected(adapterId, adapter);

                var properties = adapter.GetIPProperties();
                var gateway = Gateway(properties);
                var target = ResolveTarget(latencyTarget, gateway);
                SetLatencyKey(adapter.Id, target);
                var statistics = adapter.GetIPStatistics();
                var (download, upload) = _rates.Sample(adapter.Id, statistics.BytesReceived, statistics.BytesSent, Stopwatch.GetTimestamp());
                return new WidgetNetworkSnapshot(DateTimeOffset.Now)
                {
                    IsConnected = true,
                    InterfaceId = adapter.Id,
                    InterfaceName = adapter.Name,
                    NetworkType = NetworkType(adapter),
                    IPv4 = LocalAddress(properties, AddressFamily.InterNetwork),
                    IPv6 = LocalAddress(properties, AddressFamily.InterNetworkV6),
                    Gateway = gateway,
                    LinkBitsPerSecond = Math.Max(0, adapter.Speed),
                    DownloadBytesPerSecond = download,
                    UploadBytesPerSecond = upload,
                    LatencyTarget = target,
                    LatencyMilliseconds = _latency,
                    LatencyStatus = _latencyStatus
                };
            }
            catch (NetworkInformationException) { return Disconnected(adapterId, adapter); }
        }
    }

    public async Task UpdateLatencyAsync(string adapterId, string latencyTarget, CancellationToken cancellationToken)
    {
        CancellationTokenSource request;
        string target;
        long generation;
        lock (_gate)
        {
            if (_disposed || cancellationToken.IsCancellationRequested) return;
            var adapter = SelectAdapter(adapterId);
            try
            {
                if (adapter is null || adapter.OperationalStatus != OperationalStatus.Up) return;
                target = ResolveTarget(latencyTarget, Gateway(adapter.GetIPProperties()));
                SetLatencyKey(adapter.Id, target);
            }
            catch (NetworkInformationException) { return; }
            if (_latencyRunning) return;
            if (target.Length == 0)
            {
                _latencyStatus = "无可用网关";
                return;
            }
            request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            request.CancelAfter(TimeSpan.FromMilliseconds(800));
            generation = _latencyGeneration;
            _latencyRunning = true;
            _latency = null;
            _latencyStatus = "测量中";
        }

        long? latency = null;
        var status = "测量失败";
        using (request)
        {
            try
            {
                // Bound DNS resolution as well as the echo request.
                IPAddress? address;
                if (IPAddress.TryParse(target, out var literal)) address = literal;
                else
                {
                    var addresses = await Dns.GetHostAddressesAsync(target, request.Token).ConfigureAwait(false);
                    address = addresses.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
                }
                if (address is not null)
                {
                    using var ping = new Ping();
                    var reply = await ping.SendPingAsync(address, TimeSpan.FromMilliseconds(800),
                        cancellationToken: request.Token).ConfigureAwait(false);
                    if (reply.Status == IPStatus.Success) { latency = reply.RoundtripTime; status = "正常"; }
                    else status = reply.Status == IPStatus.TimedOut ? "超时" : "不可达";
                }
            }
            catch (OperationCanceledException) { status = cancellationToken.IsCancellationRequested ? "等待测量" : "超时"; }
            catch (PingException) { status = "测量失败"; }
            catch (SocketException) { status = "无法解析目标"; }
            catch (ArgumentException) { status = "目标无效"; }
        }
        lock (_gate)
        {
            if (_disposed || generation != _latencyGeneration) return;
            _latencyRunning = false;
            _latency = latency;
            _latencyStatus = status;
        }
    }

    /// <summary>Explicit user action only. Uses the system's HTTP route/proxy.</summary>
    public static async Task<string> QueryPublicIpAsync(CancellationToken cancellationToken)
    {
        using var response = await PublicIpClient.GetAsync("https://api64.ipify.org?format=json", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        if (!json.RootElement.TryGetProperty("ip", out var value) || value.ValueKind != JsonValueKind.String ||
            !IPAddress.TryParse(value.GetString(), out var address))
            throw new InvalidDataException("公网 IP 服务返回了无效地址。");
        return address.ToString();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime.Cancel();
            _lifetime.Dispose();
        }
    }

    private WidgetNetworkSnapshot Disconnected(string adapterId, NetworkInterface? adapter)
    {
        _rates.Reset();
        SetLatencyKey("", "");
        return new WidgetNetworkSnapshot(DateTimeOffset.Now)
        {
            InterfaceId = adapter?.Id ?? adapterId ?? "",
            InterfaceName = adapter?.Name ?? "",
            LatencyStatus = "未连接"
        };
    }

    private void SetLatencyKey(string adapterId, string target)
    {
        var key = adapterId + "\0" + target;
        if (_latencyKey == key) return;
        _latencyKey = key;
        _latencyGeneration++;
        _latencyRunning = false;
        _latency = null;
        _latencyStatus = "等待测量";
    }

    private static NetworkInterface[] ReadAdapters()
    {
        try { return NetworkInterface.GetAllNetworkInterfaces().Where(adapter => adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback).ToArray(); }
        catch (NetworkInformationException) { return []; }
    }

    private static NetworkInterface? SelectAdapter(string adapterId)
    {
        var adapters = ReadAdapters();
        if (!string.IsNullOrWhiteSpace(adapterId))
            return adapters.FirstOrDefault(adapter => string.Equals(adapter.Id, adapterId, StringComparison.OrdinalIgnoreCase));
        var active = adapters.Where(adapter => adapter.OperationalStatus == OperationalStatus.Up).ToArray();
        try
        {
            // 1.1.1.1 is only a route-table lookup: no packet is sent here.
            if (GetBestInterface(0x01010101, out var index) == 0)
            {
                var best = active.FirstOrDefault(adapter => adapter.GetIPProperties().GetIPv4Properties()?.Index == index);
                if (best is not null) return best;
            }
        }
        catch (NetworkInformationException) { }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        return active.OrderByDescending(HasGateway)
            .ThenByDescending(adapter => adapter.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
            .ThenBy(adapter => adapter.Id, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
    }

    private static bool HasGateway(NetworkInterface adapter)
    {
        try { return Gateway(adapter.GetIPProperties()).Length > 0; }
        catch (NetworkInformationException) { return false; }
    }

    private static string ResolveTarget(string target, string gateway) => string.IsNullOrWhiteSpace(target) ? gateway : target.Trim();

    private static string Gateway(IPInterfaceProperties properties) => properties.GatewayAddresses.Select(item => item.Address)
        .Where(IsUsable).OrderBy(ip => ip.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).FirstOrDefault()?.ToString() ?? "";

    private static string LocalAddress(IPInterfaceProperties properties, AddressFamily family) => properties.UnicastAddresses
        .Where(item => item.Address.AddressFamily == family && IsUsable(item.Address) &&
            item.DuplicateAddressDetectionState is not (DuplicateAddressDetectionState.Duplicate or DuplicateAddressDetectionState.Tentative or DuplicateAddressDetectionState.Invalid))
        .OrderBy(item => IsLinkLocal(item.Address) ? 1 : 0)
        .ThenBy(item => item.DuplicateAddressDetectionState == DuplicateAddressDetectionState.Deprecated ? 1 : 0)
        .Select(item => item.Address.ToString()).FirstOrDefault() ?? "";

    private static bool IsUsable(IPAddress ip) => !IPAddress.IsLoopback(ip) && !ip.Equals(IPAddress.Any) &&
        !ip.Equals(IPAddress.IPv6Any) && !ip.IsIPv6Multicast &&
        (ip.AddressFamily != AddressFamily.InterNetwork || ip.GetAddressBytes()[0] < 224);

    private static bool IsLinkLocal(IPAddress ip) => ip.IsIPv6LinkLocal ||
        (ip.AddressFamily == AddressFamily.InterNetwork && ip.GetAddressBytes() is var bytes && bytes[0] == 169 && bytes[1] == 254);

    private static string NetworkType(NetworkInterface adapter) => adapter.NetworkInterfaceType switch
    {
        NetworkInterfaceType.Wireless80211 => "Wi-Fi",
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.FastEthernetT => "以太网",
        NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => "移动网络",
        NetworkInterfaceType.Ppp => "PPP / VPN",
        NetworkInterfaceType.Tunnel => "隧道 / VPN",
        _ => adapter.NetworkInterfaceType.ToString()
    };

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetBestInterface(uint destinationAddress, out uint bestInterfaceIndex);

    internal sealed class RateSampler
    {
        private string? _previousId;
        private long _previousTimestamp;
        private long _previousReceived;
        private long _previousSent;

        internal void Reset() => _previousId = null;

        internal (double? Download, double? Upload) Sample(string adapterId, long received, long sent, long timestamp)
        {
            var elapsed = (timestamp - _previousTimestamp) / (double)Stopwatch.Frequency;
            // A resumed/suspended widget must establish a fresh baseline.
            var valid = _previousId == adapterId && elapsed > 0 && elapsed <= 10 &&
                received >= _previousReceived && sent >= _previousSent;
            double? download = valid ? WidgetMonitorMetrics.Rate(_previousReceived, received, elapsed) : null;
            double? upload = valid ? WidgetMonitorMetrics.Rate(_previousSent, sent, elapsed) : null;
            _previousId = adapterId;
            _previousTimestamp = timestamp;
            _previousReceived = received;
            _previousSent = sent;
            return (download, upload);
        }
    }
}
