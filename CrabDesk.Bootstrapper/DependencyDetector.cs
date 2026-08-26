using System.Diagnostics;
using Microsoft.Win32;

namespace CrabDesk.Bootstrapper;

internal static class DependencyDetector
{
    internal static bool IsInstalled(SetupDependency dependency) => dependency.Kind switch
    {
        SetupDependencyKind.DotNetDesktopRuntime =>
            IsDotNetDesktopRuntimeInstalled(dependency.RequiredMajorVersion, dependency.MinimumVersion),
        SetupDependencyKind.VisualCppRuntime =>
            IsVisualCppRuntimeInstalled(dependency.MinimumVersion ?? new Version(14, 0)),
        SetupDependencyKind.WindowsAppRuntime =>
            IsWindowsAppRuntimeInstalled(dependency.RequiredPackageName),
        _ => false
    };

    internal static bool IsSupportedRuntimeDirectory(string directoryName, int requiredMajorVersion, Version? minimumVersion = null)
    {
        if (directoryName.Contains('-', StringComparison.Ordinal) ||
            !Version.TryParse(directoryName, out var version))
        {
            return false;
        }

        if (version.Major != requiredMajorVersion)
        {
            return false;
        }

        if (minimumVersion is not null && version < minimumVersion)
        {
            return false;
        }

        return true;
    }

    private static bool IsDotNetDesktopRuntimeInstalled(int requiredMajorVersion, Version? minimumVersion = null)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(@"SOFTWARE\dotnet\Setup\InstalledVersions\x64");
            if (key?.GetValue("InstallLocation") is string configured && !string.IsNullOrWhiteSpace(configured))
            {
                roots.Add(configured);
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            roots.Add(Path.Combine(programFiles, "dotnet"));
        }

        foreach (var root in roots)
        {
            var sharedFramework = Path.Combine(root, "shared", "Microsoft.WindowsDesktop.App");
            try
            {
                if (Directory.Exists(sharedFramework) && Directory.EnumerateDirectories(sharedFramework)
                    .Select(Path.GetFileName)
                    .Any(name => name is not null && IsSupportedRuntimeDirectory(name, requiredMajorVersion, minimumVersion)))
                {
                    return true;
                }
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
            }
        }

        return false;
    }

    private static bool IsVisualCppRuntimeInstalled(Version minimumVersion)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\X64");
            if (Convert.ToInt32(key?.GetValue("Installed", 0)) != 1)
            {
                return false;
            }

            var versionText = Convert.ToString(key?.GetValue("Version"))?.TrimStart('v', 'V');
            return Version.TryParse(versionText, out var version) && version >= minimumVersion;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or FormatException)
        {
            return false;
        }
    }

    private static bool IsWindowsAppRuntimeInstalled(string packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName) ||
            packageName.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '_'))
        {
            return false;
        }

        var command = $"$package = Get-AppxPackage -Name '{packageName}' -ErrorAction SilentlyContinue | " +
                      "Where-Object { $_.Architecture -eq 'X64' -or $_.Architecture -eq 'Neutral' } | " +
                      "Select-Object -First 1; if ($null -ne $package) { $package.PackageFullName }";
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                Arguments = $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{command}\""
            });
            if (process is null)
            {
                return false;
            }

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(15_000))
            {
                process.Kill(true);
                return false;
            }

            return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    internal static bool TryGetExistingInstallation(out string? installPath, out string? installedVersion)
    {
        installPath = null;
        installedVersion = null;

        const string uninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{8AF9FCA9-D889-4ED7-B5A2-AC052B94016D}_is1";

        var hives = new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine };
        var views = new[] { RegistryView.Registry64, RegistryView.Registry32 };

        foreach (var hive in hives)
        {
            foreach (var view in views)
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var key = baseKey.OpenSubKey(uninstallKey);
                    if (key != null)
                    {
                        var loc = key.GetValue("InstallLocation") as string;
                        var ver = key.GetValue("DisplayVersion") as string;
                        if (!string.IsNullOrWhiteSpace(loc) && Directory.Exists(loc))
                        {
                            installPath = loc;
                            installedVersion = ver ?? string.Empty;
                            return true;
                        }
                    }
                }
                catch
                {
                }
            }
        }

        // Fallback: check standard paths
        var candidates = new[]
        {
            @"D:\Program Files\CrabDesk",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "CrabDesk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "CrabDesk")
        };

        foreach (var dir in candidates)
        {
            var exe = Path.Combine(dir, "CrabDesk.WinUI.exe");
            if (File.Exists(exe))
            {
                installPath = dir;
                try
                {
                    var fvi = FileVersionInfo.GetVersionInfo(exe);
                    installedVersion = fvi.ProductVersion ?? fvi.FileVersion ?? "20260826.02";
                }
                catch
                {
                    installedVersion = "20260826.02";
                }
                return true;
            }
        }

        return false;
    }
}
