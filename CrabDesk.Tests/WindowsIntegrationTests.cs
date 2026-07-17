using CrabDesk.Core;
using CrabDesk.Native;
using System.Diagnostics;
using System.Text.Json;
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
            using var createBoxCommand = submenuKey?.OpenSubKey(@"shell\02CreateBox\command");
            using var organizeCommand = submenuKey?.OpenSubKey(@"shell\03Organize\command");
            using var openCommand = submenuKey?.OpenSubKey(@"shell\01Open\command");
            Assert.Equal("CrabDesk", key?.GetValue(null));
            Assert.Null(key?.GetValue("SubCommands"));
            Assert.Equal(submenuClassName, key?.GetValue("ExtendedSubCommandsKey"));
            Assert.Equal($"\"{Path.GetFullPath(executable)}\" --create-box", createBoxCommand?.GetValue(null));
            Assert.Equal($"\"{Path.GetFullPath(executable)}\" --organize", organizeCommand?.GetValue(null));
            Assert.Equal($"\"{Path.GetFullPath(executable)}\"", openCommand?.GetValue(null));

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

    [Fact]
    public async Task AssignedDesktopIconPositionCanBeCapturedMovedAndRestored()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var stem = $"CrabDeskMoveTest-{Guid.NewGuid():N}";
        var path = Path.Combine(desktop, stem + ".txt");
        await File.WriteAllTextAsync(path, "CrabDesk icon position test");
        DesktopIconPositionSnapshot? original = null;
        var host = new DesktopHostService();
        try
        {
            host.Refresh();
            for (var attempt = 0; attempt < 20 && original is null; attempt++)
            {
                await Task.Delay(250);
                original = DesktopIconPositionService.CaptureItemPositions(
                    host.DesktopListView,
                    [stem, stem + ".txt"]).FirstOrDefault();
                if (string.IsNullOrWhiteSpace(original.Value.DisplayName))
                {
                    original = null;
                }
            }
            Assert.NotNull(original);

            var moved = 0;
            for (var attempt = 0; attempt < 20 && moved == 0; attempt++)
            {
                await Task.Delay(250);
                moved = DesktopIconPositionService.MoveItemsUnderBox(
                    host.DesktopListView,
                    [stem, stem + ".txt"],
                    640,
                    420);
            }

            Assert.True(moved > 0, "Explorer did not expose the new desktop item to the list view.");
            var restored = DesktopIconPositionService.RestoreItemPositions(
                host.DesktopListView,
                [original.Value]);
            Assert.True(restored > 0, "Explorer did not restore the desktop item position.");

            var final = DesktopIconPositionService.CaptureItemPositions(
                host.DesktopListView,
                [stem, stem + ".txt"]).Single();
            Assert.Equal(original.Value.X, final.X);
            Assert.Equal(original.Value.Y, final.Y);
        }
        finally
        {
            if (original is not null)
            {
                DesktopIconPositionService.RestoreItemPositions(host.DesktopListView, [original.Value]);
            }
            File.Delete(path);
        }
    }

    [Fact]
    public async Task NamedAssignedIconPlacementsLeaveUnclassifiedIconUntouched()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var prefix = $"CrabDeskPlacementTest-{Guid.NewGuid():N}";
        var firstStem = prefix + "-Z";
        var secondStem = prefix + "-A";
        var unclassifiedStem = prefix + "-U";
        var paths = new[] { firstStem, secondStem, unclassifiedStem }
            .Select(stem => Path.Combine(desktop, stem + ".txt"))
            .ToArray();
        foreach (var path in paths)
        {
            await File.WriteAllTextAsync(path, "CrabDesk named placement test");
        }
        await Task.Delay(1000);

        var originals = new List<DesktopIconPositionSnapshot>();
        var host = new DesktopHostService();
        try
        {
            host.Refresh();
            foreach (var stem in new[] { firstStem, secondStem, unclassifiedStem })
            {
                DesktopIconPositionSnapshot? original = null;
                for (var attempt = 0; attempt < 40 && original is null; attempt++)
                {
                    await Task.Delay(250);
                    var captured = DesktopIconPositionService.CaptureItemPositions(
                        host.DesktopListView,
                        [stem, stem + ".txt"]).FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(captured.DisplayName))
                    {
                        original = captured;
                    }
                }
                Assert.NotNull(original);
                originals.Add(original.Value);
            }
            var originalUnclassified = originals.Single(snapshot =>
                snapshot.DisplayName.Contains(unclassifiedStem, StringComparison.OrdinalIgnoreCase));
            var listViewBounds = DesktopWindowTools.GetWindowBounds(host.DesktopListView);
            var firstScreenX = (int)listViewBounds.X + 620;
            var firstScreenY = (int)listViewBounds.Y + 410;
            var secondScreenX = firstScreenX + 170;
            var secondScreenY = firstScreenY + 100;

            var moved = DesktopIconPositionService.MoveItemsUnderBox(
                host.DesktopListView,
                [
                    new DesktopIconPlacement([firstStem, firstStem + ".txt"], firstScreenX, firstScreenY),
                    new DesktopIconPlacement([secondStem, secondStem + ".txt"], secondScreenX, secondScreenY)
                ]);
            Assert.Equal(2, moved);

            var first = DesktopIconPositionService.CaptureItemPositions(
                host.DesktopListView,
                [firstStem, firstStem + ".txt"]).Single();
            var second = DesktopIconPositionService.CaptureItemPositions(
                host.DesktopListView,
                [secondStem, secondStem + ".txt"]).Single();
            var unclassified = DesktopIconPositionService.CaptureItemPositions(
                host.DesktopListView,
                [unclassifiedStem, unclassifiedStem + ".txt"]).Single();
            var spacing = DesktopIconPositionService.GetItemSpacing(host.DesktopListView);
            var horizontalTolerance = Math.Max(24, spacing.Width);
            var verticalTolerance = Math.Max(24, spacing.Height);
            Assert.InRange(first.X, 620 - horizontalTolerance, 620 + horizontalTolerance);
            Assert.InRange(first.Y, 410 - verticalTolerance, 410 + verticalTolerance);
            Assert.InRange(second.X, 790 - horizontalTolerance, 790 + horizontalTolerance);
            Assert.InRange(second.Y, 510 - verticalTolerance, 510 + verticalTolerance);
            Assert.Equal(originalUnclassified.X, unclassified.X);
            Assert.Equal(originalUnclassified.Y, unclassified.Y);
        }
        finally
        {
            if (originals.Count > 0)
            {
                DesktopIconPositionService.RestoreItemPositions(host.DesktopListView, originals);
            }
            foreach (var path in paths)
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task ExplorerRetainsIconInsideExtendedOffscreenWorkArea()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var stem = $"CrabDeskOffscreenTest-{Guid.NewGuid():N}";
        var path = Path.Combine(desktop, stem + ".txt");
        await File.WriteAllTextAsync(path, "CrabDesk offscreen position probe");
        DesktopIconPositionSnapshot? original = null;
        IReadOnlyList<System.Drawing.Rectangle>? originalWorkAreas = null;
        var host = new DesktopHostService();
        try
        {
            host.Refresh();
            for (var attempt = 0; attempt < 20 && original is null; attempt++)
            {
                await Task.Delay(250);
                var captured = DesktopIconPositionService.CaptureItemPositions(
                    host.DesktopListView,
                    [stem, stem + ".txt"]).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(captured.DisplayName))
                {
                    original = captured;
                }
            }
            Assert.NotNull(original);

            var bounds = DesktopWindowTools.GetWindowBounds(host.DesktopListView);
            originalWorkAreas = DesktopIconPositionService.GetWorkAreas(host.DesktopListView);
            var extendedWorkArea = new System.Drawing.Rectangle(
                0,
                0,
                (int)bounds.Width + 8192,
                (int)bounds.Height + 8192);
            Assert.True(DesktopIconPositionService.SetWorkAreas(
                host.DesktopListView,
                [extendedWorkArea]));
            var requestedX = (int)(bounds.X + bounds.Width + 4096);
            var requestedY = (int)(bounds.Y + bounds.Height + 4096);
            Assert.Equal(1, DesktopIconPositionService.MoveItemsUnderBox(
                host.DesktopListView,
                [new DesktopIconPlacement([stem, stem + ".txt"], requestedX, requestedY)]));

            await Task.Delay(3000);
            var actual = DesktopIconPositionService.CaptureItemPositions(
                host.DesktopListView,
                [stem, stem + ".txt"]).Single();
            Assert.True(
                actual.X >= bounds.Width || actual.Y >= bounds.Height,
                $"Explorer reported ({actual.X}, {actual.Y}) inside {bounds.Width}x{bounds.Height} instead of the requested offscreen position.");
        }
        finally
        {
            if (original is not null)
            {
                DesktopIconPositionService.RestoreItemPositions(host.DesktopListView, [original.Value]);
            }
            if (originalWorkAreas is not null)
            {
                DesktopIconPositionService.SetWorkAreas(host.DesktopListView, originalWorkAreas);
            }
            File.Delete(path);
        }
    }

    [Fact]
    public async Task IconGuardRestoresDesktopIconPositionAfterForcedExit()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var guardPath = Path.Combine(AppContext.BaseDirectory, "CrabDesk.IconGuard.exe");
        Assert.True(File.Exists(guardPath), $"IconGuard executable was not copied to {guardPath}.");

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var stem = $"CrabDeskGuardTest-{Guid.NewGuid():N}";
        var path = Path.Combine(desktop, stem + ".txt");
        var marker = Path.Combine(Path.GetTempPath(), stem + ".json");
        await File.WriteAllTextAsync(path, "CrabDesk guard recovery test");

        DesktopIconPositionSnapshot? original = null;
        IReadOnlyList<System.Drawing.Rectangle>? originalWorkAreas = null;
        var host = new DesktopHostService();
        try
        {
            host.Refresh();
            for (var attempt = 0; attempt < 20 && original is null; attempt++)
            {
                await Task.Delay(250);
                var captured = DesktopIconPositionService.CaptureItemPositions(
                    host.DesktopListView,
                    [stem, stem + ".txt"]).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(captured.DisplayName))
                {
                    original = captured;
                }
            }
            Assert.NotNull(original);

            originalWorkAreas = DesktopIconPositionService.GetWorkAreas(host.DesktopListView);
            var listViewBounds = DesktopWindowTools.GetWindowBounds(host.DesktopListView);
            Assert.True(DesktopIconPositionService.SetWorkAreas(
                host.DesktopListView,
                [new System.Drawing.Rectangle(
                    0,
                    0,
                    (int)listViewBounds.Width + 8192,
                    (int)listViewBounds.Height + 8192)]));

            Assert.True(DesktopIconPositionService.MoveItemsUnderBox(
                host.DesktopListView,
                [stem, stem + ".txt"],
                720,
                500) > 0);

            var previousHidden = new ExplorerIconVisibility().GetIconsHidden();
            await File.WriteAllTextAsync(marker, JsonSerializer.Serialize(new DesktopRecoveryState
            {
                PreviousHidden = previousHidden,
                IconPositions = [original.Value],
                WorkAreas = originalWorkAreas.Select(area => new DesktopWorkAreaSnapshot(
                    area.Left,
                    area.Top,
                    area.Right,
                    area.Bottom)).ToList()
            }));

            using var guard = Process.Start(new ProcessStartInfo(guardPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                Arguments = $"{int.MaxValue} {previousHidden} \"{marker}\""
            });
            Assert.NotNull(guard);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await guard.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, guard.ExitCode);

            var final = DesktopIconPositionService.CaptureItemPositions(
                host.DesktopListView,
                [stem, stem + ".txt"]).Single();
            Assert.Equal(original.Value.X, final.X);
            Assert.Equal(original.Value.Y, final.Y);
            Assert.Equal(
                originalWorkAreas,
                DesktopIconPositionService.GetWorkAreas(host.DesktopListView));
            Assert.False(File.Exists(marker));
        }
        finally
        {
            if (original is not null)
            {
                DesktopIconPositionService.RestoreItemPositions(host.DesktopListView, [original.Value]);
            }
            if (originalWorkAreas is not null)
            {
                DesktopIconPositionService.SetWorkAreas(host.DesktopListView, originalWorkAreas);
            }
            File.Delete(path);
            File.Delete(marker);
        }
    }
}
