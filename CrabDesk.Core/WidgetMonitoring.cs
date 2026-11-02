using System.Globalization;

namespace CrabDesk.Core;

public sealed record WidgetPerformanceSnapshot(DateTimeOffset CapturedAt, double? CpuPercent, double? GpuPercent,
    long MemoryUsedBytes, long MemoryTotalBytes, double? DiskActivePercent,
    double? DiskReadBytesPerSecond, double? DiskWriteBytesPerSecond, long? DiskUsedBytes, long? DiskTotalBytes);

public sealed record WidgetNetworkSnapshot(DateTimeOffset CapturedAt)
{
    public bool IsConnected { get; init; }
    public string InterfaceId { get; init; } = "";
    public string InterfaceName { get; init; } = "";
    public string NetworkType { get; init; } = "未连接";
    public string IPv4 { get; init; } = "";
    public string IPv6 { get; init; } = "";
    public string Gateway { get; init; } = "";
    public long LinkBitsPerSecond { get; init; }
    public double? DownloadBytesPerSecond { get; init; }
    public double? UploadBytesPerSecond { get; init; }
    public long? LatencyMilliseconds { get; init; }
    public string LatencyTarget { get; init; } = "";
    public string LatencyStatus { get; init; } = "等待测量";
}

public sealed record WidgetNetworkAdapter(string Id, string Name, string NetworkType);

public static class WidgetMonitorMetrics
{
    public static bool IsValidLatencyTarget(string value) => value.Length == 0 ||
        value.Length <= 253 && (System.Net.IPAddress.TryParse(value, out _) || Uri.CheckHostName(value) == UriHostNameType.Dns);
    public static double? Rate(long previous, long current, double elapsedSeconds) =>
        previous < 0 || current < previous || elapsedSeconds <= 0 || !double.IsFinite(elapsedSeconds)
            ? null : (current - previous) / elapsedSeconds;

    public static string Percent(double? value) => value is { } number && double.IsFinite(number)
        ? $"{Math.Clamp(number, 0, 100):0}%" : "—";

    public static string Bytes(double? value)
    {
        if (value is not { } bytes || !double.IsFinite(bytes) || bytes < 0) return "—";
        var units = new[] { "B", "KB", "MB", "GB", "TB" }; var unit = 0;
        while (bytes >= 1024 && unit < units.Length - 1) { bytes /= 1024; unit++; }
        return bytes.ToString(unit == 0 ? "0" : "0.#", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    public static string Speed(double? value) => value is null ? "—" : Bytes(value) + "/s";

    public static double? GpuUtilization(IEnumerable<(string Engine, double Value)> samples)
    {
        var totals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (engine, value) in samples)
        {
            if (!double.IsFinite(value) || value < 0) continue;
            var start = engine.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
            var key = start >= 0 ? engine[start..] : engine;
            totals[key] = totals.GetValueOrDefault(key) + value;
        }
        return totals.Count == 0 ? null : Math.Clamp(totals.Values.Max(), 0, 100);
    }
}
