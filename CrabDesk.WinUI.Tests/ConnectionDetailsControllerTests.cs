using System.Collections.Concurrent;
using CrabDesk.Core;
using CrabDesk.Native;
using CrabDesk.Runtime;
using Moq;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class ConnectionDetailsControllerTests
{
    [Fact]
    public async Task ExplicitScanAndPassiveRefreshPassTheirRequestedMode()
    {
        var source = Source();
        var scanned = Snapshot("network", false);
        var refreshed = Snapshot("network", true);
        source.Setup(s => s.ReadAsync(true, It.IsAny<CancellationToken>())).ReturnsAsync(scanned);
        source.Setup(s => s.ReadAsync(false, It.IsAny<CancellationToken>())).ReturnsAsync(refreshed);
        using var controller = new ConnectionDetailsController(source.Object);
        Assert.Equal(ControlConnectionKind.WiFi, controller.Kind);
        Assert.False(controller.HasSnapshot);

        await controller.RefreshAsync(true);
        Assert.True(controller.HasSnapshot);
        Assert.Same(scanned, controller.Snapshot);
        await controller.RefreshAsync();

        Assert.Same(refreshed, controller.Snapshot);
        Assert.False(controller.Busy);
        source.Verify(s => s.ReadAsync(true, It.IsAny<CancellationToken>()), Times.Once);
        source.Verify(s => s.ReadAsync(false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task BusyRefreshDropsOtherRefreshesAndRejectsCommandsUntilItFinishes()
    {
        var source = Source();
        var pending = Pending<ControlConnectionSnapshot>();
        source.Setup(s => s.ReadAsync(true, It.IsAny<CancellationToken>())).Returns(pending.Task);
        var current = Snapshot("network", true);
        source.Setup(s => s.SetEnabledAsync(true, It.IsAny<CancellationToken>())).ReturnsAsync(ControlOperationResult.Success);
        source.Setup(s => s.ReadAsync(false, It.IsAny<CancellationToken>())).ReturnsAsync(current);
        using var controller = new ConnectionDetailsController(source.Object);

        var refresh = controller.RefreshAsync(true);
        Assert.True(controller.Busy);
        Assert.True(controller.Refreshing);
        var refreshed = controller.WaitForRefreshAsync();
        Assert.False(refreshed.IsCompleted);
        await controller.RefreshAsync(true);
        await controller.RefreshAsync(false);
        var rejected = await controller.SetEnabledAsync(true);
        Assert.False(rejected.Succeeded);
        Assert.Contains("稍候", rejected.Error);
        source.Verify(s => s.SetEnabledAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);

        pending.SetResult(Snapshot("old", false));
        await refresh;
        await refreshed.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(controller.Refreshing);
        Assert.True((await controller.SetEnabledAsync(true)).Succeeded);
        Assert.Same(current, controller.Snapshot);
        source.Verify(s => s.ReadAsync(true, It.IsAny<CancellationToken>()), Times.Once);
        source.Verify(s => s.ReadAsync(false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("enable")]
    [InlineData("connect")]
    [InlineData("disconnect")]
    public async Task EveryCommandIsFollowedByAPassiveReadAndBusyNotifications(string command)
    {
        var source = Source();
        var order = new List<string>();
        var state = Snapshot("network", command == "connect");
        source.Setup(s => s.SetEnabledAsync(true, It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("command")).ReturnsAsync(ControlOperationResult.Success);
        source.Setup(s => s.ConnectAsync("network", "temporary-password", It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("command")).ReturnsAsync(ControlOperationResult.Success);
        source.Setup(s => s.DisconnectAsync("network", It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("command")).ReturnsAsync(ControlOperationResult.Success);
        source.Setup(s => s.ReadAsync(false, It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("read")).ReturnsAsync(state);
        using var controller = new ConnectionDetailsController(source.Object);
        var busy = new List<bool>();
        controller.Changed += () => busy.Add(controller.Busy);

        var result = command switch
        {
            "enable" => await controller.SetEnabledAsync(true),
            "connect" => await controller.ConnectAsync("network", "temporary-password"),
            _ => await controller.DisconnectAsync("network")
        };

        Assert.True(result.Succeeded);
        Assert.Equal(new[] { "command", "read" }, order);
        Assert.Equal(new[] { true, false }, busy);
        Assert.Same(state, controller.Snapshot);
        Assert.True(controller.HasSnapshot);
        Assert.Empty(controller.Error);
        source.Verify(s => s.ReadAsync(true, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("请输入密码", true)]
    [InlineData("Windows 已拒绝位置权限", false)]
    public async Task ServiceFailureAndPasswordRequestSurviveTheReadback(string error, bool passwordRequired)
    {
        var source = Source();
        var operation = new ControlOperationResult(false, error, passwordRequired);
        var state = new ControlConnectionSnapshot(true, [], error, "ms-settings:privacy-location");
        source.Setup(s => s.ConnectAsync("network", null, It.IsAny<CancellationToken>())).ReturnsAsync(operation);
        source.Setup(s => s.ReadAsync(false, It.IsAny<CancellationToken>())).ReturnsAsync(state);
        using var controller = new ConnectionDetailsController(source.Object);

        var result = await controller.ConnectAsync("network", null);

        Assert.Same(operation, result);
        Assert.Equal(passwordRequired, result.PasswordRequired);
        Assert.Equal(error, controller.Error);
        Assert.Same(state, controller.Snapshot);
        Assert.Equal("ms-settings:privacy-location", controller.Snapshot.SettingsUri);
    }

    [Fact]
    public async Task FailedReadbackKeepsTheLastSnapshotAndReportsItsOwnError()
    {
        var source = Source();
        var state = Snapshot("network", true);
        source.Setup(s => s.ReadAsync(true, It.IsAny<CancellationToken>())).ReturnsAsync(state);
        source.Setup(s => s.DisconnectAsync("network", It.IsAny<CancellationToken>())).ReturnsAsync(ControlOperationResult.Success);
        source.Setup(s => s.ReadAsync(false, It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException());
        using var controller = new ConnectionDetailsController(source.Object);
        await controller.RefreshAsync(true);

        var result = await controller.DisconnectAsync("network");

        Assert.True(result.Succeeded);
        Assert.Same(state, controller.Snapshot);
        Assert.Contains("更新连接状态", controller.Error);
        Assert.False(controller.Busy);
    }

    [Fact]
    public async Task CommandsAndRefreshesCannotOverlapAnExistingCommand()
    {
        var source = Source();
        var pending = Pending<ControlOperationResult>();
        source.Setup(s => s.ConnectAsync("network", null, It.IsAny<CancellationToken>())).Returns(pending.Task);
        source.Setup(s => s.ReadAsync(false, It.IsAny<CancellationToken>())).ReturnsAsync(Snapshot("network", true));
        using var controller = new ConnectionDetailsController(source.Object);

        var connect = controller.ConnectAsync("network", null);
        await controller.RefreshAsync(true);
        Assert.False((await controller.DisconnectAsync("network")).Succeeded);
        source.Verify(s => s.ReadAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        source.Verify(s => s.DisconnectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        pending.SetResult(ControlOperationResult.Success);
        await connect;
        Assert.True(Assert.Single(controller.Snapshot.Items).Connected);
        Assert.False(controller.Busy);
    }

    [Fact]
    public async Task DisposeCancelsPendingReadAndWaitsToReleaseSourceWithoutPublishingLateState()
    {
        var source = Source();
        var pending = Pending<ControlConnectionSnapshot>();
        var token = CancellationToken.None;
        source.Setup(s => s.ReadAsync(true, It.IsAny<CancellationToken>()))
            .Callback<bool, CancellationToken>((_, value) => token = value).Returns(pending.Task);
        var controller = new ConnectionDetailsController(source.Object);
        var changes = 0;
        controller.Changed += () => changes++;

        var refresh = controller.RefreshAsync(true);
        Assert.Equal(1, changes);
        controller.Dispose();
        Assert.True(token.IsCancellationRequested);
        source.Verify(s => s.Dispose(), Times.Never);
        pending.SetResult(Snapshot("late", true));
        await refresh;

        Assert.Equal(1, changes);
        Assert.False(controller.HasSnapshot);
        Assert.Empty(controller.Snapshot.Items);
        source.Verify(s => s.Dispose(), Times.Once);
        controller.Dispose();
        await controller.RefreshAsync(true);
        Assert.False((await controller.ConnectAsync("network", null)).Succeeded);
        source.Verify(s => s.ReadAsync(true, It.IsAny<CancellationToken>()), Times.Once);
        source.Verify(s => s.Dispose(), Times.Once);
    }

    [Fact]
    public async Task DisposeDuringCommandSkipsReadbackAndDefersSourceDisposal()
    {
        var source = Source();
        var pending = Pending<ControlOperationResult>();
        source.Setup(s => s.SetEnabledAsync(true, It.IsAny<CancellationToken>())).Returns(pending.Task);
        var controller = new ConnectionDetailsController(source.Object);
        var command = controller.SetEnabledAsync(true);

        controller.Dispose();
        source.Verify(s => s.Dispose(), Times.Never);
        pending.SetResult(ControlOperationResult.Success);
        await command;

        source.Verify(s => s.ReadAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        source.Verify(s => s.Dispose(), Times.Once);
        Assert.False(controller.HasSnapshot);
    }

    [Fact]
    public void CallsAndReadbackKeepTheCallingUiSynchronizationContext()
    {
        using var context = new PumpContext();
        context.Run(async () =>
        {
            var source = Source();
            var thread = Environment.CurrentManagedThreadId;
            var calls = new List<string>();
            void Check(string call)
            {
                Assert.Same(context, SynchronizationContext.Current);
                Assert.Equal(thread, Environment.CurrentManagedThreadId);
                calls.Add(call);
            }
            source.Setup(s => s.ReadAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .Callback<bool, CancellationToken>((scan, _) => Check(scan ? "scan" : "read"))
                .Returns<bool, CancellationToken>((_, _) => Task.Delay(10)
                    .ContinueWith(_ => Snapshot("network", true), TaskScheduler.Default));
            source.Setup(s => s.SetEnabledAsync(true, It.IsAny<CancellationToken>()))
                .Callback(() => Check("enable"))
                .Returns(() => Task.Delay(10).ContinueWith(_ => ControlOperationResult.Success, TaskScheduler.Default));
            using var controller = new ConnectionDetailsController(source.Object);
            controller.Changed += () =>
            {
                Assert.Same(context, SynchronizationContext.Current);
                Assert.Equal(thread, Environment.CurrentManagedThreadId);
            };

            var scan = controller.RefreshAsync(true);
            Assert.Equal(new[] { "scan" }, calls);
            await scan;
            var command = controller.SetEnabledAsync(true);
            Assert.Equal(new[] { "scan", "enable" }, calls);
            Assert.True((await command).Succeeded);
            Assert.Equal(new[] { "scan", "enable", "read" }, calls);
        });
    }

    private static Mock<IControlConnectionService> Source()
    {
        var source = new Mock<IControlConnectionService>(MockBehavior.Strict);
        source.SetupGet(s => s.Kind).Returns(ControlConnectionKind.WiFi);
        source.Setup(s => s.Dispose());
        return source;
    }

    private static ControlConnectionSnapshot Snapshot(string id, bool connected) => new(true,
        [new(id, id, "", connected, !connected, connected)]);

    private static TaskCompletionSource<T> Pending<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class PumpContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
        public override void Post(SendOrPostCallback callback, object? state) => _queue.Add((callback, state));

        public void Run(Func<Task> action)
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try
            {
                var task = action();
                while (!task.IsCompleted)
                {
                    Assert.True(_queue.TryTake(out var next, TimeSpan.FromSeconds(5)), "UI continuation did not arrive.");
                    next.Callback(next.State);
                }
                task.GetAwaiter().GetResult();
            }
            finally { SetSynchronizationContext(previous); }
        }

        public void Dispose() => _queue.Dispose();
    }
}
