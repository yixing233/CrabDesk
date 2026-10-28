using System.Reflection;
using System.Runtime.CompilerServices;
using CrabDesk.Core;
using CrabDesk.Native;
using CrabDesk.Runtime;
using CrabDesk.WinUI.Services;
using CrabDesk.WinUI.ViewModels;
using Microsoft.Win32;
using System.Collections;
using System.Drawing;
using System.Runtime.ExceptionServices;
using Moq;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class PreferenceFunctionalityTests
{
    [Fact]
    public async Task PreferencesAreSavedWhenAReaderBrieflyLocksTheConfiguration()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CrabDesk-pref-lock-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new JsonLayoutStore(directory);
            await store.SaveAsync(new CrabDeskState());
            var updated = new CrabDeskState();
            updated.Settings.DesktopBehavior.LaunchToTray = true;
            Task saving;
            using (var reader = new FileStream(store.StatePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                saving = store.SaveAsync(updated);
                await Task.Delay(100);
            }
            await saving.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True((await store.LoadAsync()).Settings.DesktopBehavior.LaunchToTray);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(WatcherChangeTypes.Created)]
    [InlineData(WatcherChangeTypes.Deleted)]
    [InlineData(WatcherChangeTypes.Changed)]
    [InlineData(WatcherChangeTypes.Renamed)]
    [InlineData(WatcherChangeTypes.All)]
    public void EnabledAutomaticRefreshProcessesExternalFileNotifications(WatcherChangeTypes change)
    {
        using var fixture = new RuntimeFixture();
        fixture.Runtime.SetRefreshAfterRename(true);
        Invoke(fixture.Runtime, "OnDesktopItemsChanged", null!, new FileSystemEventArgs(change, @"C:\Desktop", "test.txt"));
        fixture.Provider.Verify(provider => provider.EnumerateAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExplicitRefreshWorksWhenAutomaticRefreshIsDisabled()
    {
        using var fixture = new RuntimeFixture();
        fixture.Runtime.SetRefreshAfterRename(false);
        await fixture.Runtime.RefreshItemsAsync();
        fixture.Provider.Verify(provider => provider.EnumerateAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void StartupRegistrationWritesAValidCommandAndPreservesTheInstalledPathDuringMigration()
    {
        var keyPath = @"Software\CrabDesk\Tests\Startup-" + Guid.NewGuid().ToString("N");
        var directory = Path.Combine(Path.GetTempPath(), "CrabDesk startup " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var executable = Path.Combine(directory, "CrabDesk.WinUI.exe");
        File.WriteAllText(executable, "test fixture, never executed");
        try
        {
            StartupRegistration.SetEnabled(true, Registry.CurrentUser, keyPath, executable);
            Assert.True(StartupRegistration.IsEnabled(Registry.CurrentUser, keyPath));
            using (var key = Registry.CurrentUser.OpenSubKey(keyPath, true))
            {
                Assert.Equal($"\"{executable}\"", key!.GetValue("CrabDesk"));
                key.SetValue("CrabDesk", $"\"{executable}\" --background");
            }
            StartupRegistration.NormalizeLaunchCommand(Registry.CurrentUser, keyPath);
            using (var key = Registry.CurrentUser.OpenSubKey(keyPath))
                Assert.Equal($"\"{executable}\"", key!.GetValue("CrabDesk"));
            File.Delete(executable);
            Assert.False(StartupRegistration.IsEnabled(Registry.CurrentUser, keyPath));
            StartupRegistration.SetEnabled(false, Registry.CurrentUser, keyPath);
            Assert.False(StartupRegistration.IsEnabled(Registry.CurrentUser, keyPath));
            using var disabled = Registry.CurrentUser.OpenSubKey(keyPath);
            Assert.Null(disabled!.GetValue("CrabDesk"));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(keyPath, false); Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task HiddenSystemAndOfficeFilesRespectThePreferenceInDesktopAndMappedBoxes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CrabDesk-file-visibility-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var name in new[] { "normal.txt", "hidden.txt", "system.txt", "~$document.docx", "desktop.ini" })
                await File.WriteAllTextAsync(Path.Combine(directory, name), "test");
            File.SetAttributes(Path.Combine(directory, "hidden.txt"), FileAttributes.Hidden);
            File.SetAttributes(Path.Combine(directory, "system.txt"), FileAttributes.System);
            using var desktop = new DesktopItemProvider([directory]);
            using var mapped = new MappedFolderProvider();
            foreach (var enabled in new[] { false, true, false })
            {
                desktop.ShowHiddenFiles = mapped.ShowHiddenFiles = enabled;
                var desktopFiles = (await desktop.EnumerateAsync()).Where(item => item.FileSystemPath is not null)
                    .Select(item => Path.GetFileName(item.FileSystemPath)).Order().ToArray();
                var mappedFiles = (await mapped.EnumerateAsync(directory)).Items
                    .Select(item => Path.GetFileName(item.FileSystemPath)).Order().ToArray();
                var expected = (enabled ? new[] { "normal.txt", "hidden.txt", "system.txt", "~$document.docx" } : new[] { "normal.txt" }).Order().ToArray();
                Assert.Equal(expected, desktopFiles);
                Assert.Equal(expected, mappedFiles);
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task DesktopWatcherReportsARealNewFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CrabDesk-watcher-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var provider = new DesktopItemProvider([directory]);
            var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            provider.ItemsChanged += (_, _) => notified.TrySetResult();
            var path = Path.Combine(directory, "new.txt");
            await File.WriteAllTextAsync(path, "test");
            await notified.Task.WaitAsync(TimeSpan.FromSeconds(4));
            Assert.Contains(await provider.EnumerateAsync(), item => item.FileSystemPath == path);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ScrollingHonorsAnimationPreferenceAndFinishesWhenDisabled(bool enabled)
    {
        OnSta(() =>
        {
            using var fixture = new RuntimeFixture();
            fixture.Runtime.State.Settings.Appearance.AnimationEnabled = enabled;
            var monitor = new MonitorLayout { Id = "pref", DeviceName = "pref", DpiScale = 1,
                Bounds = new(0, 0, 400, 300), WorkArea = new(0, 0, 400, 300), PixelBounds = new(0, 0, 400, 300), PixelWorkArea = new(0, 0, 400, 300) };
            using var surface = new DesktopBoxForm(fixture.Runtime, monitor);
            var keyType = typeof(DesktopBoxForm).GetNestedType("ItemViewKey", BindingFlags.NonPublic)!;
            var key = Activator.CreateInstance(keyType, Guid.NewGuid(), "all")!;
            Invoke(surface, "StartScrollAnimation", key, 0d, 80d);
            Assert.Equal(enabled, Get(surface, "_scrollAnimationKey") is not null);
            if (enabled)
            {
                fixture.Runtime.State.Settings.Appearance.AnimationEnabled = false;
                Assert.True((bool)Invoke(surface, "AdvanceScrollAnimation", false)!);
                Assert.Null(Get(surface, "_scrollAnimationKey"));
            }
            var offsets = (IDictionary)Get(surface, "_scrollOffsets")!;
            Assert.Equal(80d, offsets[key]);
        });
    }

    [Fact]
    public void ResidentTrayIconExistsAndItsMenuCommandsWork()
    {
        OnSta(() =>
        {
            var settings = 0; var widgets = 0; var paused = 0; var exited = 0;
            using var tray = new CrabDeskTrayIcon(() => settings++, () => widgets++, () => paused++, () => exited++, () => false, _ => { });
            Assert.True(tray.Visible);
            tray.Menu.Items[0].PerformClick();
            tray.Menu.Items[1].PerformClick();
            tray.Menu.Items[2].PerformClick();
            tray.Menu.Items[4].PerformClick();
            Assert.Equal((1, 1, 1, 1), (settings, widgets, paused, exited));
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void DesktopBoxMenuAlsoHonorsDeleteConfirmation(bool confirm, bool accepted)
    {
        OnSta(() =>
        {
            using var fixture = new RuntimeFixture();
            fixture.Runtime.State.Settings.ConfirmDeleteBox = confirm;
            var box = new DesktopBox { Title = "test", MonitorId = "pref", Bounds = new(0, 0, 180, 180) };
            fixture.Runtime.State.Boxes.Add(box);
            var confirmations = 0;
            fixture.Runtime.DesktopConfirmationHandler = _ => { confirmations++; return Task.FromResult(accepted); };
            var monitor = new MonitorLayout { Id = "pref", DeviceName = "pref", DpiScale = 1,
                Bounds = new(0, 0, 400, 300), WorkArea = new(0, 0, 400, 300), PixelBounds = new(0, 0, 400, 300), PixelWorkArea = new(0, 0, 400, 300) };
            using var surface = new DesktopBoxForm(fixture.Runtime, monitor);
            using var menu = (System.Windows.Forms.ContextMenuStrip)Invoke(surface, "BuildBoxMenu", box)!;
            var delete = menu.Items.Cast<System.Windows.Forms.ToolStripItem>().Single(item => item.Text == "删除盒子");
            delete.PerformClick();
            Assert.Equal(confirm ? 1 : 0, confirmations);
            Assert.Equal(confirm && !accepted, fixture.Runtime.State.Boxes.Contains(box));
        });
    }

    [Theory]
    [InlineData(WatcherChangeTypes.Created)]
    [InlineData(WatcherChangeTypes.Deleted)]
    [InlineData(WatcherChangeTypes.Changed)]
    [InlineData(WatcherChangeTypes.Renamed)]
    [InlineData(WatcherChangeTypes.All)]
    public void DisabledAutomaticRefreshIgnoresExternalFileNotifications(WatcherChangeTypes change)
    {
        using var fixture = new RuntimeFixture();
        fixture.Runtime.SetRefreshAfterRename(false);
        Invoke(fixture.Runtime, "OnDesktopItemsChanged", null!, new FileSystemEventArgs(change, @"C:\Desktop", "test.txt"));
        fixture.Provider.Verify(provider => provider.EnumerateAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SystemItemVisibilityAppliesToAssignedIconsWithoutLosingTheirGrouping(bool enabled)
    {
        using var fixture = new RuntimeFixture();
        var system = Item("computer", system: true);
        var file = Item("note.txt");
        var box = new DesktopBox { Title = "system items" };
        fixture.Runtime.State.Boxes.Add(box);
        fixture.Runtime.State.Assignments[system.Key.ToString()] = box.Id;
        fixture.Provider.Setup(provider => provider.EnumerateAsync(It.IsAny<CancellationToken>())).ReturnsAsync([system, file]);
        await fixture.Runtime.SetShowSystemItemsAsync(enabled);
        Assert.Equal(enabled, fixture.Runtime.Items.Any(item => item.IsSystem));
        Assert.Contains(file, fixture.Runtime.Items);
        Assert.Contains(system.Key.ToString(), fixture.Runtime.State.Assignments.Keys);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DeleteConfirmationAcceptCancelAndDisabledAreEffective(bool confirm, bool accepted)
    {
        var state = new CrabDeskState();
        state.Settings.ConfirmDeleteBox = confirm;
        state.Boxes.Add(new DesktopBox { Title = "test" });
        var service = new Mock<ICrabDeskService>();
        service.SetupGet(item => item.State).Returns(state);
        service.SetupGet(item => item.Boxes).Returns(state.Boxes);
        var dialogs = new Mock<IDialogService>();
        dialogs.Setup(item => item.ConfirmAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(accepted);
        var viewModel = new BoxesViewModel(service.Object, dialogs.Object, Mock.Of<IFilePickerService>());
        await viewModel.DeleteCommand.ExecuteAsync(null);
        dialogs.Verify(item => item.ConfirmAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), confirm ? Times.Once() : Times.Never());
        service.Verify(item => item.DeleteBox(It.IsAny<DesktopBox>()), !confirm || accepted ? Times.Once() : Times.Never());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PreferencesSurviveSavingAndReloading(bool enabled)
    {
        var directory = Path.Combine(Path.GetTempPath(), "CrabDesk-preferences-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var fixture = new RuntimeFixture();
            fixture.Runtime.SetConfirmDeleteBox(enabled);
            fixture.Runtime.SetLaunchToTray(enabled);
            fixture.Runtime.SetRefreshAfterRename(enabled);
            fixture.Runtime.SetAnimationEnabled(enabled);
            await fixture.Runtime.SetShowSystemItemsAsync(enabled);
            await fixture.Runtime.SetShowHiddenFilesAsync(enabled);
            var store = new JsonLayoutStore(directory);
            await store.SaveAsync(fixture.Runtime.State);
            var restored = await store.LoadAsync();
            Assert.Equal(enabled, restored.Settings.ConfirmDeleteBox);
            Assert.Equal(enabled, restored.Settings.DesktopBehavior.LaunchToTray);
            Assert.Equal(enabled, restored.Settings.DesktopBehavior.RefreshAfterRename);
            Assert.Equal(enabled, restored.Settings.Appearance.AnimationEnabled);
            Assert.Equal(enabled, restored.Settings.ShowSystemItems);
            Assert.Equal(enabled, restored.Settings.ShowHiddenFiles);
            fixture.Provider.VerifySet(provider => provider.ShowHiddenFiles = enabled, Times.Once);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static DesktopItemRef Item(string name, bool system = false) => new()
    {
        Key = new DesktopItemKey(system ? "shell" : "file", name), DisplayName = name,
        ParsingName = system ? "shell:::" + name : @"C:\Desktop\" + name,
        FileSystemPath = system ? null : @"C:\Desktop\" + name,
        Kind = system ? DesktopItemKind.Shell : DesktopItemKind.File
    };

    private sealed class RuntimeFixture : IDisposable
    {
        internal CrabDeskRuntime Runtime { get; } = (CrabDeskRuntime)RuntimeHelpers.GetUninitializedObject(typeof(CrabDeskRuntime));
        internal Mock<IDesktopItemProvider> Provider { get; } = new();
        private readonly RuntimeTimer _save = new(TimeSpan.FromHours(1), false, action => action(), () => { });
        private readonly SemaphoreSlim _mappedLock = new(1, 1);
        internal RuntimeFixture()
        {
            Set(Runtime, "<State>k__BackingField", new CrabDeskState());
            Set(Runtime, "<Items>k__BackingField", Array.Empty<DesktopItemRef>());
            Set(Runtime, "<Monitors>k__BackingField", Array.Empty<MonitorLayout>());
            Set(Runtime, "_allDesktopItems", Array.Empty<DesktopItemRef>());
            Set(Runtime, "_itemProvider", Provider.Object);
            Set(Runtime, "_fileOperations", Mock.Of<IFileOperationService>());
            Provider.Setup(provider => provider.EnumerateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<DesktopItemRef>());
            Set(Runtime, "_mappedFolderProvider", Mock.Of<IMappedFolderProvider>());
            Set(Runtime, "_mappedFolderSnapshots", new Dictionary<Guid, MappedFolderSnapshot>());
            Set(Runtime, "_mappedRefreshLock", _mappedLock);
            Set(Runtime, "_saveTimer", _save);
            Set(Runtime, "_targetedDesktopRefreshPaths", new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }
        public void Dispose() { _save.Dispose(); _mappedLock.Dispose(); }
    }
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);
    private static object? Invoke(object target, string name, params object[] values) => target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, values);
    private static object? Get(object target, string name) => target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target);
    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception exception) { error = exception; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
