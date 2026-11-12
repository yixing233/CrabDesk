using System.ComponentModel;
using System.Diagnostics;
using System.Management;
using System.Text;
using CrabDesk.Core;

namespace CrabDesk.Native;

public interface INetworkAdapterControl
{
    IReadOnlyList<ControlNetworkAdapter> Read();
    Task<ControlOperationResult> SetEnabledAsync(Guid id, bool enabled);
}

public sealed class NetworkAdapterControl : INetworkAdapterControl
{
    public IReadOnlyList<ControlNetworkAdapter> Read()
    {
        using var searcher = new ManagementObjectSearcher(@"root\StandardCimv2",
            "SELECT Name, InterfaceDescription, InterfaceGuid, InterfaceAdminStatus, InterfaceOperationalStatus, InterfaceType " +
            "FROM MSFT_NetAdapter WHERE Hidden = FALSE");
        using var results = searcher.Get();
        var adapters = new List<ControlNetworkAdapter>();
        foreach (ManagementObject item in results)
        {
            using (item)
            {
                if (!Guid.TryParse(item["InterfaceGuid"]?.ToString(), out var id)) continue;
                var name = item["Name"]?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(name)) continue;
                var enabled = Convert.ToUInt32(item["InterfaceAdminStatus"]) switch { 1 => true, 2 => false, _ => (bool?)null };
                adapters.Add(new(id, name, item["InterfaceDescription"]?.ToString() ?? "", enabled,
                    Convert.ToUInt32(item["InterfaceOperationalStatus"]) == 1, Convert.ToUInt32(item["InterfaceType"]) == 71));
            }
        }
        return adapters.OrderBy(adapter => adapter.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public async Task<ControlOperationResult> SetEnabledAsync(Guid id, bool enabled)
    {
        if (id == Guid.Empty) return new(false, "网卡标识无效。");
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                    @"WindowsPowerShell\v1.0\powershell.exe"),
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = "-NoLogo -NoProfile -NonInteractive -EncodedCommand " +
                    Convert.ToBase64String(Encoding.Unicode.GetBytes(Command(id, enabled)))
            };
            using var process = Process.Start(start);
            if (process is null) return new(false, "无法启动网卡控制。");
            await process.WaitForExitAsync().ConfigureAwait(false);
            return process.ExitCode == 0 ? ControlOperationResult.Success :
                new(false, "无法更改网卡状态，请检查设备或管理员权限。");
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        { return new(false, "已取消网卡操作。"); }
        catch (Exception exception)
        { return new(false, "网卡操作失败：" + exception.Message); }
    }

    internal static string Command(Guid id, bool enabled) => $$"""
        $ErrorActionPreference = 'Stop'
        try {
            $adapters = @(Get-NetAdapter -IncludeHidden | Where-Object { ([guid]$_.InterfaceGuid) -eq [guid]'{{id:D}}' })
            if ($adapters.Count -ne 1) { exit 2 }
            $adapters[0] | {{(enabled ? "Enable-NetAdapter" : "Disable-NetAdapter")}} -Confirm:$false -ErrorAction Stop
            exit 0
        } catch { exit 1 }
        """;
}
