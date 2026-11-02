using System.Globalization;

namespace CrabDesk.Core;

public sealed record WidgetPerformanceSnapshot(DateTimeOffset CapturedAt, double? CpuPercent, double? GpuPercent,
    long MemoryUsedBytes, long MemoryTotalBytes, double? DiskActivePercent,
    double? DiskReadBytesPerSecond, double? DiskWriteBytesPerSecond, long? DiskUsedBytes, long? DiskTotalBytes)
{
    /// <summary>
    /// Model names of the sampled hardware. Empty until the first enumeration
    /// finishes; the names never change while the machine is running, so they
    /// are read once and carried on every snapshot rather than re-queried.
    /// </summary>
    public WidgetHardwareNames Hardware { get; init; } = WidgetHardwareNames.Empty;
}

/// <summary>
/// Human-readable model names for the four metrics the performance card shows.
/// A blank field means the name could not be determined and the card falls back
/// to describing the metric instead.
/// </summary>
public sealed record WidgetHardwareNames(string Cpu, string Gpu, string Memory, string Disk)
{
    public static WidgetHardwareNames Empty { get; } = new("", "", "", "");
}

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
    /// <summary>
    /// Shortens a hardware model name to what fits beside a metric. Vendor
    /// prefixes and the repeated "CPU"/"GPU" tokens only restate the row they
    /// sit on, so they are dropped before the length cap is applied.
    /// </summary>
    public static string HardwareLabel(string value, int maximumLength = 34)
    {
        var text = (value ?? "").Trim();
        if (text.Length == 0) return "";
        foreach (var prefix in new[] { "(R)", "(TM)", "(tm)", "(r)" })
        {
            text = text.Replace(prefix, "");
        }
        foreach (var noise in new[] { "CPU ", "GPU ", "Processor ", "Graphics " })
        {
            if (text.StartsWith(noise, StringComparison.OrdinalIgnoreCase) && text.Length > noise.Length)
            {
                text = text[noise.Length..];
            }
        }
        text = text.Replace("  ", " ").Trim();
        return text.Length <= maximumLength ? text : text[..Math.Max(1, maximumLength - 1)].TrimEnd() + "…";
    }

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

    /// <summary>
    /// Splits a speed into its number and its unit so a caller can give each a
    /// fixed slot. Right-aligning the whole string is not enough: the unit
    /// changes width (B/s, KB/s, MB/s), which slides the number sideways even
    /// though its own right edge held still.
    /// </summary>
    public static (string Number, string Unit) SpeedParts(double? value)
    {
        if (value is not { } bytes || !double.IsFinite(bytes) || bytes < 0) return ("—", "");
        var units = new[] { "B", "KB", "MB", "GB", "TB" }; var unit = 0;
        while (bytes >= 1024 && unit < units.Length - 1) { bytes /= 1024; unit++; }
        return (bytes.ToString(unit == 0 ? "0" : "0.#", CultureInfo.InvariantCulture), units[unit] + "/s");
    }

    /// <summary>
    /// Widest unit a speed row can show, as a fraction of the row width. The
    /// number is right-aligned against this reserve so a unit that grows from
    /// B/s to MB/s never pushes the digits leftwards.
    /// </summary>
    public static string WidestSpeedUnit => "MB/s";

    /// <summary>
    /// Widest number slot a speed row reserves: four digits and the point. The
    /// slot is fixed to this, never to the live value, so the digits a reading
    /// happens to have cannot move it.
    /// </summary>
    public static string WidestSpeedNumber => "999.9";

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
