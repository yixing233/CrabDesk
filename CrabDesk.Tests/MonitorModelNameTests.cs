using System.Text;
using System.Runtime.InteropServices;
using CrabDesk.Native;

namespace CrabDesk.Tests;

public sealed class MonitorModelNameTests
{
    [Fact]
    public void ActiveDisplayInteropLayoutsMatchWindowsSdk()
    {
        Assert.Equal(72, Marshal.SizeOf<ActiveDisplayPaths.PathInfo>());
        Assert.Equal(84, Marshal.SizeOf<ActiveDisplayPaths.SourceName>());
        Assert.Equal(420, Marshal.SizeOf<ActiveDisplayPaths.TargetName>());
    }

    [Fact]
    public void XiaomiEdidUsesARecognizableBrand()
    {
        var data = new byte[128]; data[1] = data[2] = data[3] = data[4] = data[5] = data[6] = 255;
        var manufacturer = (24 << 10) | (13 << 5) | 9;
        data[8] = (byte)(manufacturer >> 8); data[9] = (byte)manufacturer;
        data[57] = 0xFC; Encoding.ASCII.GetBytes("Mi Monitor\n").CopyTo(data, 59);
        Assert.Equal("小米/REDMI Mi Monitor", MonitorModelName.ParseEdid(data));
        Assert.False(BrightnessControl.InstanceMatches(@"DISPLAY\BOE0B68\5&1c434da0&3&UID4355_0", @"DISPLAY\XMI3001\5&1c434da0&3&UID4354"));
    }
    [Theory]
    [InlineData("27B30H6", "AOC 27B30H6")]
    [InlineData("AOC 27B30H6", "AOC 27B30H6")]
    public void ReadsTheProductNameDescriptorAndManufacturer(string name, string expected)
    {
        var data = new byte[128]; data[1] = data[2] = data[3] = data[4] = data[5] = data[6] = 255;
        var manufacturer = (1 << 10) | (15 << 5) | 3;
        data[8] = (byte)(manufacturer >> 8); data[9] = (byte)manufacturer;
        data[54 + 3] = 0xFF; // Serial number descriptor must not be used as the model.
        data[72 + 3] = 0xFC;
        Encoding.ASCII.GetBytes(name + "\n").CopyTo(data, 72 + 5);
        Assert.Equal(expected, MonitorModelName.ParseEdid(data));
    }

    [Fact]
    public void MissingOrTruncatedEdidHasNoInventedModel()
    {
        Assert.Empty(MonitorModelName.ParseEdid([]));
        Assert.Empty(MonitorModelName.ParseEdid(new byte[127]));
        Assert.Empty(MonitorModelName.ParseEdid(new byte[128]));
    }
}
