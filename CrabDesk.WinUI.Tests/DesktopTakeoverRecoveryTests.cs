using System.Reflection;
using System.Runtime.CompilerServices;
using CrabDesk.Core;
using CrabDesk.Native;
using CrabDesk.Runtime;
using Moq;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class DesktopTakeoverRecoveryTests
{
    [Fact]
    public async Task ReconnectRetriesAFailedTakeoverAfterAutomaticRetriesAreExhausted()
    {
        using var fixture = new RecoveryFixture();
        fixture.FailTakeover(retryAttempt: 6);

        await fixture.Runtime.ReconnectDesktopAsync();

        // There are deliberately no monitors: a real activation must fail and
        // schedule its first retry, rather than silently skipping the attempt.
        Assert.True(fixture.Runtime.IsPaused);
        Assert.True(fixture.Runtime.State.Settings.TakeOverDesktop);
        Assert.True(Get<bool>(fixture.Runtime, "_pausedByTakeoverFailure"));
        Assert.Equal(1, Get<int>(fixture.Runtime, "_takeoverRetryAttempt"));
        fixture.Input.VerifySet(input => input.Enabled = true, Times.Once);
        Assert.True(fixture.Notifications > 0);
    }

    [Fact]
    public async Task ReconnectDoesNotResumeAUserPausedDesktop()
    {
        using var fixture = new RecoveryFixture();
        Set(fixture.Runtime, "<IsPaused>k__BackingField", true);
        fixture.Runtime.State.Settings.TakeOverDesktop = false;

        await fixture.Runtime.ReconnectDesktopAsync();

        Assert.True(fixture.Runtime.IsPaused);
        Assert.False(fixture.Runtime.State.Settings.TakeOverDesktop);
        Assert.False(Get<bool>(fixture.Runtime, "_pausedByTakeoverFailure"));
        Assert.Equal(0, Get<int>(fixture.Runtime, "_takeoverRetryAttempt"));
        fixture.Input.VerifySet(input => input.Enabled = true, Times.Never);
    }

    [Fact]
    public void ResumeRefreshesTheDesktopAndStartsANewRetryBudget()
    {
        using var fixture = new RecoveryFixture();
        fixture.FailTakeover(retryAttempt: 6);

        fixture.Runtime.SetPaused(false);

        fixture.Monitors.Verify(service => service.GetMonitors(), Times.Once);
        Assert.True(fixture.Runtime.IsPaused);
        Assert.Equal(1, Get<int>(fixture.Runtime, "_takeoverRetryAttempt"));
        Assert.True(fixture.Runtime.State.Settings.TakeOverDesktop);
    }

    [Fact]
    public void ExplicitPauseCancelsFailureRecoveryEvenWhenAlreadyPaused()
    {
        using var fixture = new RecoveryFixture();
        fixture.FailTakeover(retryAttempt: 1);
        fixture.RetryTimer.Start(TimeSpan.FromHours(1));

        fixture.Runtime.SetPaused(true);

        Assert.True(fixture.Runtime.IsPaused);
        Assert.False(fixture.Runtime.State.Settings.TakeOverDesktop);
        Assert.False(Get<bool>(fixture.Runtime, "_pausedByTakeoverFailure"));
        Assert.Null(Get<object?>(fixture.RetryTimer, "_timer"));
        Assert.True(fixture.Notifications > 0);
    }

    [Fact]
    public void AutomaticRetryRefreshesTheHostAndMonitorTopology()
    {
        using var fixture = new RecoveryFixture();
        fixture.FailTakeover(retryAttempt: 1);

        Call(fixture.Runtime, "OnTakeoverRetryTick");

        fixture.Monitors.Verify(service => service.GetMonitors(), Times.Once);
        Assert.True(fixture.Runtime.IsPaused);
        Assert.Equal(2, Get<int>(fixture.Runtime, "_takeoverRetryAttempt"));
    }

    [Fact]
    public void QueuedRetryDoesNotReactivateADisposedRuntime()
    {
        using var fixture = new RecoveryFixture();
        fixture.FailTakeover(retryAttempt: 1);
        Set(fixture.Runtime, "_disposed", true);

        Call(fixture.Runtime, "OnTakeoverRetryTick");

        Assert.Equal(1, Get<int>(fixture.Runtime, "_takeoverRetryAttempt"));
        fixture.Monitors.Verify(service => service.GetMonitors(), Times.Never);
    }

    private sealed class RecoveryFixture : IDisposable
    {
        internal CrabDeskRuntime Runtime { get; } = (CrabDeskRuntime)RuntimeHelpers.GetUninitializedObject(typeof(CrabDeskRuntime));
        internal Mock<IMonitorTopologyService> Monitors { get; } = new();
        internal Mock<IDesktopInputMonitor> Input { get; } = new();
        internal RuntimeTimer RetryTimer { get; } = new(TimeSpan.FromHours(1), false, _ => { }, () => { });
        private readonly RuntimeTimer _saveTimer = new(TimeSpan.FromHours(1), false, _ => { }, () => { });
        private readonly SemaphoreSlim _mappedRefreshLock = new(1, 1);
        internal int Notifications { get; private set; }

        internal RecoveryFixture()
        {
            Monitors.Setup(service => service.GetMonitors()).Returns(Array.Empty<MonitorLayout>());
            var items = new Mock<IDesktopItemProvider>();
            items.Setup(provider => provider.EnumerateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<DesktopItemRef>());
            Set(Runtime, "<State>k__BackingField", new CrabDeskState());
            Set(Runtime, "<Items>k__BackingField", Array.Empty<DesktopItemRef>());
            Set(Runtime, "<Monitors>k__BackingField", Array.Empty<MonitorLayout>());
            Set(Runtime, "_allDesktopItems", Array.Empty<DesktopItemRef>());
            Set(Runtime, "_desktopHost", new DesktopHostService());
            Set(Runtime, "_monitorService", Monitors.Object);
            Set(Runtime, "_desktopInputMonitor", Input.Object);
            Set(Runtime, "_itemProvider", items.Object);
            Set(Runtime, "_mappedFolderProvider", Mock.Of<IMappedFolderProvider>());
            Set(Runtime, "_mappedFolderSnapshots", new Dictionary<Guid, MappedFolderSnapshot>());
            Set(Runtime, "_mappedRefreshLock", _mappedRefreshLock);
            Set(Runtime, "_originalFileAttributes", new Dictionary<string, FileAttributes>());
            Set(Runtime, "_hiddenShellIconOriginals", new Dictionary<string, int?>());
            Set(Runtime, "_takeoverRetryTimer", RetryTimer);
            Set(Runtime, "_saveTimer", _saveTimer);
            Runtime.Changed += (_, _) => Notifications++;
        }

        internal void FailTakeover(int retryAttempt)
        {
            Runtime.State.Settings.TakeOverDesktop = true;
            Set(Runtime, "<IsPaused>k__BackingField", true);
            Set(Runtime, "_pausedByTakeoverFailure", true);
            Set(Runtime, "_takeoverRetryAttempt", retryAttempt);
        }

        public void Dispose()
        {
            RetryTimer.Dispose();
            _saveTimer.Dispose();
            _mappedRefreshLock.Dispose();
        }
    }

    private static void Set(object instance, string name, object value) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);
    private static T Get<T>(object instance, string name) =>
        (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static void Call(object instance, string name) =>
        instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, null);
}
