namespace CrabDesk.Core;

public static class WidgetMonitorPresentation
{
    public readonly record struct Metric(string Label, string Value, string Detail, double? Percentage);
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
        return [new("CPU", WidgetMonitorMetrics.Percent(snapshot?.CpuPercent), "系统处理器", snapshot?.CpuPercent),
            new("GPU", WidgetMonitorMetrics.Percent(snapshot?.GpuPercent), snapshot?.GpuPercent is null ? "等待或不可用" : "最繁忙的图形引擎", snapshot?.GpuPercent),
            new("内存", WidgetMonitorMetrics.Percent(memory), snapshot is { MemoryTotalBytes: > 0 }
                ? $"{WidgetMonitorMetrics.Bytes(snapshot.MemoryUsedBytes)} / {WidgetMonitorMetrics.Bytes(snapshot.MemoryTotalBytes)}" : "等待采样", memory),
            new("磁盘", WidgetMonitorMetrics.Percent(snapshot?.DiskActivePercent),
                $"读 {WidgetMonitorMetrics.Speed(snapshot?.DiskReadBytesPerSecond)} · 写 {WidgetMonitorMetrics.Speed(snapshot?.DiskWriteBytesPerSecond)}", snapshot?.DiskActivePercent)];
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
