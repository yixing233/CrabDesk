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
            IsWindowsAppRuntimeInstalled(dependency.RequiredPackageName, dependency.MinimumVersion),
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
                if (AreDotNetFrameworksSupported(root, requiredMajorVersion, minimumVersion))
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

    internal static bool AreDotNetFrameworksSupported(
        string root,
        int requiredMajorVersion,
        Version? minimumVersion = null)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        static bool HasSupportedVersion(string path, int major, Version? minimum)
        {
            try
            {
                return Directory.Exists(path) && Directory.EnumerateDirectories(path)
                    .Select(Path.GetFileName)
                    .Any(name => name is not null && IsSupportedRuntimeDirectory(name, major, minimum));
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                return false;
            }
        }

        return HasSupportedVersion(Path.Combine(root, "shared", "Microsoft.NETCore.App"), requiredMajorVersion, minimumVersion) &&
            HasSupportedVersion(Path.Combine(root, "shared", "Microsoft.WindowsDesktop.App"), requiredMajorVersion, minimumVersion);
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

    internal static bool IsSupportedPackageVersion(string? versionText, Version? minimumVersion)
    {
        if (!Version.TryParse(versionText?.Trim(), out var version))
        {
            return false;
        }

        return minimumVersion is null || version >= minimumVersion;
    }

    private static bool IsWindowsAppRuntimeInstalled(string packageName, Version? minimumVersion)
    {
        if (string.IsNullOrWhiteSpace(packageName) ||
            packageName.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '_'))
        {
            return false;
        }

        // Windows App SDK requires the framework, Main and Singleton packages.
        // Checking only the framework package can produce a false positive and
        // still lets the app fail at launch with "required components missing".
        var packageNames = new[]
        {
            packageName,
            "MicrosoftCorporationII.WinAppRuntime.Main.1.8",
            "MicrosoftCorporationII.WinAppRuntime.Singleton"
        };
        var names = string.Join(",", packageNames.Select(name => $"'{name}'"));
        var command = $"$ok=$true; foreach ($name in @({names})) {{ $p=Get-AppxPackage -Name $name -ErrorAction SilentlyContinue | " +
                      "Where-Object { $_.Architecture -eq 'X64' -or $_.Architecture -eq 'Neutral' } | " +
                      "Sort-Object Version -Descending | Select-Object -First 1; " +
                      $"if ($null -eq $p -or $p.Version -lt [version]'{minimumVersion}') {{ $ok=$false }} }}; if ($ok) {{ 'true' }}";
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
            if (process is null) return false;
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(15_000)) { process.Kill(true); return false; }
            return process.ExitCode == 0 && output.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
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
                    installedVersion = fvi.ProductVersion ?? fvi.FileVersion ?? "20260901.01";
                }
                catch
                {
                    installedVersion = "20260901.01";
                }
                return true;
            }
        }

        return false;
    }
}
