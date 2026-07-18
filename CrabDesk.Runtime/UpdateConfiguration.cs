using System.Reflection;
using CrabDesk.Core;

namespace CrabDesk.Runtime;

internal static class UpdateConfiguration
{
    private const string LegacyInstallerAssetName = "CrabDesk-Setup-x64.exe";
    private const string WebInstallerAssetName = "CrabDesk-Setup-Web-x64.exe";
    private const string FullInstallerAssetName = "CrabDesk-Setup-Full-x64.exe";

    internal static string CurrentVersion
    {
        get
        {
            var assembly = Assembly.GetEntryAssembly() ?? typeof(UpdateConfiguration).Assembly;
            var informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
            {
                var build = informational.IndexOf('+');
                return build < 0 ? informational : informational[..build];
            }
            var version = assembly.GetName().Version;
            return version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    internal static (string Owner, string Repository) ResolveRepository(UpdateSettings settings)
    {
        var combined = Environment.GetEnvironmentVariable("CRABDESK_GITHUB_REPOSITORY");
        if (!string.IsNullOrWhiteSpace(combined))
        {
            var parts = combined.Split('/', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2)
            {
                return (parts[0], parts[1]);
            }
        }

        var metadata = (Assembly.GetEntryAssembly() ?? typeof(UpdateConfiguration).Assembly)
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(attribute => attribute.Key, attribute => attribute.Value ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        metadata.TryGetValue("GitHubRepositoryOwner", out var owner);
        metadata.TryGetValue("GitHubRepositoryName", out var repository);
        if (!string.IsNullOrWhiteSpace(owner) && !string.IsNullOrWhiteSpace(repository))
        {
            return (owner.Trim(), repository.Trim());
        }
        return (
            settings.RepositoryOwner?.Trim() ?? string.Empty,
            settings.RepositoryName?.Trim() ?? string.Empty);
    }

    internal static string InstallerAssetName
    {
        get
        {
            var metadata = (Assembly.GetEntryAssembly() ?? typeof(UpdateConfiguration).Assembly)
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(attribute =>
                    attribute.Key.Equals("CrabDeskPackageKind", StringComparison.OrdinalIgnoreCase))?
                .Value;
            return metadata?.Equals("Web", StringComparison.OrdinalIgnoreCase) == true
                ? WebInstallerAssetName
                : metadata?.Equals("Full", StringComparison.OrdinalIgnoreCase) == true
                    ? FullInstallerAssetName
                    : LegacyInstallerAssetName;
        }
    }
}
