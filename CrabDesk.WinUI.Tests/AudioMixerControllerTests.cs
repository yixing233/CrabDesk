using System.Collections.Concurrent;
using CrabDesk.Core;
using CrabDesk.Native;
using CrabDesk.Runtime;
using Moq;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class AudioMixerControllerTests
{
    internal static AudioMixerSnapshot Snapshot() => new("headphones", "Headphones", 25, false,
        [new("player", "Player", "", 50, true), new("browser", "Browser", "", 80, false)]);

    [Fact]
    public async Task AppSliderUnmutesOnlyThatApplicationAndCoalescesItsValues()
    {
        var state = Snapshot();
        var writes = new ConcurrentQueue<(string Device, string? App, double Volume)>();
        var source = Source(() => state);
        source.Setup(s => s.SetVolume(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<double>())).Returns((string device, string? id, double value) =>
        {
            writes.Enqueue((device, id, value));
            state = state with { Applications = state.Applications.Select(app => app.Id == id ? app with { VolumePercent = value } : app).ToArray() };
            return true;
        });
        source.Setup(s => s.SetMuted(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>())).Returns((string _, string? id, bool muted) =>
        {
            state = state with { Applications = state.Applications.Select(app => app.Id == id ? app with { Muted = muted } : app).ToArray() };
            return true;
        });
        using var controller = new AudioMixerController(source.Object);
        await controller.RefreshAsync();
        for (var value = 20; value <= 70; value++) controller.SetVolume("player", value);
        Assert.Equal(70, controller.Snapshot.Applications[0].VolumePercent);
        Assert.False(controller.Snapshot.Applications[0].Muted);
        await controller.PendingWrites.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(("headphones", "player", 70d), Assert.Single(writes));
        Assert.Equal(25, controller.Snapshot.VolumePercent);
        Assert.Equal(80, controller.Snapshot.Applications[1].VolumePercent);
        source.Verify(s => s.SetMuted("headphones", "player", false), Times.Once);
    }

    [Fact]
    public async Task MasterSliderTargetsTheEndpointAndClampsTheLevel()
    {
        var source = Source(Snapshot);
        using var controller = new AudioMixerController(source.Object);
        await controller.RefreshAsync();
        controller.SetVolume(null, 140);
        Assert.Equal(100, controller.Snapshot.VolumePercent);
        await controller.PendingWrites.WaitAsync(TimeSpan.FromSeconds(5));
        source.Verify(s => s.SetVolume("headphones", null, 100), Times.Once);
        source.Verify(s => s.SetMuted("headphones", null, false), Times.Once);
    }

    [Fact]
    public async Task AppMuteAndAnotherAppsVolumeKeepIndependentLastCommands()
    {
        var source = Source(Snapshot);
        using var controller = new AudioMixerController(source.Object);
        await controller.RefreshAsync();
        controller.SetVolume("player", 55);
        controller.SetMuted("player", true);
        controller.SetVolume("browser", 15);
        await controller.PendingWrites.WaitAsync(TimeSpan.FromSeconds(5));
        source.Verify(s => s.SetVolume("headphones", "player", 55), Times.Once);
        source.Verify(s => s.SetMuted("headphones", "player", true), Times.Once);
        source.Verify(s => s.SetMuted("headphones", "player", false), Times.Never);
        source.Verify(s => s.SetVolume("headphones", "browser", 15), Times.Once);
    }

    [Fact]
    public async Task FailedVolumeWriteDoesNotUnmuteAndRestoresTheReadBack()
    {
        var source = Source(Snapshot);
        source.Setup(s => s.SetVolume("headphones", "player", 40)).Returns(false);
        using var controller = new AudioMixerController(source.Object);
        await controller.RefreshAsync();
        controller.SetVolume("player", 40);
        await controller.PendingWrites.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(50, controller.Snapshot.Applications[0].VolumePercent);
        Assert.True(controller.Snapshot.Applications[0].Muted);
        Assert.NotEmpty(controller.Error);
        source.Verify(s => s.SetMuted(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task PendingWriteKeepsItsOutputDeviceWhenTheDefaultDeviceChanges()
    {
        var state = Snapshot();
        var source = Source(() => state);
        using var controller = new AudioMixerController(source.Object);
        await controller.RefreshAsync();
        controller.SetVolume("player", 40);
        state = state with { DeviceId = "speakers", DeviceName = "Speakers" };
        await controller.PendingWrites.WaitAsync(TimeSpan.FromSeconds(5));
        source.Verify(s => s.SetVolume("headphones", "player", 40), Times.Once);
        source.Verify(s => s.SetVolume("speakers", It.IsAny<string?>(), It.IsAny<double>()), Times.Never);
        Assert.Equal("speakers", controller.Snapshot.DeviceId);
    }

    [Fact]
    public async Task ASlowRefreshCannotReplaceANewerSliderValue()
    {
        var source = Source(Snapshot);
        using var controller = new AudioMixerController(source.Object);
        await controller.RefreshAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        source.Setup(s => s.Read()).Returns(() => { entered.TrySetResult(); release.Wait(TimeSpan.FromSeconds(5)); return Snapshot(); });
        var refresh = controller.RefreshAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            controller.SetVolume("player", 12);
            release.Set();
            await refresh;
            Assert.Equal(12, controller.Snapshot.Applications[0].VolumePercent);
        }
        finally { release.Set(); }
        await controller.PendingWrites.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ClosingStopsViewUpdatesAndCompletesTheLastRequestedWrite()
    {
        var source = Source(Snapshot);
        var controller = new AudioMixerController(source.Object);
        await controller.RefreshAsync();
        var notifications = 0;
        controller.Changed += () => notifications++;
        controller.SetVolume("player", 35);
        controller.Dispose();
        var count = notifications;
        await controller.PendingWrites.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(count, notifications);
        source.Verify(s => s.SetVolume("headphones", "player", 35), Times.Once);
    }

    private static Mock<IAudioMixer> Source(Func<AudioMixerSnapshot> read)
    {
        var source = new Mock<IAudioMixer>();
        source.Setup(s => s.Read()).Returns(read);
        source.Setup(s => s.SetVolume(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<double>())).Returns(true);
        source.Setup(s => s.SetMuted(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>())).Returns(true);
        return source;
    }
}
