using CrabDesk.Core;
using CrabDesk.Native;
using Microsoft.Win32;

namespace CrabDesk.Tests;

/// <summary>
/// Registry-touching tests for the shell-extension inventory. They follow the
/// existing convention (see <see cref="WindowsIntegrationTests"/>): write into
/// a per-run GUID subtree of the real HKCU, then delete it in a finally block.
/// </summary>
public sealed class ShellExtensionInventoryTests
{
    [Fact]
    public void ReadsOverlayHandlersAndResolvesTheirServerDll()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var stem = @"Software\CrabDesk\Tests\StallDiagnostics\" + Guid.NewGuid().ToString("N");
        var overlayPath = stem + @"\ShellIconOverlayIdentifiers";
        var clsidRoot = stem + @"\CLSID";
        var clsid = "{" + Guid.NewGuid().ToString("D").ToUpperInvariant() + "}";
        const string dllPath = @"D:\BaiduNetdisk\YunShellExtV164.dll";
        try
        {
            using (var overlay = Registry.CurrentUser.CreateSubKey(overlayPath))
            using (var handler = overlay!.CreateSubKey("  .WorkspaceExt0  "))
            {
                handler.SetValue(null, clsid);
            }

            using (var server = Registry.CurrentUser.CreateSubKey($@"{clsidRoot}\{clsid}\InprocServer32"))
            {
                server!.SetValue(null, dllPath);
            }

            var entries = ShellExtensionInventory.ReadIconOverlayHandlers(
                Registry.CurrentUser,
                overlayPath,
                clsidRoot);

            var entry = Assert.Single(entries);
            // The registered name carries Explorer's sort-prefix padding.
            Assert.Equal(".WorkspaceExt0", entry.Name);
            Assert.Equal(ShellExtensionKind.IconOverlay, entry.Kind);
            Assert.Equal(dllPath, entry.Path);
            Assert.Equal("百度网盘", entry.Product);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(stem, false);
        }
    }

    [Fact]
    public void FallsBackToTheClsidDefaultValueWhenThereIsNoInprocServer32()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var stem = @"Software\CrabDesk\Tests\StallDiagnostics\" + Guid.NewGuid().ToString("N");
        var overlayPath = stem + @"\ShellIconOverlayIdentifiers";
        var clsidRoot = stem + @"\CLSID";
        var clsid = "{" + Guid.NewGuid().ToString("D").ToUpperInvariant() + "}";
        const string localServer = @"D:\Kingsoft\WPS Office\office6\kwpsshellext64.dll";
        try
        {
            using (var overlay = Registry.CurrentUser.CreateSubKey(overlayPath))
            using (var handler = overlay!.CreateSubKey("QingShellExt"))
            {
                handler.SetValue(null, clsid);
            }

            using (var key = Registry.CurrentUser.CreateSubKey($@"{clsidRoot}\{clsid}"))
            {
                key!.SetValue(null, localServer);
            }

            var entries = ShellExtensionInventory.ReadIconOverlayHandlers(
                Registry.CurrentUser,
                overlayPath,
                clsidRoot);

            var entry = Assert.Single(entries);
            Assert.Equal(localServer, entry.Path);
            Assert.Equal("WPS Office", entry.Product);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(stem, false);
        }
    }

    [Fact]
    public void UnresolvableClsidYieldsAnEmptyPathRatherThanThrowing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var stem = @"Software\CrabDesk\Tests\StallDiagnostics\" + Guid.NewGuid().ToString("N");
        var overlayPath = stem + @"\ShellIconOverlayIdentifiers";
        try
        {
            using (var overlay = Registry.CurrentUser.CreateSubKey(overlayPath))
            using (var handler = overlay!.CreateSubKey("GhostHandler"))
            {
                handler.SetValue(null, "{00000000-0000-0000-0000-0000000000FF}");
            }

            var entries = ShellExtensionInventory.ReadIconOverlayHandlers(
                Registry.CurrentUser,
                overlayPath,
                stem + @"\CLSID");

            var entry = Assert.Single(entries);
            Assert.Equal(string.Empty, entry.Path);
            Assert.Equal("未知", entry.Product);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(stem, false);
        }
    }

    [Fact]
    public void AMissingOverlayKeyIsAnEmptyListNotAFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var entries = ShellExtensionInventory.ReadIconOverlayHandlers(
            Registry.CurrentUser,
            @"Software\CrabDesk\Tests\StallDiagnostics\DoesNotExist\" + Guid.NewGuid().ToString("N"),
            @"Software\CrabDesk\Tests\StallDiagnostics\CLSID");

        Assert.Empty(entries);
    }

    [Fact]
    public void ReadingModulesFromAnUnusableHandleYieldsNothing()
    {
        // No window, so no Explorer process to inspect. This is the path a
        // protected or elevated Explorer also takes.
        Assert.Empty(ShellExtensionInventory.ReadDesktopExplorerModules(IntPtr.Zero));
    }

    [Fact]
    public void ProbeReportsNoMeasurementForAnUnusableWindow()
    {
        Assert.Equal(
            DesktopStallProbe.NotMeasured,
            DesktopStallProbe.MeasureWorstResponseMs(IntPtr.Zero, samples: 3, spacingMs: 0));

        var paired = DesktopStallProbe.MeasurePairedWorstResponse(IntPtr.Zero, IntPtr.Zero, 3, 0);
        Assert.False(paired.First.HasValue);
        Assert.False(paired.Second.HasValue);
        Assert.Equal(0, paired.PairedRounds);
    }
}
