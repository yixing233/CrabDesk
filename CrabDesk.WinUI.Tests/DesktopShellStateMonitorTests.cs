using CrabDesk.Native;
using CrabDesk.Runtime;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class DesktopShellStateMonitorTests
{
    [Fact]
    public async Task MonitorPublishesOnlyChangedSnapshots()
    {
        var snapshot = new DesktopShellSnapshot(
            new DesktopIconViewState(
                new DesktopIconSortState(DesktopIconSortMode.Name, false),
                48,
                true,
                false,
                "same"),
            "icons");
        var published = new List<DesktopShellSnapshot>();
        await using var monitor = new DesktopShellStateMonitor(
            () => snapshot,
            value => published.Add(value),
            TimeSpan.FromMilliseconds(10));

        monitor.Start();
        await Task.Delay(45);
        await monitor.StopAsync();

        Assert.Single(published);
    }
}
