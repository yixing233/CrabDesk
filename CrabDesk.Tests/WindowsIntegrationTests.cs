using CrabDesk.Core;
using CrabDesk.Native;
using Microsoft.Win32;

namespace CrabDesk.Tests;

public sealed class WindowsIntegrationTests
{
    [Fact]
    public void MonitorTopologyReportsConsistentPixelAndDipBounds()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var monitors = new MonitorTopologyService().GetMonitors();

        Assert.NotEmpty(monitors);
        Assert.Contains(monitors, monitor => monitor.IsPrimary);
        Assert.All(monitors, monitor =>
        {
            Assert.InRange(monitor.DpiScale, 0.5, 4);
            Assert.Equal(monitor.PixelBounds.Width / monitor.DpiScale, monitor.Bounds.Width, 3);
            Assert.Equal(monitor.PixelWorkArea.Height / monitor.DpiScale, monitor.WorkArea.Height, 3);
        });
    }

    [Fact]
    public void DesktopContextMenuRegistrationWritesAndRemovesOwnedRegistryTree()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var keyPath = @"Software\CrabDesk\Tests\ContextMenu\" + Guid.NewGuid().ToString("N");
        var submenuClassName = "CrabDesk.Tests.ContextMenu." + Guid.NewGuid().ToString("N");
        var submenuKeyPath = keyPath + ".Commands";
        var legacyOrganizeKeyPath = keyPath + ".Organize";
        var registration = new DesktopContextMenuRegistration(
            Registry.CurrentUser,
            keyPath,
            submenuClassName,
            submenuKeyPath,
            legacyOrganizeKeyPath);
        var executable = Path.Combine(Path.GetTempPath(), "CrabDesk.WinUI.exe");
        try
        {
            registration.SetEnabled(true, executable);

            Assert.True(registration.IsEnabled);
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            using var submenuKey = Registry.CurrentUser.OpenSubKey(submenuKeyPath);
            using var rootCommand = key?.OpenSubKey("command");
            using var createBoxCommand = submenuKey?.OpenSubKey(@"shell\01CreateBox\command");
            using var settingsCommand = submenuKey?.OpenSubKey(@"shell\02Settings\command");
            using var organizeCommand = submenuKey?.OpenSubKey(@"shell\03RuleOrganize\command");
            using var aiOrganizeCommand = submenuKey?.OpenSubKey(@"shell\04AiOrganize\command");
            Assert.Equal("CrabDesk", key?.GetValue(null));
            Assert.Null(key?.GetValue("SubCommands"));
            Assert.Equal(submenuClassName, key?.GetValue("ExtendedSubCommandsKey"));
            Assert.Equal($"\"{Path.GetFullPath(executable)}\" --show-settings", rootCommand?.GetValue(null));
            Assert.Equal($"\"{Path.GetFullPath(executable)}\" --create-box", createBoxCommand?.GetValue(null));
            Assert.Equal($"\"{Path.GetFullPath(executable)}\" --show-settings", settingsCommand?.GetValue(null));
            Assert.Equal($"\"{Path.GetFullPath(executable)}\" --organize", organizeCommand?.GetValue(null));
            Assert.Equal($"\"{Path.GetFullPath(executable)}\" --ai-organize", aiOrganizeCommand?.GetValue(null));

            registration.SetEnabled(false, executable);
            Assert.False(registration.IsEnabled);
            Assert.Null(Registry.CurrentUser.OpenSubKey(submenuKeyPath));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, false);
            Registry.CurrentUser.DeleteSubKeyTree(submenuKeyPath, false);
            Registry.CurrentUser.DeleteSubKeyTree(legacyOrganizeKeyPath, false);
        }
    }

    [Fact]
    public async Task GlobalHotkeyDetectsConflictAndReleasesRegistration()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var completion = new TaskCompletionSource<(
            HotkeyRegistrationStatus First,
            HotkeyRegistrationStatus Conflict,
            HotkeyRegistrationStatus AfterRelease)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var first = new GlobalHotkeyService();
                using var second = new GlobalHotkeyService();
                var binding = new HotkeyBinding
                {
                    Enabled = true,
                    Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift
                };
                var firstStatus = HotkeyRegistrationStatus.Conflict;
                foreach (var key in Enum.GetValues<HotkeyKey>().Reverse())
                {
                    binding.Key = key;
                    firstStatus = first.Register(HotkeyAction.ShowDesktop, binding);
                    if (firstStatus == HotkeyRegistrationStatus.Registered)
                    {
                        break;
                    }
                }
                if (firstStatus != HotkeyRegistrationStatus.Registered)
                {
                    completion.SetResult((firstStatus, HotkeyRegistrationStatus.Failed, HotkeyRegistrationStatus.Failed));
                    return;
                }

                var conflict = second.Register(HotkeyAction.ShowDesktop, binding);
                first.Unregister(HotkeyAction.ShowDesktop);
                var afterRelease = second.Register(HotkeyAction.ShowDesktop, binding);
                completion.SetResult((firstStatus, conflict, afterRelease));
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        thread.Join(TimeSpan.FromSeconds(2));

        Assert.Equal(HotkeyRegistrationStatus.Registered, result.First);
        Assert.Equal(HotkeyRegistrationStatus.Conflict, result.Conflict);
        Assert.Equal(HotkeyRegistrationStatus.Registered, result.AfterRelease);
    }

    [Fact]
    public void ExplorerDesktopHostCanBeLocated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var host = new DesktopHostService();
        host.Refresh();

        Assert.NotEqual(IntPtr.Zero, host.DesktopParent);
        Assert.NotEqual(IntPtr.Zero, host.DesktopView);
    }

    [Fact]
    public async Task DesktopProviderReturnsShellItems()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var provider = new DesktopItemProvider();
        var items = await provider.EnumerateAsync();

        Assert.Contains(items, item => item.ParsingName.Contains("645FF040", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(items, item => item.ParsingName.Contains("20D04FE0", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DesktopFileIdentityRemainsStableAcrossRename()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var stem = $"CrabDeskIdentityTest-{Guid.NewGuid():N}";
        var originalPath = Path.Combine(desktop, stem + ".txt");
        var renamedPath = Path.Combine(desktop, stem + "-renamed.txt");
        try
        {
            await File.WriteAllTextAsync(originalPath, "CrabDesk stable identity test");
            using var provider = new DesktopItemProvider();
            var before = Assert.Single((await provider.EnumerateAsync()).Where(item =>
                string.Equals(item.FileSystemPath, originalPath, StringComparison.OrdinalIgnoreCase)));

            File.Move(originalPath, renamedPath);
            var after = Assert.Single((await provider.EnumerateAsync()).Where(item =>
                string.Equals(item.FileSystemPath, renamedPath, StringComparison.OrdinalIgnoreCase)));

            Assert.Equal(before.Key, after.Key);
            Assert.NotEqual(before.FileSystemPath, after.FileSystemPath);
        }
        finally
        {
            File.Delete(originalPath);
            File.Delete(renamedPath);
        }
    }
}
