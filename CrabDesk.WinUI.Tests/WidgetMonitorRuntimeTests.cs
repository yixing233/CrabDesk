using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using System.Runtime.CompilerServices;
using CrabDesk.Core;
using CrabDesk.Runtime;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class WidgetMonitorRuntimeTests
{
    [Fact]
    public void SwitchingNetworkStylesResizesAndClampsWithoutLosingLiveData()
    {
        var runtime = Fixture(); using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { }); Set(runtime, "_saveTimer", timer);
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Network, MonitorId = "test", Size = DesktopWidgetSize.Large,
            Bounds = new(900, 630, 372, 180), Network = new(DateTimeOffset.Now) { InterfaceId = "a", IsConnected = true } };
        widget.NetworkHistory.Add(new(DateTimeOffset.Now, 100, 50)); runtime.State.Widgets.Add(widget);
        var changed = 0; runtime.Changed += (_, _) => changed++;
        runtime.SetNetworkDisplayMode(widget, NetworkWidgetDisplayMode.Speeds);
        Assert.Equal(DesktopWidgetSize.Small, widget.Size); Assert.Equal(180, widget.Bounds.Width); Assert.Equal(84, widget.Bounds.Height);
        Assert.True(widget.Bounds.X + widget.Bounds.Width <= 1000 && widget.Bounds.Y + widget.Bounds.Height <= 700);
        runtime.ResizeWidget(widget, DesktopWidgetSize.Wide); Assert.Equal(372, widget.Bounds.Width);
        runtime.SetNetworkDisplayMode(widget, NetworkWidgetDisplayMode.Curve);
        Assert.Equal(DesktopWidgetSize.Large, widget.Size); Assert.Equal(180, widget.Bounds.Height);
        Assert.NotNull(widget.Network); Assert.Single(widget.NetworkHistory); Assert.Equal(3, changed);
        runtime.SetNetworkDisplayMode(widget, NetworkWidgetDisplayMode.Curve); Assert.Equal(3, changed);
        runtime.ResizeWidget(widget, DesktopWidgetSize.Small); Assert.Equal(DesktopWidgetSize.Large, widget.Size);
    }

    [Fact]
    public void UnsupportedDisplayModeAndInsufficientWorkAreaDoNotPartiallyChangeTheWidget()
    {
        var runtime = Fixture();
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Network, MonitorId = "test", Size = DesktopWidgetSize.Small,
            NetworkDisplayMode = NetworkWidgetDisplayMode.Speeds, Bounds = new(0, 0, 180, 84) };
        runtime.State.Widgets.Add(widget);
        runtime.SetNetworkDisplayMode(widget, (NetworkWidgetDisplayMode)999);
        Set(runtime, "<Monitors>k__BackingField", new[] { new MonitorLayout { Id = "test", DeviceName = "test", IsPrimary = true,
            Bounds = new(0, 0, 200, 100), WorkArea = new(0, 0, 200, 100), PixelBounds = new(0, 0, 200, 100), PixelWorkArea = new(0, 0, 200, 100) } });
        Assert.Throws<InvalidOperationException>(() => runtime.SetNetworkDisplayMode(widget, NetworkWidgetDisplayMode.Curve));
        Assert.Equal(NetworkWidgetDisplayMode.Speeds, widget.NetworkDisplayMode); Assert.Equal(DesktopWidgetSize.Small, widget.Size);
        Assert.Equal(new LayoutRect(0, 0, 180, 84), widget.Bounds);
    }

    [Fact]
    public async Task PublicIpIsOnlyRequestedExplicitlyAndConcurrentClicksShareOneRequest()
    {
        var runtime = Fixture(); using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { }); Set(runtime, "_saveTimer", timer);
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0; var changed = 0; runtime.Changed += (_, _) => changed++;
        Set(runtime, "_publicIpQuery", (Func<CancellationToken, Task<string>>)(_ => { requests++; return completion.Task; }));
        var widget = runtime.AddWidget(DesktopWidgetKind.Network);
        Assert.Same(widget, runtime.AddWidget(DesktopWidgetKind.Network)); Assert.Equal(0, requests);
        widget.Network = new(DateTimeOffset.Now) { IsConnected = true, InterfaceId = "test" };
        var saves = changed;
        var request = runtime.QueryWidgetPublicIpAsync(widget);
        Assert.True(widget.IsPublicIpLoading); Assert.Equal(1, requests);
        await runtime.QueryWidgetPublicIpAsync(widget); Assert.Equal(1, requests);
        completion.SetResult("203.0.113.18"); await request;
        Assert.Equal("203.0.113.18", widget.PublicIp); Assert.False(widget.IsPublicIpLoading);
        Assert.Equal(saves, changed);
    }

    [Theory]
    [InlineData("remove")] [InlineData("switch")] [InlineData("disconnect")]
    public async Task PublicIpResultCannotReappearAfterRemovalInterfaceSwitchOrDisconnect(string action)
    {
        var runtime = Fixture(); using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { }); Set(runtime, "_saveTimer", timer);
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Set(runtime, "_publicIpQuery", (Func<CancellationToken, Task<string>>)(_ => completion.Task));
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Network, Network = new(DateTimeOffset.Now) { IsConnected = true, InterfaceId = "old" } };
        runtime.State.Widgets.Add(widget);
        var request = runtime.QueryWidgetPublicIpAsync(widget);
        if (action == "remove") runtime.RemoveWidget(widget);
        else if (action == "disconnect") widget.Network = widget.Network with { IsConnected = false };
        else widget.Network = widget.Network with { InterfaceId = "new" };
        completion.SetResult("203.0.113.18"); await request;
        Assert.Empty(widget.PublicIp); Assert.False(widget.IsPublicIpLoading);
    }

    [Fact]
    public async Task DisconnectedNetworkDoesNotQueryAndFailedQueriesCanRetry()
    {
        var runtime = Fixture(); var requests = 0;
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Network }; runtime.State.Widgets.Add(widget);
        Set(runtime, "_publicIpQuery", (Func<CancellationToken, Task<string>>)(_ =>
            ++requests == 1 ? Task.FromException<string>(new HttpRequestException("offline")) : Task.FromResult("203.0.113.19")));
        await runtime.QueryWidgetPublicIpAsync(widget); Assert.Equal(0, requests); Assert.NotEmpty(widget.PublicIpError);
        widget.Network = new(DateTimeOffset.Now) { IsConnected = true, InterfaceId = "adapter" };
        await runtime.QueryWidgetPublicIpAsync(widget); Assert.False(widget.IsPublicIpLoading); Assert.NotEmpty(widget.PublicIpError);
        await runtime.QueryWidgetPublicIpAsync(widget); Assert.Equal("203.0.113.19", widget.PublicIp); Assert.Empty(widget.PublicIpError);
    }

    [Fact]
    public async Task PublicIpTransportTimeoutShowsRetryState()
    {
        var runtime = Fixture();
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Network, Network = new(DateTimeOffset.Now) { IsConnected = true, InterfaceId = "adapter" } };
        runtime.State.Widgets.Add(widget);
        Set(runtime, "_publicIpQuery", (Func<CancellationToken, Task<string>>)(_ => Task.FromException<string>(new TaskCanceledException("HTTP timeout"))));
        await runtime.QueryWidgetPublicIpAsync(widget);
        Assert.False(widget.IsPublicIpLoading); Assert.NotEmpty(widget.PublicIpError); Assert.Empty(widget.PublicIp);
    }

        // Every style must be offered in the menu, and switching to the curve-only
    // style has to land on the large card, which is what its layout assumes.
    [Fact]
    public void CurveOnlyStyleIsOfferedAndSwitchesToALargeCard()
    {
        var runtime = Fixture(); using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { }); Set(runtime, "_saveTimer", timer);
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Network, MonitorId = "test", Size = DesktopWidgetSize.Small,
            Bounds = new(100, 100, 180, 84), Network = new(DateTimeOffset.Now) { IsConnected = true } };
        runtime.State.Widgets.Add(widget);

        using (var menu = DesktopBoxForm.BuildMonitorContextMenu(widget))
        {
            var styles = menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == "显示样式");
            var labels = styles.DropDownItems.OfType<ToolStripMenuItem>().Select(item => item.Text).ToArray();
            Assert.Contains("网速曲线", labels);
            Assert.Equal(DesktopWidgets.OfferedNetworkDisplayModes.Count, labels.Length);
            Assert.Equal(["详细信息", "网速文本", "网速曲线"], labels);
        }

        runtime.SetNetworkDisplayMode(widget, NetworkWidgetDisplayMode.Curve);

        Assert.Equal(NetworkWidgetDisplayMode.Curve, widget.NetworkDisplayMode);
        Assert.Equal(DesktopWidgetSize.Large, widget.Size);
        Assert.Equal(372, widget.Bounds.Width);
        Assert.Equal(180, widget.Bounds.Height);
    }

    // The output row opens a list rather than cycling: with three devices the
    // user picks the one they want instead of clicking until it comes round.
    [Fact]
    public void OutputDeviceMenuListsEveryEndpointAndMarksTheCurrentOne()
    {
        var snapshot = new ControlCenterSnapshot
        {
            OutputDeviceId = "b",
            OutputDevices = [new("a", "耳机", false), new("b", "扬声器", true), new("c", "显示器", false)]
        };

        using var menu = DesktopBoxForm.BuildControlCenterOutputMenu(snapshot);

        var items = menu.Items.OfType<ToolStripMenuItem>().ToArray();
        Assert.Equal(["耳机", "扬声器", "显示器"], items.Select(item => item.Text).ToArray());
        // Exactly one tick, on the device the card is currently showing.
        Assert.Single(items, item => item.Checked);
        Assert.Equal("扬声器", items.Single(item => item.Checked).Text);
    }

    [Fact]
    public void OutputDeviceMenuIsEmptyWhenNoEndpointIsKnown()
    {
        using var menu = DesktopBoxForm.BuildControlCenterOutputMenu(new ControlCenterSnapshot());

        Assert.Empty(menu.Items);
    }

    // Disposing a ContextMenuStrip synchronously from its own Closed handler
    // leaves a disposed drop-down behind that ToolStripManager still references,
    // which crashed the app on the next click. Every menu in this project defers
    // the dispose; this test pins the contract for the output picker too.
    [Fact]
    public void OutputDeviceSelectionHandsOffWithoutThrow()
    {
        var snapshot = new ControlCenterSnapshot
        {
            OutputDeviceId = "a",
            OutputDevices = [new("a", "耳机", true), new("b", "扬声器", false)]
        };
        using var menu = DesktopBoxForm.BuildControlCenterOutputMenu(snapshot);

        var item = menu.Items.OfType<ToolStripMenuItem>().Last();
        Assert.Equal("扬声器", item.Text);

        // Closing then disposing is the deferred path the form uses; disposing
        // while Closed is still running is what threw.
        menu.Close();
        menu.Dispose();
    }

    // A slider that can only be clicked is not a slider. Pressing one must capture
    // the pointer, or the move falls through to the drag branch and the whole card
    // slides away instead of the level changing.
    [Fact]
    public void SlidersCaptureThePointerAndFollowItWhileDragging() => OnSta(() =>
    {
        var runtime = Fixture(); using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { }); Set(runtime, "_saveTimer", timer);
        var widget = new DesktopWidget
        {
            Kind = DesktopWidgetKind.ControlCenter, MonitorId = "test", Size = DesktopWidgetSize.Panel,
            Bounds = new(100, 100, 372, 292),
            Controls = new ControlCenterSnapshot { VolumePercent = 20, BrightnessPercent = 20 }
        };
        runtime.State.Widgets.Add(widget);
        using var form = new DesktopBoxForm(runtime, runtime.Monitors.First());

        var body = WidgetMonitorPresentation.Body(372, 292, 8);
        var sliders = ControlCenterPresentation.SliderArea(body);
        var half = (sliders.Width - 14) / 2;

        var captures = (bool)typeof(DesktopBoxForm)
            .GetMethod("IsControlCenterDragTarget", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(form, [widget, new PointF((float)(100 + sliders.X + half / 2), (float)(100 + sliders.Y + 10))])!;
        Assert.True(captures);

        var update = typeof(DesktopBoxForm)
            .GetMethod("UpdateControlCenterDrag", BindingFlags.NonPublic | BindingFlags.Instance)!;
        typeof(DesktopBoxForm).GetField("_controlPressedWidgetId", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(form, (Guid?)widget.Id);
        typeof(DesktopBoxForm).GetField("_controlPressedTarget", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(form, ControlCenterPresentation.ControlCenterTarget.VolumeSlider);

        foreach (var fraction in new[] { 0.25, 0.75 })
        {
            var x = (float)(100 + sliders.X + half * fraction);
            update.Invoke(form, [widget, new PointF(x, (float)(100 + sliders.Y + 10))]);
            // The level tracks the pointer rather than staying where it was pressed.
            Assert.Equal(fraction * 100, widget.Controls!.VolumePercent!.Value, 1);
        }
    });

    // The model names are the reason the submenu exists; they have to reach it.
    [Fact]
    public void HardwareSubmenuListsTheModelNamesItKnows()
    {
        var widget = new DesktopWidget
        {
            Kind = DesktopWidgetKind.Performance,
            Performance = new WidgetPerformanceSnapshot(DateTimeOffset.Now, 12, 3, 8L << 30, 16L << 30, 4, 1024, 2048, 100L << 30, 512L << 30)
            {
                Hardware = new WidgetHardwareNames(
                    "12th Gen Intel Core i7-12700H", "NVIDIA GeForce RTX 4060 Laptop GPU",
                    "DDR4 3200MHz 32 GB", "NVMe WD PC SN740 SDDP")
            }
        };

        using var menu = DesktopBoxForm.BuildMonitorContextMenu(widget);

        var parent = Assert.Single(menu.Items.OfType<ToolStripMenuItem>());
        var rows = parent.DropDownItems.OfType<ToolStripMenuItem>()
            .Select(item => item.Text).ToArray();
        Assert.Contains("处理器：12th Gen Intel Core i7-12700H", rows);
        Assert.Contains("显卡：NVIDIA GeForce RTX 4060 Laptop GPU", rows);
        Assert.Contains("内存：DDR4 3200MHz 32 GB", rows);
        Assert.Contains("磁盘：NVMe WD PC SN740 SDDP", rows);
        // The readings still follow, separated from the names.
        Assert.Contains(rows, row => row.StartsWith("物理内存"));
        Assert.Contains(parent.DropDownItems.Cast<ToolStripItem>(), item => item is ToolStripSeparator);
    }

// The readings used to sit directly in the top level, and their combined
    // height stretched the menu far past the actions the user picks from.
    [Fact]
    public void PerformanceMenuKeepsHardwareReadingsInAChildMenu()
    {
        var widget = new DesktopWidget
        {
            Kind = DesktopWidgetKind.Performance,
            Performance = new WidgetPerformanceSnapshot(DateTimeOffset.Now, 12, 3, 8L << 30, 16L << 30, 4, 1024, 2048, 100L << 30, 512L << 30)
        };

        using var menu = DesktopBoxForm.BuildMonitorContextMenu(widget);

        var parent = Assert.Single(menu.Items.OfType<ToolStripMenuItem>());
        Assert.Equal("硬件信息", parent.Text);
        Assert.Empty(menu.Items.OfType<ToolStripItem>().Where(item => !item.Equals(parent) && item is not ToolStripSeparator));
        Assert.All(parent.DropDownItems.OfType<ToolStripMenuItem>(), item => Assert.False(item.Enabled));
        Assert.Contains(parent.DropDownItems.OfType<ToolStripMenuItem>(), item => item.Text.StartsWith("物理内存"));
        Assert.Contains(parent.DropDownItems.OfType<ToolStripMenuItem>(), item => item.Text.StartsWith("磁盘读"));
        Assert.Contains(parent.DropDownItems.OfType<ToolStripMenuItem>(), item => item.Text.StartsWith("固定磁盘容量"));
    }

    // A disabled parent cannot open its child, so the readings would be
    // unreachable if the container inherited their read-only state.
    [Fact]
    public void TheHardwareReadingParentStaysEnabledSoItsChildCanOpen()
    {
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Performance };

        using var menu = DesktopBoxForm.BuildMonitorContextMenu(widget);

        var parent = Assert.Single(menu.Items.OfType<ToolStripMenuItem>());
        Assert.True(parent.Enabled);
        Assert.Equal(3, parent.DropDownItems.Count);
        // Read-only values: the rows exist but cannot be invoked.
        Assert.Contains(parent.DropDownItems.OfType<ToolStripMenuItem>(), item => item.Text.Contains("等待采样"));
    }

    [Fact]
    public void PerformanceMenuSpendsNoExtraTopLevelRowsOnReadings()
    {
        var withData = new DesktopWidget
        {
            Kind = DesktopWidgetKind.Performance,
            Performance = new WidgetPerformanceSnapshot(DateTimeOffset.Now, 12, 3, 8L << 30, 16L << 30, 4, 1024, 2048, 100L << 30, 512L << 30)
        };
        var withoutData = new DesktopWidget { Kind = DesktopWidgetKind.Performance };

        using var populated = DesktopBoxForm.BuildMonitorContextMenu(withData);
        using var empty = DesktopBoxForm.BuildMonitorContextMenu(withoutData);

        // Same row count either way: the readings never drive the top level.
        Assert.Equal(empty.Items.Count, populated.Items.Count);
        Assert.Single(populated.Items.OfType<ToolStripMenuItem>());
    }

    private static CrabDeskRuntime Fixture()
    {
        var runtime = (CrabDeskRuntime)RuntimeHelpers.GetUninitializedObject(typeof(CrabDeskRuntime));
        Set(runtime, "<State>k__BackingField", new CrabDeskState());
        Set(runtime, "<Monitors>k__BackingField", new[] { new MonitorLayout { Id = "test", DeviceName = "test", IsPrimary = true,
            Bounds = new(0, 0, 1000, 700), WorkArea = new(0, 0, 1000, 700),
            PixelBounds = new(0, 0, 1000, 700), PixelWorkArea = new(0, 0, 1000, 700) } });
        Set(runtime, "_beginInvoke", (Action<Action>)(action => action()));
        Set(runtime, "_widgetRequests", new Dictionary<Guid, CancellationTokenSource>());
        Set(runtime, "_weatherRetryAt", new Dictionary<Guid, DateTimeOffset>());
        return runtime;
    }

    private static void Set(object target, string field, object value) => target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);

    /// <summary>
    /// Runs a body on an STA thread. Constructing a DesktopBoxForm touches WPF's
    /// InputManager, which refuses to initialise on an MTA test thread.
    /// </summary>
    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception exception) { failure = exception; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }
}
