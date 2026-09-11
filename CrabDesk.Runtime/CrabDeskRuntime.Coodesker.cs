using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using CrabDesk.Core;

namespace CrabDesk.Runtime;

public sealed partial class CrabDeskRuntime
{
    public bool IsLocalCoodeskerInstalled()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var coodeskerDir = Path.Combine(appData, "Coodesker");
        return Directory.Exists(coodeskerDir) || !string.IsNullOrEmpty(FindCoodeskerDll());
    }

    public Task<CoodeskerMigrationResult> MigrateFromLocalCoodeskerAsync(bool overwrite = true) =>
        MigrateFromCoodeskerAsync(backupFilePath: null, overwrite: overwrite);

    public Task<CoodeskerMigrationResult> ImportFromCoodeskerBackupAsync(
        string backupFilePath,
        bool overwrite = true) =>
        MigrateFromCoodeskerAsync(backupFilePath: backupFilePath, overwrite: overwrite);

    private async Task<CoodeskerMigrationResult> MigrateFromCoodeskerAsync(
        string? backupFilePath = null,
        bool overwrite = true)
    {
        // 1. Take safety snapshot of current state
        try
        {
            var backupService = GetBackupService();
            await backupService.CreateAsync(State, CaptureDesktopBackup());
        }
        catch { }

        // 2. Extract Coodesker boxes (100% 1:1 raw from Coodesker data)
        var coodeskerBoxes = await ExtractCoodeskerBoxesAsync(backupFilePath);
        if (coodeskerBoxes.Count == 0)
        {
            return new CoodeskerMigrationResult(
                false,
                "未能从酷呆桌面读取到有效的盒子与图标配置数据。",
                0,
                0,
                []);
        }

        var previous = State;
        try
        {
            var desktopItems = Items.ToList();
            var monitors = Monitors.ToList();

            // 3. 1:1 Clean Overwrite Migration
            var nextState = CoodeskerMigrationService.CreateOverwriteState(
                coodeskerBoxes,
                State,
                monitors,
                desktopItems);

            await ApplyLoadedStateAsync(nextState);
            await SaveNowAsync();

            int assignedItems = nextState.Assignments.Count;
            var titles = nextState.Boxes.Select(b => b.Title).ToList();
            return new CoodeskerMigrationResult(
                true,
                $"成功原样导入 {titles.Count} 个酷呆桌面盒子，收纳 {assignedItems} 个桌面图标。",
                titles.Count,
                assignedItems,
                titles);
        }
        catch (Exception ex)
        {
            await ApplyLoadedStateAsync(previous);
            return new CoodeskerMigrationResult(false, $"迁移失败: {ex.Message}", 0, 0, []);
        }
    }

    private static async Task<List<CoodeskerBoxModel>> ExtractCoodeskerBoxesAsync(string? backupFilePath)
    {
        // Priority 1: User explicitly provided a file
        if (!string.IsNullOrWhiteSpace(backupFilePath) && File.Exists(backupFilePath))
        {
            try
            {
                var text = await File.ReadAllTextAsync(backupFilePath);
                var parsed = CoodeskerMigrationService.ParseCoodeskerLayoutJson(text);
                if (parsed.Count > 0) return parsed;
            }
            catch { }

            var decrypted = DecryptCoodeskerCache(backupFilePath);
            if (!string.IsNullOrEmpty(decrypted))
            {
                var parsed = CoodeskerMigrationService.ParseCoodeskerLayoutJson(decrypted);
                if (parsed.Count > 0) return parsed;
            }
        }

        // Priority 2: From local machine Coodesker cache
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var cachePath = Path.Combine(appData, "Coodesker", "cache", "desk.cache");
        if (File.Exists(cachePath))
        {
            var decrypted = DecryptCoodeskerCache(cachePath);
            if (!string.IsNullOrEmpty(decrypted))
            {
                var parsed = CoodeskerMigrationService.ParseCoodeskerLayoutJson(decrypted);
                if (parsed.Count > 0) return parsed;
            }
        }

        return [];
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LoadLibrary(string path);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr hModule);

    private delegate void FnCipherCtor(IntPtr cipher);
    private delegate void FnCipherSetKey(IntPtr cipher, [MarshalAs(UnmanagedType.LPStr)] string tag, ulong len);
    private delegate void FnCipherSetLen(IntPtr cipher, uint len);
    private delegate void FnCipherDecrypt(IntPtr cipher, IntPtr inBytes, IntPtr outBytes, uint len);

    public static string? DecryptCoodeskerCache(string cachePath, string? dllPath = null)
    {
        if (string.IsNullOrWhiteSpace(dllPath) || !File.Exists(dllPath))
        {
            dllPath = FindCoodeskerDll();
        }
        if (string.IsNullOrWhiteSpace(dllPath) || !File.Exists(dllPath) || !File.Exists(cachePath))
        {
            return null;
        }

        var hModule = LoadLibrary(dllPath);
        if (hModule == IntPtr.Zero) return null;

        try
        {
            var fnCtor = Marshal.GetDelegateForFunctionPointer<FnCipherCtor>(hModule + 0x001AFD20);
            var fnSetKey = Marshal.GetDelegateForFunctionPointer<FnCipherSetKey>(hModule + 0x001B0850);
            var fnSetLen = Marshal.GetDelegateForFunctionPointer<FnCipherSetLen>(hModule + 0x001B0840);
            var fnDecrypt = Marshal.GetDelegateForFunctionPointer<FnCipherDecrypt>(hModule + 0x001B0610);

            var cipher = Marshal.AllocHGlobal(512);
            for (int i = 0; i < 512; i++) Marshal.WriteByte(cipher, i, 0);

            fnCtor(cipher);
            fnSetKey(cipher, "FindWindow", 10);

            var encBytes = File.ReadAllBytes(cachePath);
            fnSetLen(cipher, (uint)encBytes.Length);

            var pIn = Marshal.AllocHGlobal(encBytes.Length);
            var pOut = Marshal.AllocHGlobal(encBytes.Length);
            Marshal.Copy(encBytes, 0, pIn, encBytes.Length);

            fnDecrypt(cipher, pIn, pOut, (uint)encBytes.Length);

            var decBytes = new byte[encBytes.Length];
            Marshal.Copy(pOut, decBytes, 0, encBytes.Length);

            Marshal.FreeHGlobal(pIn);
            Marshal.FreeHGlobal(pOut);
            Marshal.FreeHGlobal(cipher);

            int len = encBytes.Length;
            for (int i = 0; i < decBytes.Length; i++)
            {
                if (decBytes[i] == 0) { len = i; break; }
            }

            return Encoding.UTF8.GetString(decBytes, 0, len);
        }
        catch
        {
            return null;
        }
        finally
        {
            FreeLibrary(hModule);
        }
    }

    private static string? FindCoodeskerDll()
    {
        var candidates = new[]
        {
            Path.Combine(@"D:\Coodesker", "Native-x64.dll"),
            Path.Combine(@"D:\Program Files\Coodesker", "Native-x64.dll"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Coodesker", "Native-x64.dll"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Coodesker", "Native-x64.dll")
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}
