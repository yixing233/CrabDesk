using System.Diagnostics;
using System.Text.Json;

namespace CrabDesk.Bootstrapper;

/// <summary>Checks the complete unpackaged Windows App SDK dependency graph for the current user.</summary>
internal static class WindowsAppRuntimeDetector
{
    private const int DetectionTimeoutMilliseconds = 15_000;

    internal sealed record Result(bool IsInstalled, string Details);

    internal static Result Detect(SetupDependency dependency)
    {
        if (string.IsNullOrWhiteSpace(dependency.RequiredPackageName) || dependency.MinimumVersion is null)
            return new(false, "Windows App Runtime metadata is incomplete");

        if (!TryReadPackages(PackageQueryCommand, out var packages, out var error))
            return new(false, error);

        return Evaluate(packages, dependency.RequiredPackageName, dependency.MinimumVersion);
    }

    internal static Result Evaluate(
        IReadOnlyList<Package> packages,
        string frameworkPackageName,
        Version minimumVersion)
    {
        var suffix = frameworkPackageName.StartsWith("Microsoft.WindowsAppRuntime.", StringComparison.OrdinalIgnoreCase)
            ? frameworkPackageName["Microsoft.WindowsAppRuntime.".Length..]
            : string.Empty;
        if (string.IsNullOrWhiteSpace(suffix))
            return new(false, "Windows App Runtime framework package name is invalid");

        bool HasPackage(string name) => packages.Any(p =>
            p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
            IsUsableX64(p) &&
            p.Version >= minimumVersion);

        var missing = new List<string>();
        if (!HasPackage(frameworkPackageName)) missing.Add("Framework");
        if (!HasPackage($"MicrosoftCorporationII.WinAppRuntime.Main.{suffix}")) missing.Add("Main");
        if (!HasPackage("MicrosoftCorporationII.WinAppRuntime.Singleton")) missing.Add("Singleton");

        // DDLM identity names contain the runtime version, while package updates
        // can legitimately leave the framework/Main packages at a newer version
        // than the DDLM (for example 8000.946 with DDLM 8000.921). Require a
        // compatible x64 DDLM at the minimum version, not an exact version match.
        if (!packages.Any(p =>
                p.Name.StartsWith("Microsoft.WinAppRuntime.DDLM.", StringComparison.OrdinalIgnoreCase) &&
                p.Name.EndsWith("-x6", StringComparison.OrdinalIgnoreCase) &&
                IsUsableX64(p) &&
                p.Version >= minimumVersion))
        {
            missing.Add("DDLM");
        }

        return missing.Count == 0
            ? new(true, "all Windows App Runtime x64 packages are installed")
            : new(false, "missing or unusable packages: " + string.Join(", ", missing));
    }

    private const string PackageQueryCommand = @"
$items = @(Get-AppxPackage -ErrorAction SilentlyContinue | Where-Object {
  $_.Name -like 'Microsoft.WindowsAppRuntime.*' -or
  $_.Name -like 'MicrosoftCorporationII.WinAppRuntime.*' -or
  $_.Name -like 'Microsoft.WinAppRuntime.DDLM.*'
} | ForEach-Object {
  [pscustomobject]@{ Name=$_.Name; Version=$_.Version.ToString(); Architecture=$_.Architecture.ToString(); Status=$_.Status.ToString() }
})
ConvertTo-Json -InputObject $items -Compress
";
    internal static bool IsUsableX64(Package package) =>
        package.Architecture.Equals("X64", StringComparison.OrdinalIgnoreCase) &&
        (string.IsNullOrWhiteSpace(package.Status) || package.Status.Equals("Ok", StringComparison.OrdinalIgnoreCase));

    internal sealed record Package(string Name, Version Version, string Architecture, string Status);

    private static bool TryReadPackages(string command, out IReadOnlyList<Package> packages, out string error)
    {
        packages = Array.Empty<Package>();
        error = "Windows App Runtime package query failed";
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", command }
            });
            if (process is null) return false;
            var output = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(DetectionTimeoutMilliseconds))
            {
                process.Kill(true);
                error = "Windows App Runtime package query timed out";
                return false;
            }
            if (process.ExitCode != 0)
            {
                error = string.IsNullOrWhiteSpace(stderr) ? error : stderr.Trim();
                return false;
            }
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(output) ? "[]" : output);
            var values = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray()
                : new[] { document.RootElement }.AsEnumerable();
            var result = new List<Package>();
            foreach (var value in values)
            {
                if (!Version.TryParse(value.GetProperty("Version").GetString(), out var version)) continue;
                result.Add(new Package(
                    value.GetProperty("Name").GetString() ?? string.Empty,
                    version,
                    value.GetProperty("Architecture").GetString() ?? string.Empty,
                    value.GetProperty("Status").GetString() ?? string.Empty));
            }
            packages = result;
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or JsonException)
        {
            error = ex.Message;
            return false;
        }
    }
}
