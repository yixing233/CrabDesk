using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace CrabDesk.Bootstrapper;

internal static class Program
{
    private const string FullInstallerName = "CrabDesk-Setup-x64.exe";
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
                var releaseBase = $"https://github.com/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}/releases/download/v{version}";
                var checksumPath = await DownloadAsync($"{releaseBase}/SHA256SUMS.txt", Path.Combine(root, "SHA256SUMS.txt"));
                var installerPath = await DownloadAsync($"{releaseBase}/{FullInstallerName}", Path.Combine(root, FullInstallerName));
                VerifySha256(installerPath, FindExpectedHash(checksumPath, FullInstallerName));

                using var process = Process.Start(new ProcessStartInfo(installerPath, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-")
                {
                    UseShellExecute = true
                });
                process?.WaitForExit();
                if (process is null || process.ExitCode is not 0)
                {
                    throw new InvalidOperationException("安装程序未能正常完成。");
                }

                TryLaunchInstalledApp();
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

    private static void TryLaunchInstalledApp()
    {
        // Inno Setup with PrivilegesRequired=lowest installs per-user under
        // %LocalAppData%\Programs. Launch the freshly installed application
        // when it exists; otherwise the installer's shortcuts remain usable.
        var candidate = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "CrabDesk",
            "CrabDesk.WinUI.exe");
        if (File.Exists(candidate))
        {
            Process.Start(new ProcessStartInfo(candidate) { UseShellExecute = true });
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
            throw new InvalidDataException("安装包 SHA-256 校验失败。");
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
        throw new InvalidDataException("校验文件中缺少安装包校验值。");
    }

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
