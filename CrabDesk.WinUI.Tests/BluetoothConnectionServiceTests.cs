using CrabDesk.Core;
using CrabDesk.Native;
using Xunit;
using Endpoint = CrabDesk.Native.BluetoothConnectionService.BluetoothEndpoint;

namespace CrabDesk.WinUI.Tests;

public sealed class BluetoothConnectionServiceTests
{
    [Fact]
    public void ClassicAndLeEndpointsMergeAcrossContainerAndAddressLinks()
    {
        var container = Guid.NewGuid().ToString("D");
        var endpoints = new[]
        {
            new Endpoint("classic", "Headphones", container, "AA:BB:CC:DD:EE:FF", false, MajorClass: 4),
            new Endpoint("le", "Headphones LE", null, "aabbccddeeff", true, LowEnergy: true),
            new Endpoint("auxiliary", "", container, "11:22:33:44:55:66", false)
        };

        var item = Assert.Single(BluetoothConnectionService.BuildItems(endpoints, true));

        Assert.Equal("Headphones", item.Name);
        Assert.True(item.Connected);
        Assert.Contains("音频 / 视频设备", item.Detail);
        Assert.Equal(ControlConnectionDeviceKind.Audio, item.DeviceKind);
        Assert.False(item.CanConnect);
        Assert.False(item.CanDisconnect);
    }

    [Fact]
    public void SameNamesAndEmptyHardwareIdsDoNotMergeDifferentDevices()
    {
        var endpoints = new[]
        {
            new Endpoint("first", "Mouse", Guid.Empty.ToString(), "00:00:00:00:00:00", false),
            new Endpoint("second", "Mouse", Guid.Empty.ToString(), "00:00:00:00:00:00", true)
        };

        var items = BluetoothConnectionService.BuildItems(endpoints, true);

        Assert.Equal(2, items.Count);
        Assert.Equal("second", items[0].Id);
        Assert.True(items[0].Connected);
        Assert.False(items[1].Connected);
    }

    [Theory]
    [InlineData(0x10, ControlConnectionDeviceKind.Keyboard)]
    [InlineData(0x20, ControlConnectionDeviceKind.Mouse)]
    [InlineData(0x25, ControlConnectionDeviceKind.Mouse)]
    [InlineData(0x30, ControlConnectionDeviceKind.Keyboard)]
    [InlineData(null, ControlConnectionDeviceKind.Input)]
    public void PeripheralIconUsesClassBitsAndPreservesSpecificTypeAfterEndpointMerge(int? minor, ControlConnectionDeviceKind expected)
    {
        var container = Guid.NewGuid().ToString("D");
        var item = Assert.Single(BluetoothConnectionService.BuildItems(
        [
            new Endpoint("a-generic", "Custom device name", container, null, false, MajorClass: 5),
            new Endpoint("b-specific", "Custom device name", container, null, false, MajorClass: 5, MinorClass: minor)
        ], true));

        Assert.Equal(expected, item.DeviceKind);
    }

    [Fact]
    public void RadioOffClearsCachedConnectionState()
    {
        var item = Assert.Single(BluetoothConnectionService.BuildItems(
            [new Endpoint("headphones", "Headphones", null, null, true, MajorClass: 4)], false));

        Assert.False(item.Connected);
        Assert.Contains("未连接", item.Detail);
    }

    [Fact]
    public void MissingConnectionPropertyRemainsUnknown()
    {
        var item = Assert.Single(BluetoothConnectionService.BuildItems(
            [new Endpoint("device", "", null, null, null, LowEnergy: true)], true));

        Assert.Equal("蓝牙设备", item.Name);
        Assert.False(item.Connected);
        Assert.Contains("低功耗蓝牙设备", item.Detail);
        Assert.Contains("连接状态未知", item.Detail);
    }

    [Fact]
    public async Task ProfileOperationsReturnUnsupportedWithoutChangingDevices()
    {
        using var service = new BluetoothConnectionService();

        Assert.False((await service.ConnectAsync("device", null)).Succeeded);
        var result = await service.DisconnectAsync("device");
        Assert.False(result.Succeeded);
        Assert.Contains("系统设置", result.Error);
    }

    [Fact]
    public async Task PreCanceledOperationsNeverReachHardware()
    {
        using var service = new BluetoothConnectionService();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ReadAsync(true, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SetEnabledAsync(true, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ConnectAsync("device", null, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DisconnectAsync("device", cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BluetoothConnectionService.ReadConnectedLabelAsync(cancellation.Token));
    }

    [Fact]
    public async Task DisposedServiceRejectsFurtherReads()
    {
        var service = new BluetoothConnectionService();
        service.Dispose();
        service.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.ReadAsync(false));
    }
}
