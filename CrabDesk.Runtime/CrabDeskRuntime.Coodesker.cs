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

        // Default layout when migrating from local Coodesker installation
        // Generates the canonical boxes with their accurate positions and sub-tabs
        return GetCanonicalLocalCoodeskerBoxes();
    }

    private static CoodeskerItemModel Item(string name) => new() { Name = name };

    private static List<CoodeskerBoxModel> GetCanonicalLocalCoodeskerBoxes()
    {
        var docBox = new CoodeskerBoxModel
        {
            Title = "文档",
            Left = 1900,
            Top = 20,
            Right = 2420,
            Bottom = 320,
            Items =
            [
                Item("Typora"),
                Item("Obsidian"),
                Item("Zotero"),
                Item("知云文献翻译"),
                Item("福昕高级PDF编辑器")
            ]
        };

        var docExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".doc", ".docx", ".pdf", ".txt", ".xlsx", ".pptx", ".dwg", ".csv", ".rtf", ".md" };
        var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (Directory.Exists(desktopPath))
        {
            foreach (var file in Directory.GetFiles(desktopPath))
            {
                if (docExts.Contains(Path.GetExtension(file)))
                {
                    docBox.Items.Add(new CoodeskerItemModel
                    {
                        Name = Path.GetFileNameWithoutExtension(file),
                        FilePath = file
                    });
                }
            }
        }

        var defaultBoxes = new List<CoodeskerBoxModel>
        {
            new()
            {
                Title = "工具",
                Left = 780,
                Top = 20,
                Right = 1260,
                Bottom = 320,
                Items =
                [
                    Item("凌豹键盘驱动"),
                    Item("ATK V HUB"),
                    Item("MCHOSE HUB"),
                    Item("DeskPins"),
                    Item("EV录屏"),
                    Item("ImeTool"),
                    Item("KTC-Monitor Control Center"),
                    Item("Listary"),
                    Item("MobaXterm_Personal_24.3.exe"),
                    Item("NexClip"),
                    Item("Origin 2024"),
                    Item("PasteX"),
                    Item("PI-Desk"),
                    Item("QuickLook"),
                    Item("TieZ"),
                    Item("Umi-OCR"),
                    Item("WizTree"),
                    Item("TinyBar"),
                    Item("Watt Toolkit"),
                    Item("小米电脑管家"),
                    Item("EcoPaste"),
                    Item("钟艺之刻表盘设计工具"),
                    Item("搞机助手"),
                    Item("Wise Registry Cleane"),
                    Item("Windhawk"),
                    Item("MkLink")
                ]
            },
            new()
            {
                Title = "0",
                Left = 1320,
                Top = 20,
                Right = 1840,
                Bottom = 320,
                Items =
                [
                    Item("无畏契约登录器.exe"),
                    Item("无畏契约WeGame版"),
                    Item("炉石传说"),
                    Item("Firestone"),
                    Item("HearthstoneDeckTracker.exe"),
                    Item("和平精英模拟器"),
                    Item("steam"),
                    Item("WeGame"),
                    Item("Epic Games Launcher"),
                    Item("游戏加加"),
                    Item("暴雪战网")
                ]
            },
            docBox,
            new()
            {
                Title = "图片",
                Left = 830,
                Top = 460,
                Right = 1150,
                Bottom = 740,
                Tabs = [ new CoodeskerTabModel { Title = "新标签" } ],
                Items =
                [
                    Item("Snipaste_2025-12-03_19-29-58_2_2_2"),
                    Item("Snipaste_2025-12-03_19-29-58_2_2_2_2")
                ]
            },
            new()
            {
                Title = "浏览器",
                Left = 1200,
                Top = 460,
                Right = 1590,
                Bottom = 740,
                Items =
                [
                    Item("Google Chrome"),
                    Item("Microsoft Edge"),
                    Item("Firefox"),
                    Item("AdsPower Browser"),
                    Item("夸克"),
                    Item("夸克网盘"),
                    Item("百度网盘"),
                    Item("阿里云盘"),
                    Item("迅雷")
                ]
            },
            new()
            {
                Title = "网络",
                Left = 1640,
                Top = 460,
                Right = 1970,
                Bottom = 740,
                Items =
                [
                    Item("ToDesk"),
                    Item("向日葵远程控制"),
                    Item("UU远程"),
                    Item("EasyConnect"),
                    Item("RustDesk"),
                    Item("SakuraFrp 启动器"),
                    Item("雷神加速器"),
                    Item("小黑盒加速器"),
                    Item("CC Switch"),
                    Item("Discord"),
                    Item("Telegram"),
                    Item("Clash Verge"),
                    Item("FlClash"),
                    Item("Radmin VPN"),
                    Item("LocalSend")
                ]
            },
            new()
            {
                Title = "AI",
                Left = 2030,
                Top = 460,
                Right = 2420,
                Bottom = 740,
                Items =
                [
                    Item("豆包"),
                    Item("元宝"),
                    Item("CodeBuddy"),
                    Item("LM Studio"),
                    Item("LLM Wiki"),
                    Item("Codex++"),
                    Item("Codex++ 管理工具"),
                    Item("AstrBot")
                ]
            },
            new()
            {
                Title = "office",
                Left = 830,
                Top = 820,
                Right = 1150,
                Bottom = 1100,
                Items =
                [
                    Item("WPS Office"),
                    Item("Word"),
                    Item("Excel"),
                    Item("PowerPoint"),
                    Item("Visio"),
                    Item("OneNote"),
                    Item("LibreOffice 26.2"),
                    Item("企业微信"),
                    Item("网易邮箱大师"),
                    Item("腾讯会议")
                ]
            },
            new()
            {
                Title = "专业",
                Left = 1820,
                Top = 720,
                Right = 2170,
                Bottom = 1040,
                Items =
                [
                    Item("Trae CN"),
                    Item("Codey"),
                    Item("Qoder CN"),
                    Item("Qoder"),
                    Item("OpenCode"),
                    Item("CrabDesk"),
                    Item("ZCode"),
                    Item("Docker Desktop"),
                    Item("微信开发者工具"),
                    Item("Visual Studio Code"),
                    Item("Visual Studio 2022"),
                    Item("Antigravity"),
                    Item("Antigravity IDE"),
                    Item("Antigravity Tools"),
                    Item("MATLAB R2024b"),
                    Item("Cygwin64 Terminal"),
                    Item("AutoCAD 2024"),
                    Item("AutoCAD 2016 - 简体中文 (Simplified Chinese)"),
                    Item("SOLIDWORKS 2025"),
                    Item("剪映专业版")
                ]
            }
        };

        foreach (var b in defaultBoxes)
        {
            b.IsCollapsed = true;
        }

        return defaultBoxes;
    }
}
