using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace CrabDesk.Bootstrapper;

internal static class Program
{
    private const int MinimumWindowsBuild = 19041;
    private const long MaximumDependencyBytes = 512L * 1024 * 1024;
    private const long MaximumPayloadBytes = 512L * 1024 * 1024;
    private const int MessageBoxOkCancel = 0x00000001;
    private const int MessageBoxIconInformation = 0x00000040;
    private const int MessageBoxIconError = 0x00000010;
    private const int MessageBoxIconWarning = 0x00000030;
    private const int MessageBoxResultOk = 1;
    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 8
    })
    {
        Timeout = TimeSpan.FromMinutes(20)
    };

    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        InitializeDpiAwareness();
        OleInitialize(IntPtr.Zero);
        try
        {
            ValidatePlatform();
            var metadata = ReadMetadata();
            var dependencies = BuildDependencies(metadata);
            if (TryGetDiagnosticPath(args, out var diagnosticPath))
            {
                WriteDiagnostics(diagnosticPath, metadata, dependencies);
                return 0;
            }

            var isSilent = args.Any(arg => arg.Equals("/VERYSILENT", StringComparison.OrdinalIgnoreCase) ||
                                           arg.Equals("/SILENT", StringComparison.OrdinalIgnoreCase));
            if (!isSilent)
            {
                return InstallerWindow.Run(metadata, dependencies, args, Client);
            }

            return await RunHeadlessInstallationAsync(metadata, dependencies, args);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            ShowMessage("安装已取消。", MessageBoxIconWarning);
            return 2;
        }
        catch (Exception exception)
        {
            ShowMessage($"CrabDesk 在线安装失败：{exception.Message}", MessageBoxIconError);
            return 1;
        }
    }

    private static async Task<int> RunHeadlessInstallationAsync(
        IReadOnlyDictionary<string, string> metadata,
        IReadOnlyList<SetupDependency> dependencies,
        string[] args)
    {
        var missing = dependencies
            .Where(dependency => !DependencyDetector.IsInstalled(dependency))
            .ToArray();

        EnsureSufficientDiskSpace(missing.Length, args);

        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "CrabDesk-Setup",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            var restartRequired = false;
            foreach (var dependency in missing)
            {
                var dependencyPath = Path.Combine(temporaryRoot, dependency.FileName);
                await DownloadVerifier.DownloadAsync(
                    Client,
                    dependency.DownloadUri,
                    dependencyPath,
                    MaximumDependencyBytes);
                DownloadVerifier.VerifySha256(dependencyPath, dependency.Sha256);
                DownloadVerifier.VerifyTrustedMicrosoftSignature(dependencyPath);

                var exitCode = RunInstaller(
                    dependencyPath,
                    dependency.SilentArguments.Split(' ', StringSplitOptions.RemoveEmptyEntries),
                    elevate: true);
                if (!SetupPolicy.IsSuccessfulInstallerExitCode(exitCode))
                {
                    throw new InvalidOperationException(
                        $"{dependency.DisplayName} 安装失败，退出代码：{exitCode}。");
                }

                restartRequired |= SetupPolicy.RequiresRestart(exitCode);
                if (!DependencyDetector.IsInstalled(dependency))
                {
                    throw new InvalidOperationException($"{dependency.DisplayName} 安装后仍未检测到。");
                }
            }

            var payloadPath = Path.Combine(temporaryRoot, SetupPolicy.PayloadName);
            await ExtractPayloadAsync(payloadPath);
            DownloadVerifier.VerifySha256(
                payloadPath,
                GetRequiredMetadata(metadata, "PayloadSha256"));

            SetupPolicy.StopRunningAppInstances();

            var payloadExitCode = RunInstaller(
                payloadPath,
                BuildPayloadArguments(args),
                elevate: true);
            if (!SetupPolicy.IsSuccessfulInstallerExitCode(payloadExitCode))
            {
                throw new InvalidOperationException($"CrabDesk 安装失败，退出代码：{payloadExitCode}。");
            }
            restartRequired |= SetupPolicy.RequiresRestart(payloadExitCode);

            if (restartRequired)
            {
                ShowMessage(
                    "CrabDesk 已安装完成。系统需要重新启动后才能运行 CrabDesk。",
                    MessageBoxIconInformation);
                return 3010;
            }

            if (!restartRequired)
            {
                TryLaunchInstalledApp();
            }
            return 0;
        }
        finally
        {
            TryDeleteDirectory(temporaryRoot);
        }
    }

    private static void ValidatePlatform()
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitOperatingSystem || !Environment.Is64BitProcess)
        {
            throw new PlatformNotSupportedException("CrabDesk 仅支持 Windows x64。");
        }
        if (Environment.OSVersion.Version.Build < MinimumWindowsBuild)
        {
            throw new PlatformNotSupportedException(
                $"CrabDesk 需要 Windows 10 版本 2004（内部版本 {MinimumWindowsBuild}）或更高版本。");
        }
    }

    private static Dictionary<string, string> ReadMetadata() => Assembly.GetExecutingAssembly()
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .ToDictionary(
            attribute => attribute.Key,
            attribute => attribute.Value ?? string.Empty,
            StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<SetupDependency> BuildDependencies(
        IReadOnlyDictionary<string, string> metadata)
    {
        static Uri RequiredUri(IReadOnlyDictionary<string, string> values, string key)
        {
            var value = GetRequiredMetadata(values, key);
            if (!SetupPolicy.TryCreateHttpsUri(value, out var uri))
            {
                throw new InvalidDataException($"{key} 不是有效的 HTTPS 地址。");
            }
            return uri;
        }

        if (!Version.TryParse(GetRequiredMetadata(metadata, "VisualCppMinimumVersion"), out var visualCppVersion))
        {
            throw new InvalidDataException("VisualCppMinimumVersion 格式无效。");
        }

        if (!Version.TryParse(GetRequiredMetadata(metadata, "DotNetDesktopMinimumVersion"), out var dotNetDesktopVersion))
        {
            throw new InvalidDataException("DotNetDesktopMinimumVersion 格式无效。");
        }

        if (!Version.TryParse(GetRequiredMetadata(metadata, "WindowsAppRuntimeMinimumVersion"), out var windowsAppRuntimeVersion))
        {
            throw new InvalidDataException("WindowsAppRuntimeMinimumVersion 格式无效。");
        }

        return
        [
            new SetupDependency(
                SetupDependencyKind.VisualCppRuntime,
                "Microsoft Visual C++ x64 运行库",
                RequiredUri(metadata, "VisualCppInstallerUrl"),
                GetRequiredMetadata(metadata, "VisualCppInstallerSha256"),
                "vc_redist.x64.exe",
                "/install /quiet /norestart",
                MinimumVersion: visualCppVersion),
            new SetupDependency(
                SetupDependencyKind.DotNetDesktopRuntime,
                ".NET 8 Desktop Runtime x64",
                RequiredUri(metadata, "DotNetDesktopInstallerUrl"),
                GetRequiredMetadata(metadata, "DotNetDesktopInstallerSha256"),
                "windowsdesktop-runtime-win-x64.exe",
                "/install /quiet /norestart",
                MinimumVersion: dotNetDesktopVersion,
                RequiredMajorVersion: 8),
            new SetupDependency(
                SetupDependencyKind.WindowsAppRuntime,
                "Windows App SDK 1.8 Runtime x64",
                RequiredUri(metadata, "WindowsAppRuntimeInstallerUrl"),
                GetRequiredMetadata(metadata, "WindowsAppRuntimeInstallerSha256"),
                "windowsappruntimeinstall-x64.exe",
                "--quiet",
                MinimumVersion: windowsAppRuntimeVersion,
                RequiredPackageName: "Microsoft.WindowsAppRuntime.1.8")
        ];
    }

    private static string GetRequiredMetadata(IReadOnlyDictionary<string, string> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"安装器缺少 {key} 元数据。");
        }
        return value.Trim();
    }

    private static bool TryGetDiagnosticPath(string[] args, out string path)
    {
        if (args.Length == 2 && args[0].Equals("--diagnose", StringComparison.OrdinalIgnoreCase))
        {
            path = Path.GetFullPath(args[1]);
            return true;
        }

        path = string.Empty;
        return false;
    }

    private static async Task ExtractPayloadAsync(string destination)
    {
        await using var source = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("CrabDesk.Payload.exe") ??
            throw new InvalidOperationException("安装器中缺少 CrabDesk 应用负载。");
        if (source.Length <= 0 || source.Length > MaximumPayloadBytes)
        {
            throw new InvalidDataException("CrabDesk 应用负载大小无效。");
        }

        await using var target = new FileStream(
            destination,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(target);
        await target.FlushAsync();
    }

    private static IReadOnlyList<string> BuildPayloadArguments(string[] setupArguments)
    {
        var forwarded = setupArguments
            .Where(argument => argument.StartsWith("/", StringComparison.Ordinal))
            .ToList();
        if (forwarded.Count == 0)
        {
            forwarded.AddRange(["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-"]);
        }
        return forwarded;
    }

    private static void WriteDiagnostics(
        string path,
        IReadOnlyDictionary<string, string> metadata,
        IReadOnlyList<SetupDependency> dependencies)
    {
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        var lines = new List<string>
        {
            "Platform=Windows-x64",
            $"WindowsBuild={Environment.OSVersion.Version.Build}",
            $"ReleaseVersion={GetRequiredMetadata(metadata, "ReleaseVersion")}",
            $"PayloadAsset={SetupPolicy.PayloadName}",
            $"PayloadEmbedded={Assembly.GetExecutingAssembly().GetManifestResourceNames().Contains("CrabDesk.Payload.exe", StringComparer.Ordinal)}"
        };
        lines.AddRange(dependencies.Select(dependency =>
            $"Dependency.{dependency.Kind}={(DependencyDetector.IsInstalled(dependency) ? "Installed" : "Missing")}"));
        File.WriteAllLines(path, lines, new UTF8Encoding(false));
    }

    private static bool ConfirmDependencyInstallation(IReadOnlyList<SetupDependency> missing)
    {
        var list = string.Join(Environment.NewLine, missing.Select(item => $"• {item.DisplayName}"));
        var message = "CrabDesk 安装引导程序" + Environment.NewLine + Environment.NewLine +
                      "检测到当前系统缺少以下运行组件：" + Environment.NewLine +
                      list + Environment.NewLine + Environment.NewLine +
                      "安装器将在后台自动从 Microsoft 官方下载并安装这些组件，无需您手动打开浏览器。" + Environment.NewLine + Environment.NewLine +
                      "点击【确定】开始自动下载与安装。";
        return NativeMessageBox(
            IntPtr.Zero,
            message,
            "CrabDesk 安装",
            MessageBoxOkCancel | MessageBoxIconInformation) == MessageBoxResultOk;
    }

    private static int RunInstaller(string path, IEnumerable<string> arguments, bool elevate)
    {
        var startInfo = new ProcessStartInfo(path)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(path) ?? string.Empty
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        if (elevate)
        {
            startInfo.Verb = "runas";
        }

        using var process = Process.Start(startInfo) ??
            throw new InvalidOperationException($"无法启动 {Path.GetFileName(path)}。");
        return SetupPolicy.WaitForInstaller(process);
    }

    private static void EnsureSufficientDiskSpace(int missingDependencyCount, string[]? setupArguments = null)
    {
        var payloadBytes = 0L;
        try
        {
            using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("CrabDesk.Payload.exe");
            if (payload?.CanSeek == true) payloadBytes = payload.Length;
        }
        catch
        {
        }

        var required = SetupPolicy.CalculateRequiredSpaceBytes(payloadBytes, missingDependencyCount);
        var installPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "CrabDesk");
        var dirArgument = setupArguments?.FirstOrDefault(arg => arg.StartsWith("/DIR=", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(dirArgument))
        {
            installPath = dirArgument[5..].Trim().Trim('"');
        }
        EnsurePathHasSpace(installPath, payloadBytes + SetupPolicy.DiskSafetyMarginBytes, "目标磁盘");
        EnsurePathHasSpace(Path.GetTempPath(), required, "临时文件磁盘");
    }

    private static void EnsurePathHasSpace(string path, long required, string label)
    {
        if (SetupPolicy.TryGetAvailableDiskSpace(path, out var available) &&
            !SetupPolicy.HasSufficientSpace(available, required))
        {
            throw new IOException($"安装所需空间约 {required / (1024 * 1024)} MB，但{label}仅剩 {available / (1024 * 1024)} MB。");
        }
    }

    private static void TryLaunchInstalledApp()
    {
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

    private static void ShowMessage(string message, int icon) =>
        _ = NativeMessageBox(IntPtr.Zero, message, "CrabDesk", icon);

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void InitializeDpiAwareness()
    {
        try
        {
            SetProcessDpiAwarenessContext(new IntPtr(-4)); // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
        }
        catch
        {
            try
            {
                SetProcessDPIAware();
            }
            catch
            {
            }
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    [System.Runtime.InteropServices.DllImport("ole32.dll")]
    private static extern int OleInitialize(IntPtr pvReserved);

    [System.Runtime.InteropServices.DllImport(
        "user32.dll",
        EntryPoint = "MessageBoxW",
        CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int NativeMessageBox(IntPtr window, string text, string caption, int type);
}
