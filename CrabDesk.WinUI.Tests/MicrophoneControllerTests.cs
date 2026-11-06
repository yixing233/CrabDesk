using System.Collections.Concurrent;
using CrabDesk.Core;
using CrabDesk.Native;
using CrabDesk.Runtime;
using Moq;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class MicrophoneControllerTests
{
    internal static MicrophoneSnapshot Snapshot() => new("mic", "USB Microphone", 35, true, 18,
        [new("mic", "USB Microphone"), new("headset", "Headset Microphone")]);

    [Fact]
    public async Task SliderCoalescesValuesAndPreservesMicrophoneMute()
    {
        var state = Snapshot();
        var writes = new ConcurrentQueue<double>();
        var source = new Mock<IMicrophone>();
        source.Setup(s => s.Read()).Returns(() => state);
        source.Setup(s => s.SetVolume("mic", It.IsAny<double>())).Returns((string _, double volume) =>
        { writes.Enqueue(volume); state = state with { VolumePercent = volume }; return true; });
        using var controller = new MicrophoneController(source.Object);
        await controller.RefreshAsync();
        for (var volume = 20; volume <= 70; volume++) controller.SetVolume(volume);
        Assert.Equal(70, controller.Snapshot.VolumePercent);
        Assert.True(controller.Snapshot.Muted);
        await controller.PendingWrites.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(70, Assert.Single(writes));
        Assert.True(controller.Snapshot.Muted);
        source.Verify(s => s.SetMuted(It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task MuteAndUnmuteUseTheSelectedCaptureEndpoint()
    {
        var state = Snapshot();
        var source = new Mock<IMicrophone>();
        source.Setup(s => s.Read()).Returns(() => state);
        source.Setup(s => s.SetMuted("mic", It.IsAny<bool>())).Returns((string _, bool muted) =>
        { state = state with { Muted = muted }; return true; });
        using var controller = new MicrophoneController(source.Object);
        await controller.RefreshAsync();
        controller.SetMuted(false);
        await controller.PendingWrites.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(controller.Snapshot.Muted);
        controller.SetMuted(true);
        await controller.PendingWrites.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(controller.Snapshot.Muted);
        source.Verify(s => s.SetMuted("mic", false), Times.Once);
        source.Verify(s => s.SetMuted("mic", true), Times.Once);
    }

    [Fact]
    public async Task SwitchingDeviceKeepsQueuedVolumeOnItsOriginalEndpoint()
    {
        var state = Snapshot();
        var source = new Mock<IMicrophone>();
        source.Setup(s => s.Read()).Returns(() => state);
        source.Setup(s => s.SetVolume("mic", 70)).Returns(true);
        source.Setup(s => s.SetDevice("headset")).Returns(() =>
        { state = state with { DeviceId = "headset", DeviceName = "Headset Microphone", VolumePercent = 50, Muted = false }; return true; });
        using var controller = new MicrophoneController(source.Object);
        await controller.RefreshAsync();
        controller.SetVolume(70);
        controller.SetDevice("headset");
        Assert.Null(controller.Snapshot.VolumePercent);
        controller.SetVolume(90);
        await controller.PendingWrites.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("headset", controller.Snapshot.DeviceId);
        Assert.Equal(50, controller.Snapshot.VolumePercent);
        source.Verify(s => s.SetVolume("mic", 70), Times.Once);
        source.Verify(s => s.SetVolume("headset", It.IsAny<double>()), Times.Never);
    }

    [Fact]
    public async Task FailedWritesReadBackConfirmedStateAndCanRetry()
    {
        var state = Snapshot();
        var source = new Mock<IMicrophone>();
        source.Setup(s => s.Read()).Returns(() => state);
        source.Setup(s => s.SetVolume("mic", It.IsAny<double>())).Returns(false);
        using var controller = new MicrophoneController(source.Object);
        await controller.RefreshAsync();
        controller.SetVolume(90);
        await controller.PendingWrites.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(35, controller.Snapshot.VolumePercent);
        Assert.NotEmpty(controller.Error);
        source.Setup(s => s.SetVolume("mic", 60)).Returns(() => { state = state with { VolumePercent = 60 }; return true; });
        controller.SetVolume(60);
        await controller.PendingWrites.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(60, controller.Snapshot.VolumePercent);
        Assert.Empty(controller.Error);
    }

    [Fact]
    public async Task MissingDevicesDisableWritesAndInvalidSelectionsAreIgnored()
    {
        var source = new Mock<IMicrophone>();
        source.Setup(s => s.Read()).Returns(MicrophoneSnapshot.Empty);
        using var controller = new MicrophoneController(source.Object);
        await controller.RefreshAsync();
        controller.SetVolume(20); controller.SetMuted(false); controller.SetDevice("missing");
        await controller.PendingWrites;
        source.Verify(s => s.SetVolume(It.IsAny<string>(), It.IsAny<double>()), Times.Never);
        source.Verify(s => s.SetMuted(It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
        source.Verify(s => s.SetDevice(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task SlowRefreshCannotOverwriteANewerVolumeRequest()
    {
        var state = Snapshot();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var reads = 0;
        var source = new Mock<IMicrophone>();
        source.Setup(s => s.Read()).Returns(() =>
        {
            var result = state;
            if (Interlocked.Increment(ref reads) == 2) { entered.TrySetResult(); release.Wait(TimeSpan.FromSeconds(5)); }
            return result;
        });
        source.Setup(s => s.SetVolume("mic", 65)).Returns(() => { state = state with { VolumePercent = 65 }; return true; });
        using var controller = new MicrophoneController(source.Object);
        await controller.RefreshAsync();
        var refresh = controller.RefreshAsync();
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); controller.SetVolume(65); }
        finally { release.Set(); }
        await refresh;
        Assert.Equal(65, controller.Snapshot.VolumePercent);
        await controller.PendingWrites.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(65, controller.Snapshot.VolumePercent);
    }
}
