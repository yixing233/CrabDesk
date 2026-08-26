using System.Diagnostics;

namespace CrabDesk.Bootstrapper;

internal static class SetupPolicy
{
    internal const string PayloadName = "CrabDesk-Payload-x64.exe";

    internal static bool IsSuccessfulInstallerExitCode(int exitCode) =>
        exitCode is 0 or 1638 or 1641 or 3010;

    internal static bool RequiresRestart(int exitCode) => exitCode is 1641 or 3010;

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
