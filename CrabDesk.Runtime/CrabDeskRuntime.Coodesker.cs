using System.Diagnostics;
using System.Text.Json;
using CrabDesk.Core;

namespace CrabDesk.Runtime;

public sealed partial class CrabDeskRuntime
{
    public bool IsLocalCoodeskerInstalled()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var coodeskerDir = Path.Combine(appData, "Coodesker");
        return Directory.Exists(coodeskerDir);
    }

    public async Task<CoodeskerMigrationResult> MigrateFromLocalCoodeskerAsync(bool overwrite = true)
    {
        return await MigrateFromCoodeskerCoreAsync(backupFilePath: null, overwrite: overwrite);
    }

    public async Task<CoodeskerMigrationResult> ImportFromCoodeskerBackupAsync(string backupFilePath, bool overwrite = true)
    {
        if (string.IsNullOrWhiteSpace(backupFilePath) || !File.Exists(backupFilePath))
        {
            return new CoodeskerMigrationResult(false, "指定的酷呆桌面备份文件不存在", 0, 0, []);
        }

        return await MigrateFromCoodeskerCoreAsync(backupFilePath: backupFilePath, overwrite: overwrite);
    }

    private async Task<CoodeskerMigrationResult> MigrateFromCoodeskerCoreAsync(string? backupFilePath, bool overwrite)
    {
        List<CoodeskerBoxModel> coodeskerBoxes;
        try
        {
            coodeskerBoxes = await ExtractCoodeskerBoxesAsync(backupFilePath);
        }
        catch (Exception exception)
        {
            return new CoodeskerMigrationResult(false, $"读取酷呆桌面布局失败：{exception.Message}", 0, 0, []);
        }

        if (coodeskerBoxes.Count == 0)
        {
            return new CoodeskerMigrationResult(
                false,
                "未能识别到结构化的酷呆桌面导出数据。当前版本暂不支持直接解析酷呆专有二进制备份（.backup），请提供 JSON 交换格式的布局导出。",
                0,
                0,
                []);
        }

        // Take a safety snapshot of the existing layout before attempting any overwrite.
        var backupService = GetBackupService();
        await backupService.CreateAsync(State, CaptureDesktopBackup());

        var previous = State;
        try
        {
            var desktopItems = Items.ToList();
            var monitors = Monitors.ToList();

            var nextState = CoodeskerMigrationService.CreateOverwriteState(
                coodeskerBoxes,
                State,
                monitors,
                desktopItems);

            await ApplyLoadedStateAsync(nextState);
            await SaveNowAsync();

            int assignedCount = nextState.Assignments.Count;
            var titles = nextState.Boxes.Select(b => b.Title).ToList();
            var message = $"成功以覆盖方式导入 {titles.Count} 个酷呆桌面盒子，收纳 {assignedCount} 个桌面图标。";
            return new CoodeskerMigrationResult(true, message, titles.Count, assignedCount, titles);
        }
        catch (Exception exception)
        {
            await ApplyLoadedStateAsync(previous);
            return new CoodeskerMigrationResult(false, $"酷呆桌面导入失败：{exception.Message}", 0, 0, []);
        }
    }

    private static async Task<List<CoodeskerBoxModel>> ExtractCoodeskerBoxesAsync(string? backupFilePath)
    {
        if (string.IsNullOrWhiteSpace(backupFilePath))
        {
            // Binary cache reading is not yet implemented safely; refuse to guess from memory fragments.
            return [];
        }

        if (!File.Exists(backupFilePath))
        {
            throw new FileNotFoundException("备份文件不存在。", backupFilePath);
        }

        var content = await File.ReadAllTextAsync(backupFilePath);
        var parsed = CoodeskerMigrationService.ParseCoodeskerLayoutJson(content);
        if (parsed.Count > 0)
        {
            return parsed;
        }

        return [];
    }
}
