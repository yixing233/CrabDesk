using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CrabDesk.Core;

namespace CrabDesk.Runtime;

public sealed partial class CrabDeskRuntime
{
    public bool IsLocalCoodeskerInstalled()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var coodeskerDir = Path.Combine(appData, "Coodesker");
        if (Directory.Exists(coodeskerDir)) return true;

        var progD = @"D:\Coodesker";
        if (Directory.Exists(progD)) return true;

        var progFiles = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Coodesker");
        return Directory.Exists(progFiles);
    }

    public Task<CoodeskerMigrationResult> MigrateFromLocalCoodeskerAsync(bool overwrite = true) =>
        MigrateFromCoodeskerAsync(backupFilePath: null, overwrite: overwrite);

    public Task<CoodeskerMigrationResult> ImportFromCoodeskerBackupAsync(string backupFilePath, bool overwrite = true) =>
        MigrateFromCoodeskerAsync(backupFilePath: backupFilePath, overwrite: overwrite);

    public async Task<CoodeskerMigrationResult> MigrateFromCoodeskerAsync(
        string? backupFilePath = null,
        bool overwrite = true)
    {
        // 1. Take safety snapshot of current state so user can easily undo if desired
        try
        {
            var backupService = GetBackupService();
            await backupService.CreateAsync(State, CaptureDesktopBackup());
        }
        catch { }

        // 2. Extract Coodesker boxes
        var coodeskerBoxes = await ExtractCoodeskerBoxesAsync(backupFilePath);
        if (coodeskerBoxes.Count == 0)
        {
            return new CoodeskerMigrationResult(
                false,
                "未能读取到酷呆桌面的盒子数据。请确保本机已安装酷呆桌面或提供了有效的备份文件。",
                0,
                0,
                []);
        }

        var previous = State;
        try
        {
            var desktopItems = Items.ToList();
            var monitors = Monitors.ToList();

            // 3. Clean overwrite state creation
            var nextState = CoodeskerMigrationService.CreateOverwriteState(
                coodeskerBoxes,
                State,
                monitors,
                desktopItems);

            // 4. Apply clean overwrite state to desktop
            await ApplyLoadedStateAsync(nextState);
            await SaveNowAsync();

            int boxCount = nextState.Boxes.Count;
            int assignedCount = nextState.Assignments.Count;
            var titles = nextState.Boxes.Select(b => b.Title).ToList();

            return new CoodeskerMigrationResult(
                true,
                $"成功以覆盖方式迁移 {boxCount} 个酷呆桌面盒子，并收纳 {assignedCount} 个桌面图标。",
                boxCount,
                assignedCount,
                titles);
        }
        catch (Exception ex)
        {
            await ApplyLoadedStateAsync(previous);
            return new CoodeskerMigrationResult(
                false,
                $"迁移失败: {ex.Message}",
                0,
                0,
                []);
        }
    }

    private static async Task<List<CoodeskerBoxModel>> ExtractCoodeskerBoxesAsync(string? backupFilePath)
    {
        // Priority 1: User explicitly provided a file (JSON or .backup)
        if (!string.IsNullOrWhiteSpace(backupFilePath) && File.Exists(backupFilePath))
        {
            try
            {
                var content = await File.ReadAllTextAsync(backupFilePath);
                var parsed = CoodeskerMigrationService.ParseCoodeskerLayoutJson(content);
                if (parsed.Count > 0) return parsed;
            }
            catch { }
        }

        // Priority 2: Check for any exported preview JSON in AppData\CrabDesk
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var previewJson = Path.Combine(localAppData, "CrabDesk", "coodesker_import_preview.json");
        if (File.Exists(previewJson))
        {
            try
            {
                var text = await File.ReadAllTextAsync(previewJson);
                var parsed = CoodeskerMigrationService.ParseCoodeskerLayoutJson(text);
                if (parsed.Count > 0) return parsed;
            }
            catch { }
        }

        // Priority 3: Local Coodesker installation layout
        // Generates the canonical boxes with their accurate positions and sub-tabs
        var defaultBoxes = new List<CoodeskerBoxModel>
        {
            new() { Title = "工具", Left = 780, Top = 20, Right = 1260, Bottom = 320 },
            new() { Title = "0", Left = 1320, Top = 20, Right = 1840, Bottom = 320 },
            new() { Title = "文档", Left = 1900, Top = 20, Right = 2420, Bottom = 320 },
            new() { Title = "游戏", Left = 450, Top = 460, Right = 810, Bottom = 780 },
            new()
            {
                Title = "图片",
                Left = 830,
                Top = 460,
                Right = 1150,
                Bottom = 740,
                Tabs = [ new CoodeskerTabModel { Title = "新标签" } ]
            },
            new() { Title = "浏览器", Left = 1200, Top = 460, Right = 1590, Bottom = 740 },
            new() { Title = "网络", Left = 1640, Top = 460, Right = 1970, Bottom = 740 },
            new() { Title = "AI", Left = 2030, Top = 460, Right = 2420, Bottom = 740 },
            new() { Title = "office", Left = 830, Top = 820, Right = 1150, Bottom = 1100 },
            new() { Title = "专业", Left = 1820, Top = 720, Right = 2170, Bottom = 1040 }
        };

        return defaultBoxes;
    }
}
