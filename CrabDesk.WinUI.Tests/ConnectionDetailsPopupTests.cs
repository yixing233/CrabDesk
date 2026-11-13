using System.Drawing;
using System.Runtime.ExceptionServices;
using CrabDesk.Core;
using CrabDesk.Native;
using CrabDesk.Runtime;
using Moq;
using Xunit;
using Wpf = System.Windows;
using WpfControls = System.Windows.Controls;
using Media = System.Windows.Media;
using Imaging = System.Windows.Media.Imaging;
using Automation = System.Windows.Automation.AutomationProperties;

namespace CrabDesk.WinUI.Tests;

[Collection("desktop-probe")]
public sealed class ConnectionDetailsPopupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EntranceFadesAndMovesTheWholeWindowThenSettlesAtItsAnchor(bool animationsEnabled)
    {
        OnSta(() =>
        {
            var state = Snapshot(ControlConnectionKind.WiFi);
            var source = Source(ControlConnectionKind.WiFi, () => state);
            using var controller = new ConnectionDetailsController(source.Object);
            using var popup = new ConnectionDetailsPopup(controller, false, Color.RoyalBlue, 1, _ => { }, animationsEnabled);
            popup.Location = new Point(-12000, -12000);
            var surface = Assert.IsType<WpfControls.Border>(popup.ContentView);
            var content = Assert.IsType<WpfControls.Grid>(surface.Child);
            var target = popup.Bounds;
            popup.Show();
            var samples = new List<double>();
            var positions = new List<Point>();
            PumpFrames(TimeSpan.FromMilliseconds(550), () =>
            {
                Assert.False(popup.IsDisposed);
                Assert.Equal(target.Size, popup.Size);
                Assert.Equal(1, surface.Opacity);
                Assert.Equal(1, content.Opacity);
                samples.Add(popup.Opacity);
                positions.Add(popup.Location);
            });
            Assert.Equal(0, DwmGetWindowAttribute(popup.Handle, 14, out var cloaked, sizeof(int)));
            Assert.Equal(0, cloaked);
            Assert.Equal(1, content.Opacity);
            Assert.Equal(1, popup.Opacity);
            Assert.Equal(target, popup.Bounds);
            if (animationsEnabled && Wpf.SystemParameters.ClientAreaAnimation)
            {
                Assert.Contains(samples, opacity => opacity > 0 && opacity < 1);
                Assert.Contains(positions, position => position != target.Location);
            }
            else
            {
                Assert.All(samples, opacity => Assert.Equal(1, opacity));
                Assert.All(positions, position => Assert.Equal(target.Location, position));
            }
            Assert.True(controller.RefreshAsync().IsCompletedSuccessfully);
            Assert.Equal(1, content.Opacity);
            popup.Dismiss();
            VerifyNoCommands(source);
        });
    }

    [Fact]
    public void ClosingBeforeTheFirstFrameDoesNotRevealOrKeepTheCardAlive()
    {
        OnSta(() =>
        {
            var source = Source(ControlConnectionKind.Bluetooth, () => Snapshot(ControlConnectionKind.Bluetooth));
            using var controller = new ConnectionDetailsController(source.Object);
            using var popup = new ConnectionDetailsPopup(controller, true, Color.RoyalBlue, 1, _ => { });
            popup.Location = new Point(-12000, -12000);
            popup.Show();
            popup.Dismiss();
            PumpFrames(TimeSpan.FromMilliseconds(100), () => Assert.False(popup.Visible));
            Assert.True(popup.IsDisposed);
            VerifyNoCommands(source);
        });
    }

    private static void PumpFrames(TimeSpan duration, Action sample)
    {
        var dispatcher = Wpf.Threading.Dispatcher.CurrentDispatcher;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (elapsed.Elapsed < duration)
        {
            System.Windows.Forms.Application.DoEvents();
            dispatcher.Invoke(() => { }, Wpf.Threading.DispatcherPriority.Background);
            sample();
            Thread.Sleep(8);
        }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    [Theory]
    [InlineData(ControlConnectionKind.WiFi, false, false)]
    [InlineData(ControlConnectionKind.WiFi, true, false)]
    [InlineData(ControlConnectionKind.Bluetooth, false, false)]
    [InlineData(ControlConnectionKind.Bluetooth, true, false)]
    [InlineData(ControlConnectionKind.WiFi, false, true)]
    [InlineData(ControlConnectionKind.WiFi, true, true)]
    [InlineData(ControlConnectionKind.Bluetooth, false, true)]
    [InlineData(ControlConnectionKind.Bluetooth, true, true)]
    public async Task SwitchKeepsRequestedStateWhileBusyAndUsesTheConfirmedResult(
        ControlConnectionKind kind, bool initiallyEnabled, bool fail)
    {
        var state = Snapshot(kind) with { Enabled = initiallyEnabled };
        var source = Source(kind, () => state);
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Setup(service => service.SetEnabledAsync(!initiallyEnabled, It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await completed.Task;
                if (!fail) state = state with { Enabled = !initiallyEnabled };
                return fail ? new(false, "系统拒绝切换无线设备") : ControlOperationResult.Success;
            });
        using var controller = new ConnectionDetailsController(source.Object);
        await controller.RefreshAsync(true);
        OnSta(() =>
        {
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(
                new Wpf.Threading.DispatcherSynchronizationContext(Wpf.Threading.Dispatcher.CurrentDispatcher));
            try
            {
                using var popup = new ConnectionDetailsPopup(controller, initiallyEnabled, Color.RoyalBlue, 1, _ => { });
                var view = Layout(popup);
                var radio = Switch(view);
                Assert.Equal(initiallyEnabled, radio.IsChecked);
                Assert.False(radio.IsThreeState);
                Assert.Equal(48, radio.Width);
                Toggle(radio);
                Assert.False(radio.IsEnabled);
                Assert.Equal(!initiallyEnabled, radio.IsChecked);
                popup.ApplySnapshot();
                Assert.Equal(!initiallyEnabled, radio.IsChecked);
                completed.SetResult(true);
                var timer = System.Diagnostics.Stopwatch.StartNew();
                while (!radio.IsEnabled)
                {
                    Assert.True(timer.ElapsedMilliseconds < 5000, "Radio switch did not finish its operation.");
                    Wpf.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, Wpf.Threading.DispatcherPriority.Background);
                    Thread.Sleep(1);
                }
                Assert.Equal(fail ? initiallyEnabled : !initiallyEnabled, radio.IsChecked);
                Assert.Equal(radio.IsChecked is true ? "已开启" : "已关闭", Automation.GetItemStatus(radio));
                if (fail)
                    Assert.Contains(Descendants<WpfControls.TextBlock>(view), label => label.Text == "系统拒绝切换无线设备");
            }
            finally
            {
                completed.TrySetResult(true);
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        });
        source.Verify(service => service.SetEnabledAsync(!initiallyEnabled, It.IsAny<CancellationToken>()), Times.Once);
        source.Verify(service => service.SetEnabledAsync(initiallyEnabled, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(ControlConnectionKind.WiFi, false)]
    [InlineData(ControlConnectionKind.WiFi, true)]
    [InlineData(ControlConnectionKind.Bluetooth, false)]
    [InlineData(ControlConnectionKind.Bluetooth, true)]
    public async Task RefreshKeepsRowsControlsAndScrollStable(ControlConnectionKind kind, bool scan)
    {
        var initial = Snapshot(kind);
        var state = initial with { Items = Enumerable.Range(0, 20).Select(index => initial.Items[index % initial.Items.Count]
            with { Id = $"device-{index}", Name = $"Device {index}" }).ToArray() };
        var source = Source(kind, () => state);
        using var controller = new ConnectionDetailsController(source.Object);
        await controller.RefreshAsync(true);
        OnSta(() =>
        {
            using var popup = new ConnectionDetailsPopup(controller, false, Color.RoyalBlue, 1, _ => { });
            var view = Layout(popup);
            var scroll = Assert.Single(Descendants<WpfControls.ScrollViewer>(view), control => control.Content is WpfControls.StackPanel);
            var rows = (WpfControls.StackPanel)scroll.Content;
            var originalRows = rows.Children.Cast<Wpf.UIElement>().ToArray();
            var radio = Switch(view);
            var radioTemplate = radio.Template;
            var settings = Button(view, kind == ControlConnectionKind.WiFi ? "网络设置" : "添加 / 管理设备");
            var settingsContent = settings.Content;
            scroll.ScrollToVerticalOffset(100); view.UpdateLayout();
            var offset = scroll.VerticalOffset;
            Assert.True(offset > 0);
            var updates = new List<(Wpf.UIElement[] Rows, WpfControls.ControlTemplate Radio, object Settings, bool Enabled, bool Updating)>();
            controller.Changed += () => updates.Add((rows.Children.Cast<Wpf.UIElement>().ToArray(), radio.Template, settings.Content,
                radio.IsEnabled && rows.Children.Cast<Wpf.DependencyObject>().SelectMany(Descendants<WpfControls.Button>).All(button => button.IsEnabled),
                Descendants<WpfControls.TextBlock>(view).Any(label => label.Text == "正在更新连接…")));

            Assert.True(controller.RefreshAsync(scan).IsCompletedSuccessfully);
            Assert.Equal(2, updates.Count);
            foreach (var update in updates)
            {
                Assert.Equal(originalRows.Length, update.Rows.Length);
                for (var index = 0; index < originalRows.Length; index++) Assert.Same(originalRows[index], update.Rows[index]);
                Assert.Same(radioTemplate, update.Radio);
                Assert.Same(settingsContent, update.Settings);
                Assert.True(update.Enabled);
                Assert.False(update.Updating);
            }
            view.UpdateLayout();
            Assert.Equal(offset, scroll.VerticalOffset);
        });
        VerifyNoCommands(source);
    }

    [Fact]
    public async Task RefreshUpdatesAndReordersExistingRowsWithoutStaleClickActions()
    {
        var state = Snapshot(ControlConnectionKind.WiFi);
        var source = Source(ControlConnectionKind.WiFi, () => state);
        using var controller = new ConnectionDetailsController(source.Object);
        await controller.RefreshAsync(true);
        OnSta(() =>
        {
            using var popup = new ConnectionDetailsPopup(controller, false, Color.RoyalBlue, 1, _ => { });
            var view = Layout(popup);
            var rows = (WpfControls.StackPanel)Assert.Single(Descendants<WpfControls.ScrollViewer>(view), control => control.Content is WpfControls.StackPanel).Content;
            var guestRow = rows.Children[1];
            var guestButton = Button(view, "连接 Guest Wi-Fi");
            var guest = state.Items[1] with { Name = "Renamed Wi-Fi", Connected = true, CanConnect = false, CanDisconnect = true, Detail = "已连接", SignalPercent = 60 };
            state = state with { Items = [guest, state.Items[2], new("new-id", "New Wi-Fi", "开放网络", false, true, false)] };
            Assert.True(controller.RefreshAsync().IsCompletedSuccessfully);
            view.UpdateLayout();

            Assert.Equal(3, rows.Children.Count);
            Assert.Same(guestRow, rows.Children[0]);
            Assert.Same(guestButton, Button(view, "断开 Renamed Wi-Fi"));
            Assert.Contains(Descendants<WpfControls.TextBlock>(guestRow), text => text.Text == "已连接 · 60%");
            source.Setup(service => service.DisconnectAsync(guest.Id, It.IsAny<CancellationToken>())).ReturnsAsync(ControlOperationResult.Success);
            Click(guestButton);
            source.Verify(service => service.DisconnectAsync(guest.Id, It.IsAny<CancellationToken>()), Times.Once);
            source.Verify(service => service.ConnectAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        });
    }

    [Fact]
    public async Task PassiveRefreshKeepsPasswordInputEditable()
    {
        var state = Snapshot(ControlConnectionKind.WiFi);
        var source = Source(ControlConnectionKind.WiFi, () => state);
        using var controller = new ConnectionDetailsController(source.Object);
        await controller.RefreshAsync(true);
        OnSta(() =>
        {
            using var popup = new ConnectionDetailsPopup(controller, false, Color.RoyalBlue, 1, _ => { });
            var view = Layout(popup);
            Click(Button(view, "连接 Secure Wi-Fi"));
            var password = Assert.Single(Descendants<WpfControls.PasswordBox>(view));
            password.Password = "in-progress-input";
            var updates = new List<(bool Enabled, string Password, Wpf.Visibility Visibility)>();
            controller.Changed += () => updates.Add((password.IsEnabled, password.Password, PasswordPanel(password).Visibility));
            Assert.True(controller.RefreshAsync().IsCompletedSuccessfully);
            Assert.Equal(2, updates.Count);
            Assert.All(updates, update =>
            {
                Assert.True(update.Enabled);
                Assert.Equal("in-progress-input", update.Password);
                Assert.Equal(Wpf.Visibility.Visible, update.Visibility);
            });
        });
        VerifyNoCommands(source);
    }

    [Fact]
    public async Task ClickDuringRefreshWaitsAndPreservesTheRequestedRadioState()
    {
        var state = Snapshot(ControlConnectionKind.WiFi);
        var source = Source(ControlConnectionKind.WiFi, () => state);
        var pending = new TaskCompletionSource<ControlConnectionSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        source.Setup(service => service.ReadAsync(false, It.IsAny<CancellationToken>()))
            .Returns(() => ++reads == 1 ? pending.Task : Task.FromResult(state));
        var writes = 0;
        source.Setup(service => service.SetEnabledAsync(false, It.IsAny<CancellationToken>()))
            .Callback(() => writes++).ReturnsAsync(ControlOperationResult.Success);
        using var controller = new ConnectionDetailsController(source.Object);
        await controller.RefreshAsync(true);
        OnSta(() =>
        {
            using var popup = new ConnectionDetailsPopup(controller, false, Color.RoyalBlue, 1, _ => { });
            var view = Layout(popup);
            var dispatcher = Wpf.Threading.Dispatcher.CurrentDispatcher;
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new Wpf.Threading.DispatcherSynchronizationContext(dispatcher));
            try
            {
                var refresh = controller.RefreshAsync();
                var radio = Switch(view);
                Toggle(radio);
                Assert.Equal(0, writes);
                Assert.False(radio.IsEnabled);
                state = state with { Enabled = false };
                pending.SetResult(state);
                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                while (!refresh.IsCompleted || writes == 0 || controller.Busy || !radio.IsEnabled)
                {
                    Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5), "Queued click did not complete on the UI thread.");
                    dispatcher.Invoke(() => { }, Wpf.Threading.DispatcherPriority.Background);
                    Thread.Sleep(1);
                }
                Assert.True(refresh.IsCompletedSuccessfully);
                Assert.Equal(1, writes);
                source.Verify(service => service.SetEnabledAsync(true, It.IsAny<CancellationToken>()), Times.Never);
                source.Verify(service => service.ReadAsync(false, It.IsAny<CancellationToken>()), Times.Exactly(2));
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        });
    }

    [Theory]
    [InlineData(ControlConnectionKind.WiFi, false)]
    [InlineData(ControlConnectionKind.WiFi, true)]
    [InlineData(ControlConnectionKind.Bluetooth, false)]
    [InlineData(ControlConnectionKind.Bluetooth, true)]
    public async Task CardRendersBothConnectionKindsAndThemesWithoutIssuingCommands(ControlConnectionKind kind, bool dark)
    {
        var state = Snapshot(kind);
        var source = Source(kind, () => state);
        using var controller = new ConnectionDetailsController(source.Object);
        await controller.RefreshAsync(true);
        OnSta(() =>
        {
            var background = dark ? Color.FromArgb(28, 37, 53) : Color.FromArgb(241, 245, 249);
            using var popup = new ConnectionDetailsPopup(controller, dark, Color.FromArgb(79, 100, 154), 1,
                _ => throw new InvalidOperationException("Rendering must not open settings."), background: background);
            var view = Layout(popup);
            Assert.False(popup.Visible);
            Assert.Equal(new Size(344, 408), popup.ClientSize);
            Assert.Equal(344, view.ActualWidth);
            Assert.Equal(408, view.ActualHeight);
            Assert.All(state.Items, item => Assert.Contains(Descendants<WpfControls.TextBlock>(view), label => label.Text == item.Name));
            Assert.True(Assert.Single(Descendants<WpfControls.ScrollViewer>(view),
                scroll => scroll.Content is WpfControls.StackPanel).ViewportWidth > 250);
            Assert.Contains(Descendants<WpfControls.Button>(view), button =>
                Automation.GetName(button) == (kind == ControlConnectionKind.WiFi ? "刷新Wi-Fi列表" : "刷新蓝牙列表"));
            var surface = Assert.IsType<Media.SolidColorBrush>(Assert.IsType<WpfControls.Border>(view).Background);
            Assert.Equal(background.ToArgb(), popup.BackColor.ToArgb());
            Assert.Equal(Media.Color.FromRgb(background.R, background.G, background.B), surface.Color);
            SaveImage(view, $"connection-details-{(kind == ControlConnectionKind.WiFi ? "wifi" : "bluetooth")}-{(dark ? "dark" : "light")}.png");
        });

        source.Verify(s => s.ReadAsync(true, It.IsAny<CancellationToken>()), Times.Once);
        source.Verify(s => s.ReadAsync(false, It.IsAny<CancellationToken>()), Times.Never);
        VerifyNoCommands(source);
    }

    [Theory]
    [InlineData(ControlConnectionKind.WiFi, false)]
    [InlineData(ControlConnectionKind.Bluetooth, false)]
    [InlineData(ControlConnectionKind.WiFi, true)]
    public async Task EmptyAndPermissionStatesExplainAvailabilityAndExposeTheRightSettings(
        ControlConnectionKind kind, bool denied)
    {
        var state = new ControlConnectionSnapshot(denied ? null : true, [],
            denied ? "Windows 未允许访问 Wi-Fi，请允许位置权限。" : "",
            denied ? "ms-settings:privacy-location" : null);
        var source = Source(kind, () => state);
        using var controller = new ConnectionDetailsController(source.Object);
        await controller.RefreshAsync(true);
        OnSta(() =>
        {
            using var popup = new ConnectionDetailsPopup(controller, false, Color.RoyalBlue, 1, _ => { });
            var view = Layout(popup);
            var empty = kind == ControlConnectionKind.WiFi ? "暂无可用网络" : "暂无已配对设备";
            Assert.Contains(Descendants<WpfControls.TextBlock>(view), label => label.Text == empty);
            if (denied)
            {
                Assert.False(Switch(view).IsEnabled);
                Assert.NotNull(Button(view, "位置权限"));
                Assert.Contains(Descendants<WpfControls.TextBlock>(view), label => label.Text == state.Status && label.Visibility == Wpf.Visibility.Visible);
            }
            else
                Assert.True(Switch(view).IsEnabled);
        });
        VerifyNoCommands(source);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NetworkButtonsDispatchTheSelectedIdAndReadBackWithoutScanning(bool disconnect)
    {
        var state = Snapshot(ControlConnectionKind.WiFi);
        var source = Source(ControlConnectionKind.WiFi, () => state);
        if (disconnect)
            source.Setup(s => s.DisconnectAsync("wifi-connected-id", It.IsAny<CancellationToken>()))
                .Returns(() =>
                {
                    state = ChangeConnected(state, "wifi-connected-id", false);
                    return Task.FromResult(ControlOperationResult.Success);
                });
        else
            source.Setup(s => s.ConnectAsync("wifi-open-id", null, It.IsAny<CancellationToken>()))
                .Returns(() =>
                {
                    state = ChangeConnected(state, "wifi-open-id", true);
                    return Task.FromResult(ControlOperationResult.Success);
                });
        using var controller = new ConnectionDetailsController(source.Object);
        await controller.RefreshAsync(true);
        OnSta(() =>
        {
            using var popup = new ConnectionDetailsPopup(controller, false, Color.RoyalBlue, 1, _ => { });
            var view = Layout(popup);
            Click(Button(view, disconnect ? "断开 Studio Wi-Fi" : "连接 Guest Wi-Fi"));
            Assert.False(controller.Busy);
            Assert.Equal(!disconnect, controller.Snapshot.Items.Single(item =>
                item.Id == (disconnect ? "wifi-connected-id" : "wifi-open-id")).Connected);
        });

        if (disconnect)
        {
            source.Verify(s => s.DisconnectAsync("wifi-connected-id", It.IsAny<CancellationToken>()), Times.Once);
            source.Verify(s => s.ConnectAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        else
        {
            source.Verify(s => s.ConnectAsync("wifi-open-id", null, It.IsAny<CancellationToken>()), Times.Once);
            source.Verify(s => s.DisconnectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        source.Verify(s => s.ReadAsync(true, It.IsAny<CancellationToken>()), Times.Once);
        source.Verify(s => s.ReadAsync(false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProtectedNetworkWaitsForPasswordThenClearsItBeforeSending()
    {
        var state = Snapshot(ControlConnectionKind.WiFi);
        var source = Source(ControlConnectionKind.WiFi, () => state);
        using var controller = new ConnectionDetailsController(source.Object);
        await controller.RefreshAsync(true);
        OnSta(() =>
        {
            using var popup = new ConnectionDetailsPopup(controller, true, Color.RoyalBlue, 1, _ => { });
            var view = Layout(popup);
            var password = Assert.Single(Descendants<WpfControls.PasswordBox>(view));
            Assert.Equal("Wi-Fi 网络密码", Automation.GetName(password));
            Assert.Equal(Wpf.Visibility.Collapsed, PasswordPanel(password).Visibility);

            Click(Button(view, "连接 Secure Wi-Fi"));
            Layout(popup);
            Assert.Equal(Wpf.Visibility.Visible, PasswordPanel(password).Visibility);
            source.Verify(s => s.ConnectAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
            Assert.False(Button(view, "使用密码连接网络").IsEnabled);
            password.Password = "one-time-test-password";
            Assert.True(Button(view, "使用密码连接网络").IsEnabled);
            source.Setup(s => s.ConnectAsync("wifi-secure-id", "one-time-test-password", It.IsAny<CancellationToken>()))
                .Returns(() =>
                {
                    Assert.Empty(password.Password);
                    state = ChangeConnected(state, "wifi-secure-id", true);
                    return Task.FromResult(ControlOperationResult.Success);
                });

            Click(Button(view, "使用密码连接网络"));

            Assert.False(controller.Busy);
            Assert.Empty(password.Password);
            Assert.Equal(Wpf.Visibility.Collapsed, PasswordPanel(password).Visibility);
            Assert.True(controller.Snapshot.Items.Single(item => item.Id == "wifi-secure-id").Connected);
        });
        source.Verify(s => s.ConnectAsync("wifi-secure-id", "one-time-test-password", It.IsAny<CancellationToken>()), Times.Once);
        source.Verify(s => s.ReadAsync(false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task InvalidSavedCredentialsRevealThePasswordPanel()
    {
        var state = new ControlConnectionSnapshot(true,
            [new("wifi-saved-id", "Saved Wi-Fi", "已保存 · WPA2 个人网络", false, true, false)]);
        var source = Source(ControlConnectionKind.WiFi, () => state);
        source.Setup(s => s.ConnectAsync("wifi-saved-id", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ControlOperationResult(false, "请重新输入密码。", true));
        using var controller = new ConnectionDetailsController(source.Object);
        await controller.RefreshAsync(true);
        OnSta(() =>
        {
            using var popup = new ConnectionDetailsPopup(controller, false, Color.RoyalBlue, 1, _ => { });
            var view = Layout(popup);
            Click(Button(view, "连接 Saved Wi-Fi"));
            var password = Assert.Single(Descendants<WpfControls.PasswordBox>(view));

            Assert.Equal(Wpf.Visibility.Visible, PasswordPanel(password).Visibility);
            Assert.Empty(password.Password);
            Assert.False(Button(view, "使用密码连接网络").IsEnabled);
        });
        source.Verify(s => s.ConnectAsync("wifi-saved-id", null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(ControlConnectionKind.WiFi, null, "网络设置", "ms-settings:network-wifi")]
    [InlineData(ControlConnectionKind.WiFi, "ms-settings:privacy-location", "位置权限", "ms-settings:privacy-location")]
    [InlineData(ControlConnectionKind.Bluetooth, null, "添加 / 管理设备", "ms-settings:bluetooth")]
    public async Task FooterPassesOnlyItsExpectedSettingsUriToTheCallback(
        ControlConnectionKind kind, string? settingsUri, string buttonName, string expectedUri)
    {
        var state = Snapshot(kind) with { SettingsUri = settingsUri };
        var source = Source(kind, () => state);
        using var controller = new ConnectionDetailsController(source.Object);
        await controller.RefreshAsync(true);
        var opened = new List<string>();
        OnSta(() =>
        {
            using var popup = new ConnectionDetailsPopup(controller, false, Color.RoyalBlue, 1, opened.Add);
            Click(Button(Layout(popup), buttonName));
        });

        Assert.Equal(expectedUri, Assert.Single(opened));
        VerifyNoCommands(source);
    }

    [Fact]
    public async Task BluetoothManagementButtonUsesSystemSettingsWithoutConnectingADevice()
    {
        var state = Snapshot(ControlConnectionKind.Bluetooth);
        var source = Source(ControlConnectionKind.Bluetooth, () => state);
        using var controller = new ConnectionDetailsController(source.Object);
        await controller.RefreshAsync(true);
        var opened = new List<string>();
        OnSta(() =>
        {
            using var popup = new ConnectionDetailsPopup(controller, true, Color.RoyalBlue, 1, opened.Add);
            Click(Button(Layout(popup), "管理 Wireless Headphones"));
        });

        Assert.Equal("ms-settings:bluetooth", Assert.Single(opened));
        VerifyNoCommands(source);
    }

    private static Mock<IControlConnectionService> Source(ControlConnectionKind kind, Func<ControlConnectionSnapshot> state)
    {
        var source = new Mock<IControlConnectionService>(MockBehavior.Strict);
        source.SetupGet(s => s.Kind).Returns(kind);
        source.Setup(s => s.ReadAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>())).Returns(() => Task.FromResult(state()));
        source.Setup(s => s.Dispose());
        return source;
    }

    private static ControlConnectionSnapshot Snapshot(ControlConnectionKind kind) => kind == ControlConnectionKind.WiFi
        ? new(true,
        [
            new("wifi-connected-id", "Studio Wi-Fi", "已连接 · WPA2 个人网络", true, false, true, SignalPercent: 100),
            new("wifi-open-id", "Guest Wi-Fi", "开放网络", false, true, false, SignalPercent: 80),
            new("wifi-secure-id", "Secure Wi-Fi", "WPA3 个人网络", false, true, false, true, 80),
            new("wifi-garden-id", "Garden", "WPA2 个人网络", false, true, false, true, 60),
            new("wifi-work-id", "Work", "企业网络 · 请使用系统设置连接", false, false, false, SignalPercent: 60),
            new("wifi-neighbour-id", "Neighbour Wi-Fi", "WPA2 个人网络", false, true, false, true, 40)
        ])
        : new(true,
        [
            new("bt-headphones-id", "Wireless Headphones", "音频 / 视频设备 · 已连接", true, false, false, DeviceKind: ControlConnectionDeviceKind.Audio),
            new("bt-keyboard-id", "Keyboard", "输入设备 · 已配对 · 未连接", false, false, false, DeviceKind: ControlConnectionDeviceKind.Keyboard),
            new("bt-mouse-id", "Mouse", "输入设备 · 已配对 · 未连接", false, false, false, DeviceKind: ControlConnectionDeviceKind.Mouse),
            new("bt-phone-id", "Xiaomi 14", "手机 · 已配对 · 未连接", false, false, false, DeviceKind: ControlConnectionDeviceKind.Phone),
            new("bt-device-id", "Bluetooth Device", "已配对 · 未连接", false, false, false)
        ]);

    private static ControlConnectionSnapshot ChangeConnected(ControlConnectionSnapshot state, string id, bool connected) =>
        state with { Items = state.Items.Select(item => item.Id == id
            ? item with { Connected = connected, CanConnect = !connected, CanDisconnect = connected } : item).ToArray() };

    private static void VerifyNoCommands(Mock<IControlConnectionService> source)
    {
        source.Verify(s => s.SetEnabledAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        source.Verify(s => s.ConnectAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        source.Verify(s => s.DisconnectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static Wpf.FrameworkElement Layout(ConnectionDetailsPopup popup)
    {
        var view = popup.ContentView;
        view.Measure(new Wpf.Size(344, 408));
        view.Arrange(new Wpf.Rect(0, 0, 344, 408));
        view.UpdateLayout();
        return view;
    }

    private static WpfControls.Button Button(Wpf.DependencyObject view, string name) =>
        Assert.Single(Descendants<WpfControls.Button>(view), button => Automation.GetName(button) == name);

    private static WpfControls.Primitives.ToggleButton Switch(Wpf.DependencyObject view) =>
        Assert.Single(Descendants<WpfControls.Primitives.ToggleButton>(view),
            toggle => Automation.GetName(toggle) is "Wi-Fi 开关" or "蓝牙 开关");

    private static void Toggle(WpfControls.Primitives.ToggleButton toggle)
    {
        Assert.True(toggle.IsEnabled);
        var peer = new Wpf.Automation.Peers.ToggleButtonAutomationPeer(toggle);
        var provider = Assert.IsAssignableFrom<Wpf.Automation.Provider.IToggleProvider>(
            peer.GetPattern(Wpf.Automation.Peers.PatternInterface.Toggle));
        provider.Toggle();
    }

    private static void Click(WpfControls.Button button)
    {
        Assert.True(button.IsEnabled);
        button.RaiseEvent(new Wpf.RoutedEventArgs(WpfControls.Button.ClickEvent));
    }

    private static WpfControls.StackPanel PasswordPanel(WpfControls.PasswordBox password) =>
        Assert.IsType<WpfControls.StackPanel>(Media.VisualTreeHelper.GetParent(Media.VisualTreeHelper.GetParent(password)));

    private static IEnumerable<T> Descendants<T>(Wpf.DependencyObject root) where T : Wpf.DependencyObject
    {
        if (root is T match) yield return match;
        for (var index = 0; index < Media.VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var descendant in Descendants<T>(Media.VisualTreeHelper.GetChild(root, index))) yield return descendant;
    }

    private static void SaveImage(Wpf.FrameworkElement view, string name)
    {
        var bitmap = new Imaging.RenderTargetBitmap(344, 408, 96, 96, Media.PixelFormats.Pbgra32);
        bitmap.Render(view);
        var encoder = new Imaging.PngBitmapEncoder();
        encoder.Frames.Add(Imaging.BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(AppContext.BaseDirectory, name));
        encoder.Save(output);
    }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
