using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace CrabDesk.Bootstrapper;

internal static class Program
{
    private const string DotnetDesktopRuntimeUrl =
        "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe";
    private const string WindowsAppRuntimeUrl =
        "https://aka.ms/windowsappsdk/1.8/latest/windowsappruntimeinstall-x64.exe";
    private const string WebPayloadName = "CrabDesk-portable-web-win-x64.zip";
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(20) };

    [STAThread]
    private static async Task<int> Main()
    {
        try
        {
            var metadata = Assembly.GetExecutingAssembly()
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .ToDictionary(item => item.Key, item => item.Value ?? string.Empty, StringComparer.OrdinalIgnoreCase);
            var owner = metadata.GetValueOrDefault("GitHubRepositoryOwner", "yixing233");
            var repository = metadata.GetValueOrDefault("GitHubRepositoryName", "CrabDesk");
            var version = metadata.GetValueOrDefault("ReleaseVersion", "0.0.0");
            var root = Path.Combine(Path.GetTempPath(), "CrabDesk-WebSetup", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                EnsureDesktopRuntime(root);
                EnsureWindowsAppRuntime(root);

                var releaseBase = $"https://github.com/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}/releases/download/v{version}";
                var checksumPath = await DownloadAsync($"{releaseBase}/SHA256SUMS.txt", Path.Combine(root, "SHA256SUMS.txt"));
                var payloadPath = await DownloadAsync($"{releaseBase}/{WebPayloadName}", Path.Combine(root, WebPayloadName));
                VerifySha256(payloadPath, FindExpectedHash(checksumPath, WebPayloadName));

                var installDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CrabDesk");
                Directory.CreateDirectory(installDirectory);
                ZipFile.ExtractToDirectory(payloadPath, installDirectory, true);
                CreateShortcuts(Path.Combine(installDirectory, "CrabDesk.WinUI.exe"));
                Process.Start(new ProcessStartInfo(Path.Combine(installDirectory, "CrabDesk.WinUI.exe"))
                {
                    UseShellExecute = true
                });
                return 0;
            }
            finally
            {
                TryDeleteDirectory(root);
            }
        }
        catch (Exception exception)
        {
            ShowError($"CrabDesk 在线安装失败：{exception.Message}");
            return 1;
        }
    }

    private static void EnsureDesktopRuntime(string root)
    {
        if (GetCommandOutput("dotnet", "--list-runtimes")?.Contains("Microsoft.WindowsDesktop.App 8.", StringComparison.OrdinalIgnoreCase) == true)
        {
            return;
        }
        var installer = DownloadAsync(DotnetDesktopRuntimeUrl, Path.Combine(root, "windowsdesktop-runtime.exe")).GetAwaiter().GetResult();
        RunElevatedInstaller(installer, "/install /quiet /norestart");
    }

    private static void EnsureWindowsAppRuntime(string root)
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\WindowsAppRuntime\InstalledVersions");
        if (key?.GetSubKeyNames().Any(name => name.StartsWith("1.8", StringComparison.OrdinalIgnoreCase)) == true)
        {
            return;
        }
        var installer = DownloadAsync(WindowsAppRuntimeUrl, Path.Combine(root, "windowsappruntime.exe")).GetAwaiter().GetResult();
        RunElevatedInstaller(installer, "/quiet /norestart");
    }

    private static void RunElevatedInstaller(string path, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(path, arguments)
        {
            UseShellExecute = true,
            Verb = "runas"
        });
        process?.WaitForExit();
        if (process is null || process.ExitCode is not 0)
        {
            throw new InvalidOperationException("依赖环境安装未完成。");
        }
    }

    private static async Task<string> DownloadAsync(string url, string path)
    {
        using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync();
        await using var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await source.CopyToAsync(target);
        return path;
    }

    private static void VerifySha256(string path, string expected)
    {
        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("应用包 SHA-256 校验失败。");
        }
    }

    private static string FindExpectedHash(string checksumPath, string fileName)
    {
        foreach (var line in File.ReadLines(checksumPath, Encoding.ASCII))
        {
            var parts = line.Trim().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[1].TrimStart('*').Equals(fileName, StringComparison.OrdinalIgnoreCase) && parts[0].Length == 64)
            {
                return parts[0];
            }
        }
        throw new InvalidDataException("校验文件中缺少应用包校验值。");
    }

    private static string? GetCommandOutput(string fileName, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            return process is null ? null : process.StandardOutput.ReadToEnd();
        }
        catch
        {
            return null;
        }
    }

    private static void CreateShortcuts(string executable)
    {
        var script = "$w=New-Object -ComObject WScript.Shell; $t='" +
            EscapePowerShell(executable) +
            "'; foreach($p in @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs'))){ if($p){$s=$w.CreateShortcut((Join-Path $p 'CrabDesk.lnk'));$s.TargetPath=$t;$s.WorkingDirectory=[IO.Path]::GetDirectoryName($t);$s.Save()}}";
        using var process = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"{script}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false
        });
        process?.WaitForExit();
    }

    private static string EscapePowerShell(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static void ShowError(string message)
    {
        _ = NativeMessageBox(IntPtr.Zero, message, "CrabDesk", 0x10);
    }

    private static void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, true); } catch { }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    private static int NativeMessageBox(IntPtr hWnd, string text, string caption, uint type) =>
        MessageBox(hWnd, text, caption, type);
}
