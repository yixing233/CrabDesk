using System.Runtime.InteropServices;

namespace CrabDesk.Bootstrapper;

internal enum InstallerPage
{
    Welcome,
    Installing,
    Completed,
    Error
}

internal enum DependencyCheckStatus
{
    Pending,
    Checking,
    Installed,
    Missing,
    Failed
}

internal sealed class DependencyItemState
{
    public required SetupDependency Dependency { get; init; }
    public DependencyCheckStatus Status { get; set; } = DependencyCheckStatus.Pending;
    public bool IsInstalled => Status == DependencyCheckStatus.Installed;
    public string StatusText { get; set; } = "等待检测";
}

internal sealed class InstallerState
{
    public InstallerPage Page { get; set; } = InstallerPage.Welcome;
    public string Version { get; set; } = "20260901.01";
    public bool IsUpgrade { get; set; } = false;
    public string? ExistingVersion { get; set; }
    public string InstallPath { get; set; }
    public bool CreateDesktopShortcut { get; set; } = true;
    public bool RegisterContextMenu { get; set; } = true;
    public bool LaunchOnFinish { get; set; } = true;

    public bool IsCheckingDependencies { get; set; } = true;
    public bool DependencyDetectionFailed { get; set; }
    public bool RestartRequired { get; set; }
    public float CheckingAnimationAngle { get; set; } = 0f;

    public long RequiredSpaceBytes { get; set; } = 85L * 1024 * 1024; // 约 85 MB

    public void UpdateRequiredSpace(int missingDependencyCount, long payloadBytes = 0)
    {
        RequiredSpaceBytes = SetupPolicy.CalculateRequiredSpaceBytes(payloadBytes, missingDependencyCount);
    }

    public List<DependencyItemState> Dependencies { get; set; } = [];

    public string CurrentAction { get; set; } = "准备就绪";
    public string SubAction { get; set; } = "";
    public double ProgressPercentage { get; set; } = 0;
    public double TargetProgressPercentage { get; set; } = 0;
    public string? ErrorMessage { get; set; }

    public string? HoveredControlId { get; set; }
    public string? PressedControlId { get; set; }

    public InstallerState()
    {
        if (DependencyDetector.TryGetExistingInstallation(out var existingPath, out var existingVer))
        {
            IsUpgrade = true;
            ExistingVersion = existingVer;
            InstallPath = existingPath!;
        }
        else
        {
            InstallPath = GetDefaultInstallPath();
        }
    }

    public string GetRequiredSpaceText()
    {
        var mb = RequiredSpaceBytes / (1024.0 * 1024.0);
        return $"{mb:F1} MB";
    }

    public (string text, bool isSufficient, string? driveLabel) GetAvailableSpaceInfo()
    {
        try
        {
            var raw = InstallPath?.Trim().Trim('"', '\'').Trim();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return ("未知", true, null);
            }

            char driveLetter = '\0';
            string driveLabel = "未知";
            string driveRoot = "";

            if (raw.Length >= 2 && raw[1] == ':' && char.IsLetter(raw[0]))
            {
                driveLetter = char.ToUpperInvariant(raw[0]);
                driveLabel = $"{driveLetter}:";
                driveRoot = $"{driveLetter}:\\";
            }
            else
            {
                try
                {
                    var full = Path.GetFullPath(raw);
                    if (full.Length >= 2 && full[1] == ':' && char.IsLetter(full[0]))
                    {
                        driveLetter = char.ToUpperInvariant(full[0]);
                        driveLabel = $"{driveLetter}:";
                        driveRoot = $"{driveLetter}:\\";
                    }
                }
                catch
                {
                }
            }

            // 1. Win32 GetDiskFreeSpaceExW on drive root (e.g. "D:\")
            if (!string.IsNullOrWhiteSpace(driveRoot))
            {
                if (GetDiskFreeSpaceExW(driveRoot, out var freeBytes, out _, out _) && freeBytes > 0)
                {
                    return FormatSpaceResult(freeBytes, driveLabel);
                }
            }

            // 2. Fallback to .NET DriveInfo.GetDrives()
            if (driveLetter != '\0')
            {
                try
                {
                    foreach (var drive in DriveInfo.GetDrives())
                    {
                        if (drive.Name.Length > 0 && char.ToUpperInvariant(drive.Name[0]) == driveLetter && drive.IsReady)
                        {
                            return FormatSpaceResult((ulong)drive.AvailableFreeSpace, driveLabel);
                        }
                    }
                }
                catch
                {
                }
            }

            // 3. Fallback to Win32 GetDiskFreeSpaceW (cluster-based)
            if (!string.IsNullOrWhiteSpace(driveRoot))
            {
                if (GetDiskFreeSpaceW(driveRoot, out var spc, out var bps, out var freeClusters, out _) && spc > 0 && bps > 0)
                {
                    var freeBytes = (ulong)freeClusters * spc * bps;
                    return FormatSpaceResult(freeBytes, driveLabel);
                }
            }

            // 4. Fallback to existing ancestor directories
            var searchPath = raw;
            while (!string.IsNullOrWhiteSpace(searchPath))
            {
                if (Directory.Exists(searchPath) && GetDiskFreeSpaceExW(searchPath, out var freeBytes, out _, out _) && freeBytes > 0)
                {
                    return FormatSpaceResult(freeBytes, driveLabel);
                }
                searchPath = Path.GetDirectoryName(searchPath);
            }
        }
        catch
        {
        }

        return ("未知", true, null);
    }

    private (string text, bool isSufficient, string? driveLabel) FormatSpaceResult(ulong freeBytes, string driveLetter)
    {
        var isSufficient = freeBytes >= (ulong)RequiredSpaceBytes;

        string formatted;
        if (freeBytes >= 1024UL * 1024 * 1024 * 1024)
        {
            formatted = $"{freeBytes / (1024.0 * 1024 * 1024 * 1024):F1} TB";
        }
        else if (freeBytes >= 1024UL * 1024 * 1024)
        {
            formatted = $"{freeBytes / (1024.0 * 1024 * 1024):F1} GB";
        }
        else
        {
            formatted = $"{freeBytes / (1024.0 * 1024):F0} MB";
        }

        return (formatted, isSufficient, driveLetter);
    }

    private static string GetDefaultInstallPath()
    {
        try
        {
            if (GetDiskFreeSpaceExW(@"D:\", out var freeBytes, out _, out _) && freeBytes > 0)
            {
                return @"D:\Program Files\CrabDesk";
            }
            if (Directory.Exists(@"D:\"))
            {
                return @"D:\Program Files\CrabDesk";
            }
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.Name.Length > 0 && char.ToUpperInvariant(drive.Name[0]) == 'D' && drive.IsReady)
                {
                    return @"D:\Program Files\CrabDesk";
                }
            }
        }
        catch
        {
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "CrabDesk");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceExW(
        string lpDirectoryName,
        out ulong lpFreeBytesAvailableToCaller,
        out ulong lpTotalNumberOfBytes,
        out ulong lpTotalNumberOfFreeBytes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceW(
        string lpRootPathName,
        out uint lpSectorsPerCluster,
        out uint lpBytesPerSector,
        out uint lpNumberOfFreeClusters,
        out uint lpTotalNumberOfClusters);
}
