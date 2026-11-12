namespace CrabDesk.Core;

public enum NetworkProcessStatus { WarmingUp, Ready, PermissionRequired, Unavailable }

public sealed record WidgetNetworkProcess(int ProcessId, string Name, double DownloadBytesPerSecond, double UploadBytesPerSecond)
{
    public double Rate(NetworkProcessSort direction) =>
        direction == NetworkProcessSort.Upload ? UploadBytesPerSecond : DownloadBytesPerSecond;
}

public sealed record WidgetNetworkProcesses(NetworkProcessStatus Status, IReadOnlyList<WidgetNetworkProcess> Processes)
{
    public static WidgetNetworkProcesses Waiting { get; } = new(NetworkProcessStatus.WarmingUp, []);

    public IReadOnlyList<WidgetNetworkProcess> Top(NetworkProcessSort direction, int count = 3) =>
        Processes.Where(process => double.IsFinite(process.Rate(direction)) && process.Rate(direction) > 0)
            .OrderByDescending(process => process.Rate(direction))
            .ThenBy(process => process.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(process => process.ProcessId).Take(Math.Clamp(count, 0, 100)).ToArray();

    public string Message(NetworkProcessSort direction) => Status switch
    {
        NetworkProcessStatus.PermissionRequired => "进程流量需要管理员权限",
        NetworkProcessStatus.Unavailable => "暂时无法读取进程流量",
        NetworkProcessStatus.WarmingUp => "正在采集进程流量…",
        _ => direction == NetworkProcessSort.Upload ? "当前没有进程在上传" : "当前没有进程在下载"
    };
}
