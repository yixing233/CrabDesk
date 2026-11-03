namespace CrabDesk.Core;

public static class WidgetMonitorPresentation
{
    /// <summary>
    /// One cell of the performance grid. Temperature is carried separately
    /// rather than appended to Detail: the detail line is already filled by the
    /// model name, and a longer string there is simply clipped away.
    /// </summary>
    public readonly record struct Metric(string Label, string Value, string Detail, double? Percentage,
        double? TemperatureCelsius = null);
    public readonly record struct NetworkLayout(LayoutRect Download, LayoutRect Upload, LayoutRect Connection,
        LayoutRect Latency, LayoutRect IPv4, LayoutRect IPv6, LayoutRect PublicIp);

    public static string ConnectionText(WidgetNetworkSnapshot? snapshot) => snapshot is null ? "正在采样…" :
        !snapshot.IsConnected ? "未连接网络" : string.IsNullOrEmpty(snapshot.InterfaceName) || snapshot.InterfaceName == snapshot.NetworkType
            ? snapshot.NetworkType : snapshot.NetworkType + " · " + snapshot.InterfaceName;

    public static LayoutRect Body(double width, double height, double cornerRadius)
    {
        var inset = 12 + Math.Clamp(cornerRadius, 0, 20) * .2;
        return new(inset, 36, width - inset * 2, height - 36 - inset);
    }

    public static LayoutRect PerformanceCell(LayoutRect body, int index)
    {
        if (body.Width < 300) return new(body.X, body.Y + index * body.Height / 4, body.Width, body.Height / 4);
        var width = (body.Width - 16) / 2; var height = (body.Height - 12) / 2;
        return new(body.X + index % 2 * (width + 16), body.Y + index / 2 * (height + 12), width, height);
    }

    public static Metric[] PerformanceMetrics(WidgetPerformanceSnapshot? snapshot)
    {
        var memory = snapshot is { MemoryTotalBytes: > 0 } ? snapshot.MemoryUsedBytes * 100d / snapshot.MemoryTotalBytes : (double?)null;
        var hardware = snapshot?.Hardware ?? WidgetHardwareNames.Empty;
        // The model name replaces the generic description once it is known: the
        // row already says "CPU", so "12th Gen Intel Core i7-12700H" is what the
        // user cannot get anywhere else on this card. Memory and disk keep their
        // measurements, which are more useful there than a part number.
        return [new("CPU", WidgetMonitorMetrics.Percent(snapshot?.CpuPercent), Detail(hardware.Cpu, "系统处理器"),
                snapshot?.CpuPercent, snapshot?.CpuTemperatureCelsius),
            new("GPU", WidgetMonitorMetrics.Percent(snapshot?.GpuPercent),
                Detail(hardware.Gpu, snapshot?.GpuPercent is null ? "等待或不可用" : "最繁忙的图形引擎"),
                snapshot?.GpuPercent, snapshot?.GpuTemperatureCelsius),
            new("内存", WidgetMonitorMetrics.Percent(memory), snapshot is { MemoryTotalBytes: > 0 }
                ? $"{WidgetMonitorMetrics.Bytes(snapshot.MemoryUsedBytes)} / {WidgetMonitorMetrics.Bytes(snapshot.MemoryTotalBytes)}" : "等待采样", memory),
            new("磁盘", WidgetMonitorMetrics.Percent(snapshot?.DiskActivePercent),
                $"读 {WidgetMonitorMetrics.Speed(snapshot?.DiskReadBytesPerSecond)} · 写 {WidgetMonitorMetrics.Speed(snapshot?.DiskWriteBytesPerSecond)}", snapshot?.DiskActivePercent)];
    }

    /// <summary>
    /// Appends a temperature to a detail line, or returns it untouched when the
    /// sensor is missing. Used where the detail line has room; the performance
    /// grid draws it beside the value instead.
    /// </summary>
    private static string WithTemperature(string detail, double? celsius) =>
        celsius is { } value && double.IsFinite(value) && value > -50 && value < 200
            ? $"{detail} · {value:0.#} °C"
            : detail;

    private static string Detail(string hardware, string fallback)
    {
        var label = WidgetMonitorMetrics.HardwareLabel(hardware);
        return label.Length == 0 ? fallback : label;
    }

    /// <summary>
    /// The hardware model lines the context menu lists under 硬件信息. Blank
    /// entries are left out so the menu never shows an empty value.
    /// </summary>
    public static string[] HardwareDetailLines(WidgetPerformanceSnapshot? snapshot)
    {
        var hardware = snapshot?.Hardware ?? WidgetHardwareNames.Empty;
        var lines = new List<string>(5);
        foreach (var (label, value) in new[]
                 {
                     ("处理器", hardware.Cpu), ("显卡", hardware.Gpu),
                     ("内存", hardware.Memory), ("磁盘", hardware.Disk)
                 })
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                lines.Add($"{label}：{value.Trim()}");
            }
        }
        if (snapshot?.CpuTemperatureCelsius is { } cpuTemp)
        {
            lines.Add($"CPU 温度：{WidgetMonitorMetrics.Degrees(cpuTemp)}");
        }
        if (snapshot?.GpuTemperatureCelsius is { } gpuTemp)
        {
            lines.Add($"GPU 温度：{WidgetMonitorMetrics.Degrees(gpuTemp)}");
        }
        return lines.ToArray();
    }

    public static NetworkLayout NetworkFields(LayoutRect body)
    {
        var width = (body.Width - 12) / 2;
        return new(new(body.X, body.Y, width, 40), new(body.X + width + 12, body.Y, width, 40),
            new(body.X, body.Y + 44, body.Width - 104, 18), new(body.X + body.Width - 104, body.Y + 44, 104, 18),
            new(body.X, body.Y + 66, body.Width, 18), new(body.X, body.Y + 86, body.Width, 18),
            new(body.X, body.Y + 106, body.Width, 22));
    }
}
