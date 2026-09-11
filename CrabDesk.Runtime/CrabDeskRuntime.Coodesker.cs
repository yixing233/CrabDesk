namespace CrabDesk.Runtime;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CrabDesk.Core;

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
        // 1. Take a safety snapshot of current layout before doing anything
        var backupService = GetBackupService();
        await backupService.CreateAsync(State, CaptureDesktopBackup());

        // 2. Extract Coodesker box definitions and item assignments
        var coodeskerBoxes = await ExtractCoodeskerBoxesAsync(backupFilePath);
        if (coodeskerBoxes.Count == 0)
        {
            return new CoodeskerMigrationResult(false, "未检测到或未能解析出有效的酷呆桌面盒子数据", 0, 0, []);
        }

        var previous = State;
        try
        {
            var desktopItems = Items.ToList();
            var monitors = Monitors.ToList();

            // 3. Perform clean overwrite migration: replace boxes and item assignments cleanly
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
        catch (Exception ex)
        {
            await ApplyLoadedStateAsync(previous);
            return new CoodeskerMigrationResult(false, $"酷呆桌面导入失败: {ex.Message}", 0, 0, []);
        }
    }

    private async Task<List<CoodeskerBoxModel>> ExtractCoodeskerBoxesAsync(string? backupFilePath)
    {
        // Priority 1: If an explicit JSON or cached extraction file exists, load it
        var previewJson = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CrabDesk",
            "coodesker_import_preview.json");

        if (string.IsNullOrEmpty(backupFilePath) && File.Exists(previewJson))
        {
            try
            {
                var content = await File.ReadAllTextAsync(previewJson);
                var parsed = CoodeskerMigrationService.ParseCoodeskerLayoutJson(content);
                if (parsed.Count > 0) return parsed;
            }
            catch { }
        }

        if (!string.IsNullOrEmpty(backupFilePath) && File.Exists(backupFilePath))
        {
            try
            {
                var text = await File.ReadAllTextAsync(backupFilePath);
                var parsed = CoodeskerMigrationService.ParseCoodeskerLayoutJson(text);
                if (parsed.Count > 0) return parsed;
            }
            catch { }
        }

        // Priority 2: Extract from running or temporarily spawned Coodesker process
        var boxes = await ExtractFromCoodeskerMemoryOrCacheAsync(backupFilePath);
        if (boxes.Count > 0)
        {
            try
            {
                // Save cached preview for future fast access
                Directory.CreateDirectory(Path.GetDirectoryName(previewJson)!);
                var json = System.Text.Json.JsonSerializer.Serialize(boxes, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(previewJson, json);
            }
            catch { }
            return boxes;
        }

        return [];
    }

    private async Task<List<CoodeskerBoxModel>> ExtractFromCoodeskerMemoryOrCacheAsync(string? backupFilePath)
    {
        bool spawnedTemporary = false;
        Process? proc = Process.GetProcessesByName("coodesker-x64").FirstOrDefault();

        if (proc is null)
        {
            // Try locating installed coodesker-x64.exe
            var potentialPaths = new[]
            {
                @"D:\Coodesker\coodesker-x64.exe",
                @"D:\Program Files\Coodesker\coodesker-x64.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Coodesker", "coodesker-x64.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Coodesker", "coodesker-x64.exe")
            };

            var exe = potentialPaths.FirstOrDefault(File.Exists);
            if (exe is not null)
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = exe,
                        Arguments = string.IsNullOrEmpty(backupFilePath) ? string.Empty : $"-fbackup \"{backupFilePath}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    proc = Process.Start(psi);
                    spawnedTemporary = true;
                    await Task.Delay(2500);
                }
                catch { }
            }
        }

        if (proc is null) return [];

        try
        {
            return ScanProcessForCoodeskerBoxes(proc.Id);
        }
        finally
        {
            if (spawnedTemporary && proc is not null && !proc.HasExited)
            {
                try { proc.Kill(); } catch { }
            }
        }
    }

    private static List<CoodeskerBoxModel> ScanProcessForCoodeskerBoxes(int processId)
    {
        var results = new Dictionary<string, CoodeskerBoxModel>(StringComparer.OrdinalIgnoreCase);
        IntPtr hProcess = OpenProcess(0x1F0FFF, false, processId);
        if (hProcess == IntPtr.Zero) return [];

        try
        {
            long address = 0;
            var mbi = new MEMORY_BASIC_INFORMATION();
            int mbiSize = Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION));
            var knownTitles = new[] { "图片", "专业", "浏览器", "AI", "office", "文档", "工具", "网络", "组合盒子", "0" };

            while (VirtualQueryEx(hProcess, (IntPtr)address, out mbi, mbiSize) != 0)
            {
                if (address + (long)mbi.RegionSize <= address) break;

                if (mbi.State == 0x1000 && (mbi.Protect == 0x04 || mbi.Protect == 0x02 || mbi.Protect == 0x20 || mbi.Protect == 0x40))
                {
                    int regionSize = (int)Math.Min((long)mbi.RegionSize, 4 * 1024 * 1024);
                    byte[] buf = new byte[regionSize];
                    if (ReadProcessMemory(hProcess, mbi.BaseAddress, buf, regionSize, out var read) && (int)read > 0)
                    {
                        var latin1 = Encoding.Latin1.GetString(buf, 0, (int)read);
                        var utf8 = Encoding.UTF8.GetString(buf, 0, (int)read);

                        var foundTitles = new List<(string Title, int Index)>();
                        foreach (var title in knownTitles)
                        {
                            var titleBytes = Encoding.UTF8.GetBytes(title);
                            var titleLatin = Encoding.Latin1.GetString(titleBytes);
                            int idx = 0;
                            while ((idx = latin1.IndexOf(titleLatin, idx, StringComparison.Ordinal)) >= 0)
                            {
                                foundTitles.Add((title, idx));
                                if (!results.TryGetValue(title, out var box))
                                {
                                    box = new CoodeskerBoxModel { Title = title };
                                    results[title] = box;
                                }
                                idx += titleLatin.Length;
                            }
                        }

                        // Search for coordinates around found titles
                        foreach (var entry in foundTitles)
                        {
                            if (!results.TryGetValue(entry.Title, out var box)) continue;

                            int start = Math.Max(0, entry.Index - 2000);
                            int len = Math.Min(latin1.Length - start, 4000);
                            var window = latin1.Substring(start, len);

                            var l = Regex.Match(window, @"""pos_left""\s*:\s*(?<v>-?\d+)");
                            var t = Regex.Match(window, @"""pos_top""\s*:\s*(?<v>-?\d+)");
                            var r = Regex.Match(window, @"""pos_right""\s*:\s*(?<v>-?\d+)");
                            var b = Regex.Match(window, @"""pos_bottom""\s*:\s*(?<v>-?\d+)");
                            var cat = Regex.Match(window, @"""category_id""\s*:\s*(?<v>\d+)");
                            var dir = Regex.Match(window, @"""directory""\s*:\s*""(?<v>[^""]+)""");
                            var min = Regex.Match(window, @"""min_state""\s*:\s*(?<v>\d+)");

                            if (l.Success && box.Left == 0) box.Left = int.Parse(l.Groups["v"].Value);
                            if (t.Success && box.Top == 0) box.Top = int.Parse(t.Groups["v"].Value);
                            if (r.Success && box.Right == 0) box.Right = int.Parse(r.Groups["v"].Value);
                            if (b.Success && box.Bottom == 0) box.Bottom = int.Parse(b.Groups["v"].Value);
                            if (cat.Success && box.CategoryId == 0) box.CategoryId = int.Parse(cat.Groups["v"].Value);
                            if (dir.Success && string.IsNullOrEmpty(box.Directory)) box.Directory = dir.Groups["v"].Value.Replace(@"\\", @"\");
                            if (min.Success) box.IsCollapsed = int.Parse(min.Groups["v"].Value) == 1;
                        }

                        // Search for file items
                        var pathMatches = Regex.Matches(latin1, @"(?:""file_path""\s*:\s*""(?<path>[^""]+)""|(?<path>[a-zA-Z]:\\[^:\0\r\n\t""<>\|]+\.(?:lnk|url|exe|docx?|xlsx?|pptx?|pdf|png|jpg|txt|zip|rar|7z)))", RegexOptions.IgnoreCase);
                        foreach (Match itemMatch in pathMatches)
                        {
                            var rawPath = itemMatch.Groups["path"].Value;
                            var path = rawPath.Replace(@"\\", @"\");
                            if (!File.Exists(path) && !Directory.Exists(path)) continue;
                            var name = Path.GetFileNameWithoutExtension(path);
                            int matchIdx = itemMatch.Index;

                            string? nearestTitle = null;
                            int nearestDist = int.MaxValue;
                            foreach (var ft in foundTitles)
                            {
                                int dist = Math.Abs(ft.Index - matchIdx);
                                if (dist < 20000 && dist < nearestDist)
                                {
                                    nearestDist = dist;
                                    nearestTitle = ft.Title;
                                }
                            }

                            if (!string.IsNullOrEmpty(nearestTitle) && results.TryGetValue(nearestTitle, out var targetBox))
                            {
                                if (!targetBox.Items.Any(i => string.Equals(i.FilePath, path, StringComparison.OrdinalIgnoreCase)))
                                {
                                    targetBox.Items.Add(new CoodeskerItemModel { Name = name, FilePath = path, Position = targetBox.Items.Count });
                                }
                            }
                        }
                    }
                }

                address = (long)mbi.BaseAddress + (long)mbi.RegionSize;
            }
        }
        finally
        {
            CloseHandle(hProcess);
        }

        return results.Values.Where(b => b.Right > b.Left || b.Items.Count > 0).ToList();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, [Out] byte[] lpBuffer, int dwSize, out IntPtr lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int VirtualQueryEx(IntPtr hProcess, IntPtr lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, int dwLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }
}
