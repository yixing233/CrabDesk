using System.Diagnostics;

namespace CrabDesk.Bootstrapper;

internal static class SetupPolicy
{
    internal const string PayloadName = "CrabDesk-Payload-x64.exe";
    internal const int DownloadMaxAttempts = 3;
    internal const int InstallerTimeoutMilliseconds = 20 * 60 * 1000;
    internal const long DependencyDownloadAllowanceBytes = 512L * 1024 * 1024;
    internal const long DiskSafetyMarginBytes = 128L * 1024 * 1024;

    internal static bool IsSuccessfulInstallerExitCode(int exitCode) =>
        exitCode is 0 or 1638 or 1641 or 3010;

    internal static bool RequiresRestart(int exitCode) => exitCode is 1641 or 3010;

    internal static bool IsTransientDownloadStatus(System.Net.HttpStatusCode? statusCode) =>
        statusCode is System.Net.HttpStatusCode.RequestTimeout or
            System.Net.HttpStatusCode.TooManyRequests or
            System.Net.HttpStatusCode.InternalServerError or
            System.Net.HttpStatusCode.BadGateway or
            System.Net.HttpStatusCode.ServiceUnavailable or
            System.Net.HttpStatusCode.GatewayTimeout;

    internal static TimeSpan GetRetryDelay(int failedAttempt)
    {
        if (failedAttempt < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(failedAttempt));
        }

        return TimeSpan.FromSeconds(Math.Min(8, Math.Pow(2, failedAttempt - 1)));
    }

    internal static long CalculateRequiredSpaceBytes(long payloadBytes, int missingDependencyCount)
    {
        payloadBytes = Math.Max(0, payloadBytes);
        missingDependencyCount = Math.Max(0, missingDependencyCount);
        try
        {
            return checked(payloadBytes +
                missingDependencyCount * DependencyDownloadAllowanceBytes +
                DiskSafetyMarginBytes);
        }
        catch (OverflowException)
        {
            return long.MaxValue;
        }
    }

    internal static bool HasSufficientSpace(long availableBytes, long requiredBytes) =>
        availableBytes >= 0 && requiredBytes >= 0 && availableBytes >= requiredBytes;

    internal static int WaitForInstaller(Process process)
    {
        if (process.WaitForExit(InstallerTimeoutMilliseconds))
        {
            return process.ExitCode;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
                process.WaitForExit(2_000);
            }
        }
        catch
        {
        }

        throw new TimeoutException($"安装程序运行超过 {InstallerTimeoutMilliseconds / 60000} 分钟，已终止。");
    }

    internal static bool TryGetAvailableDiskSpace(string path, out long availableBytes)
    {
        availableBytes = 0;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrWhiteSpace(root))
            {
                return false;
            }

            availableBytes = new DriveInfo(root).AvailableFreeSpace;
            return availableBytes >= 0;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static bool TryCreateHttpsUri(string value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var candidate) &&
            candidate.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            uri = candidate;
            return true;
        }

        uri = null!;
        return false;
    }

    internal static string EnsureAppFolder(string path, string appName = "CrabDesk")
    {
        var clean = path?.Trim().Trim('"', '\'').TrimEnd('\\', '/') ?? string.Empty;
        if (string.IsNullOrWhiteSpace(clean))
        {
            return path ?? string.Empty;
        }

        var leaf = Path.GetFileName(clean);
        if (!string.Equals(leaf, appName, StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(clean, appName);
        }

        return clean;
    }

    internal static void StopRunningAppInstances(string? installPath = null)
    {
        var targetNames = new[] { "CrabDesk.WinUI", "CrabDesk" };
        foreach (var name in targetNames)
        {
            try
            {
                foreach (var proc in Process.GetProcessesByName(name))
                {
                    try
                    {
                        if (!proc.HasExited)
                        {
                            proc.Kill();
                            proc.WaitForExit(1500);
                        }
                    }
                    catch
                    {
                    }
                    finally
                    {
                        proc.Dispose();
                    }
                }
            }
            catch
            {
            }
        }

        if (!string.IsNullOrWhiteSpace(installPath) && Directory.Exists(installPath))
        {
            try
            {
                var fullInstall = Path.GetFullPath(installPath).TrimEnd('\\', '/');
                foreach (var proc in Process.GetProcesses())
                {
                    try
                    {
                        var mainModulePath = proc.MainModule?.FileName;
                        if (!string.IsNullOrWhiteSpace(mainModulePath) &&
                            mainModulePath.StartsWith(fullInstall, StringComparison.OrdinalIgnoreCase))
                        {
                            if (!proc.HasExited)
                            {
                                proc.Kill();
                                proc.WaitForExit(1500);
                            }
                        }
                    }
                    catch
                    {
                    }
                    finally
                    {
                        proc.Dispose();
                    }
                }
            }
            catch
            {
            }
        }
    }
}
