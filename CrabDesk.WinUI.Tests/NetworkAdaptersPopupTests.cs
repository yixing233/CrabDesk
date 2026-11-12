using System.Diagnostics;
using System.Drawing;
using System.Reflection;
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

public sealed class NetworkAdaptersPopupTests
{
    [Fact]
    public void PassiveRefreshPreservesEnabledControlsTheirContentAndScrollPosition() => OnSta(() =>
    {
        using var release = new ManualResetEventSlim();
        var reads = 0;
        var source = new Mock<INetworkAdapterControl>();
        source.Setup(control => control.Read()).Returns(() =>
        {
            if (Interlocked.Increment(ref reads) > 1)
                Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            return Adapters();
        });
        using var popup = Popup(source.Object);
        Wait(popup.RefreshAsync());
        Layout(popup);
        var scroll = Assert.Single(Descendants<WpfControls.ScrollViewer>(popup.ContentView));
        var rows = Assert.IsType<WpfControls.StackPanel>(scroll.Content);
        var originalRows = rows.Children.Cast<Wpf.UIElement>().ToArray();
        var button = Button(popup, "禁用 " + Adapters()[0].Name);
        var content = button.Content;
        var settings = Button(popup, "网络设置");
        var scan = Button(popup, "刷新网卡列表");
        scroll.ScrollToVerticalOffset(120);
        popup.ContentView.UpdateLayout();
        var offset = scroll.VerticalOffset;
        var enabledChanges = 0;
        button.IsEnabledChanged += (_, _) => enabledChanges++;
        settings.IsEnabledChanged += (_, _) => enabledChanges++;
        scan.IsEnabledChanged += (_, _) => enabledChanges++;

        var pending = popup.RefreshAsync();
        try
        {
            Assert.True(button.IsEnabled);
            Assert.True(settings.IsEnabled);
            Assert.True(scan.IsEnabled);
            Assert.Same(content, button.Content);
        }
        finally
        {
            release.Set();
            Wait(pending);
        }

        Layout(popup);
        Assert.Equal(0, enabledChanges);
        Assert.Equal(originalRows, rows.Children.Cast<Wpf.UIElement>().ToArray());
        Assert.Same(content, button.Content);
        Assert.Equal(offset, scroll.VerticalOffset);
        source.Verify(control => control.SetEnabledAsync(It.IsAny<Guid>(), It.IsAny<bool>()), Times.Never);
    });

    [Fact]
    public void AClickDuringPassiveRefreshRunsOnceAfterTheReadCompletes() => OnSta(() =>
    {
        using var release = new ManualResetEventSlim();
        var reads = 0;
        var adapter = Adapters()[0];
        var source = new Mock<INetworkAdapterControl>();
        source.Setup(control => control.Read()).Returns(() =>
        {
            if (Interlocked.Increment(ref reads) == 2)
                Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            return new[] { adapter };
        });
        source.Setup(control => control.SetEnabledAsync(adapter.Id, false)).Returns(() =>
        {
            adapter = adapter with { Enabled = false };
            return Task.FromResult(ControlOperationResult.Success);
        });
        using var popup = Popup(source.Object);
        Wait(popup.RefreshAsync());
        var button = Button(popup, "禁用 " + adapter.Name);
        var reading = popup.RefreshAsync();
        Task pending;
        try
        {
            Click(button);
            pending = popup.PendingOperation;
            Assert.NotSame(reading, pending);
            Assert.False(button.IsEnabled);
            Click(button);
            source.Verify(control => control.SetEnabledAsync(It.IsAny<Guid>(), It.IsAny<bool>()), Times.Never);
        }
        finally { release.Set(); }
        Wait(pending);

        Assert.Same(button, Button(popup, "启用 " + adapter.Name));
        Assert.True(button.IsEnabled);
        source.Verify(control => control.SetEnabledAsync(adapter.Id, false), Times.Once);
        source.Verify(control => control.Read(), Times.Exactly(3));
    });

    [Fact]
    public void AConnectionChangeUpdatesTheStatusWithoutRecreatingTheAction() => OnSta(() =>
    {
        var adapter = Adapters()[0];
        var source = new Mock<INetworkAdapterControl>();
        source.Setup(control => control.Read()).Returns(() => new[] { adapter });
        using var popup = Popup(source.Object);
        Wait(popup.RefreshAsync());
        var button = Button(popup, "禁用 " + adapter.Name);
        var content = button.Content;

        adapter = adapter with { Connected = true };
        Wait(popup.RefreshAsync());

        Assert.Same(content, button.Content);
        Assert.Contains(Descendants<WpfControls.TextBlock>(popup.ContentView), label => label.Text == "已启用 · 已连接");
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ListRowsHaveEqualOuterInsetsWithAndWithoutTheScrollbar(bool overflow) => OnSta(() =>
    {
        var source = new Mock<INetworkAdapterControl>();
        source.Setup(control => control.Read()).Returns(Adapters().Take(overflow ? 8 : 2).ToArray());
        using var popup = Popup(source.Object);
        Wait(popup.RefreshAsync());
        Layout(popup);
        var scroll = Assert.Single(Descendants<WpfControls.ScrollViewer>(popup.ContentView));
        var rows = Assert.IsType<WpfControls.StackPanel>(scroll.Content);
        var row = Assert.IsType<WpfControls.Border>(rows.Children[0]);
        var left = row.TranslatePoint(new Wpf.Point(), popup.ContentView).X;
        var right = popup.ContentView.ActualWidth - left - row.ActualWidth;
        Assert.Equal(left, right, 1);
        var bar = Assert.Single(Descendants<WpfControls.Primitives.ScrollBar>(scroll),
            element => element.Orientation == WpfControls.Orientation.Vertical);
        Assert.Equal(overflow ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed, bar.Visibility);
        if (overflow)
        {
            var barLeft = bar.TranslatePoint(new Wpf.Point(), popup.ContentView).X;
            Assert.True(barLeft >= left + row.ActualWidth,
                $"Scrollbar starts at {barLeft:F2}; row ends at {left + row.ActualWidth:F2}; scrollbar width is {bar.ActualWidth:F2}.");
            Assert.True(barLeft + bar.ActualWidth <= popup.ContentView.ActualWidth - 2,
                $"Scrollbar ends at {barLeft + bar.ActualWidth:F2}; card width is {popup.ContentView.ActualWidth:F2}.");
            var thumb = Assert.Single(Descendants<WpfControls.Primitives.Thumb>(bar));
            thumb.RaiseEvent(new WpfControls.Primitives.DragStartedEventArgs(0, 0)
            {
                RoutedEvent = WpfControls.Primitives.Thumb.DragStartedEvent
            });
            thumb.RaiseEvent(new WpfControls.Primitives.DragDeltaEventArgs(0, 40)
            {
                RoutedEvent = WpfControls.Primitives.Thumb.DragDeltaEvent
            });
            thumb.RaiseEvent(new WpfControls.Primitives.DragCompletedEventArgs(0, 40, false)
            {
                RoutedEvent = WpfControls.Primitives.Thumb.DragCompletedEvent
            });
            Layout(popup);
            Assert.True(scroll.VerticalOffset > 0);
        }
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void CardRendersAdapterAdministrationStateAndInheritedBackground(bool dark, bool empty) => OnSta(() =>
    {
        var source = new Mock<INetworkAdapterControl>(MockBehavior.Strict);
        var adapters = empty ? [] : Adapters();
        source.Setup(control => control.Read()).Returns(adapters);
        var background = dark ? Color.FromArgb(28, 37, 53) : Color.FromArgb(241, 245, 249);
        using var popup = Popup(source.Object, dark, background);

        Wait(popup.RefreshAsync());
        Layout(popup);

        Assert.Equal(background.ToArgb(), popup.BackColor.ToArgb());
        var brush = Assert.IsType<Media.SolidColorBrush>(Assert.IsType<WpfControls.Border>(popup.ContentView).Background);
        Assert.Equal(Media.Color.FromRgb(background.R, background.G, background.B), brush.Color);
        var labels = Descendants<WpfControls.TextBlock>(popup.ContentView).ToArray();
        if (empty)
            Assert.Contains(labels, label => label.Text == "未找到可用网卡");
        else
        {
            Assert.Contains(labels, label => label.Text == "已启用 · 未连接");
            Assert.Contains(labels, label => label.Text == "已禁用");
            Assert.Contains(labels, label => label.Text == "不可用");
            Assert.True(Button(popup, "禁用 " + adapters[0].Name).IsEnabled);
            Assert.True(Button(popup, "启用 " + adapters[1].Name).IsEnabled);
            Assert.False(Button(popup, "启用 " + adapters[2].Name).IsEnabled);
            Assert.All(labels.Where(label => adapters.Any(adapter => adapter.Name == label.Text)), label =>
            {
                Assert.True(label.ActualWidth > 100);
                Assert.True(label.ActualWidth < 220);
                Assert.Equal(Wpf.TextTrimming.CharacterEllipsis, label.TextTrimming);
            });
            Assert.True(Assert.Single(Descendants<WpfControls.ScrollViewer>(popup.ContentView)).ScrollableHeight > 0);
        }
        var bitmap = new Imaging.RenderTargetBitmap(360, 408, 96, 96, Media.PixelFormats.Pbgra32);
        bitmap.Render(popup.ContentView);
        var encoder = new Imaging.PngBitmapEncoder();
        encoder.Frames.Add(Imaging.BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(AppContext.BaseDirectory,
            $"network-adapters-{(dark ? "dark" : "light")}{(empty ? "-empty" : "")}.png"));
        encoder.Save(file);
        source.Verify(control => control.Read(), Times.Once);
        source.VerifyNoOtherCalls();
    });

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void PerAdapterCommandKeepsCardOpenAndReadsBackConfirmedState(bool initialEnabled, bool succeeds) => OnSta(() =>
    {
        var state = Adapters()[0] with { Enabled = initialEnabled };
        var source = new Mock<INetworkAdapterControl>(MockBehavior.Strict);
        source.Setup(control => control.Read()).Returns(() => new[] { state });
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var uiThread = Environment.CurrentManagedThreadId;
        var commandThread = uiThread;
        source.Setup(control => control.SetEnabledAsync(state.Id, !initialEnabled)).Returns(async () =>
        {
            commandThread = Environment.CurrentManagedThreadId;
            await release.Task;
            if (succeeds) state = state with { Enabled = !initialEnabled };
            return succeeds ? ControlOperationResult.Success : new(false, "已取消网卡操作。");
        });
        using var popup = Popup(source.Object);
        Wait(popup.RefreshAsync());
        var button = Button(popup, (initialEnabled ? "禁用 " : "启用 ") + state.Name);

        Click(button);
        Assert.False(button.IsEnabled);
        Assert.False(Button(popup, "刷新网卡列表").IsEnabled);
        Click(button);
        typeof(System.Windows.Forms.Form).GetMethod("OnDeactivate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(popup, new object[] { EventArgs.Empty });
        Assert.False(popup.IsDisposed);
        var pending = popup.PendingOperation;
        Assert.Same(pending, popup.RefreshAsync());
        release.SetResult();
        Wait(pending);

        Assert.NotEqual(uiThread, commandThread);
        Assert.Same(button, Button(popup, (state.Enabled is true ? "禁用 " : "启用 ") + state.Name));
        Assert.True(button.IsEnabled);
        Assert.True(Button(popup, "刷新网卡列表").IsEnabled);
        Assert.Equal(succeeds ? !initialEnabled : initialEnabled, state.Enabled);
        if (!succeeds)
            Assert.Contains(Descendants<WpfControls.TextBlock>(popup.ContentView), label => label.Text == "已取消网卡操作。");
        source.Verify(control => control.SetEnabledAsync(state.Id, !initialEnabled), Times.Once);
        source.Verify(control => control.Read(), Times.Exactly(2));
        source.VerifyNoOtherCalls();
    });

    [Fact]
    public void ReadFailurePreservesExistingRowsAndAllowsRetry() => OnSta(() =>
    {
        var source = new Mock<INetworkAdapterControl>();
        source.SetupSequence(control => control.Read()).Returns(Adapters())
            .Throws(new InvalidOperationException("设备读取失败")).Returns(Array.Empty<ControlNetworkAdapter>());
        using var popup = Popup(source.Object);
        Wait(popup.RefreshAsync());
        var button = Button(popup, "禁用 " + Adapters()[0].Name);

        Wait(popup.RefreshAsync());

        Assert.Same(button, Button(popup, "禁用 " + Adapters()[0].Name));
        Assert.Contains(Descendants<WpfControls.TextBlock>(popup.ContentView), label => label.Text.Contains("设备读取失败"));
        Wait(popup.RefreshAsync());
        Assert.Contains(Descendants<WpfControls.TextBlock>(popup.ContentView), label => label.Text == "未找到可用网卡");
        Assert.DoesNotContain(Descendants<WpfControls.TextBlock>(popup.ContentView), label => label.Text.Contains("设备读取失败"));
    });

    [Fact]
    public void SuccessfulCommandStillRequiresReadBackToMatchTheRequestedState() => OnSta(() =>
    {
        var adapter = Adapters()[0];
        var source = new Mock<INetworkAdapterControl>();
        source.Setup(control => control.Read()).Returns(new[] { adapter });
        source.Setup(control => control.SetEnabledAsync(adapter.Id, false)).ReturnsAsync(ControlOperationResult.Success);
        using var popup = Popup(source.Object);
        Wait(popup.RefreshAsync());

        Click(Button(popup, "禁用 " + adapter.Name));
        Wait(popup.PendingOperation);

        Assert.True(Button(popup, "禁用 " + adapter.Name).IsEnabled);
        Assert.Contains(Descendants<WpfControls.TextBlock>(popup.ContentView), label => label.Text.Contains("状态尚未更新"));
        source.Verify(control => control.SetEnabledAsync(adapter.Id, false), Times.Once);
    });

    [Fact]
    public async Task InvalidAdapterNeverStartsAnElevationRequest()
    {
        var result = await new NetworkAdapterControl().SetEnabledAsync(Guid.Empty, false);
        Assert.False(result.Succeeded);
        Assert.Contains("标识无效", result.Error);
    }

    private static NetworkAdaptersPopup Popup(INetworkAdapterControl source, bool dark = true, Color? background = null) =>
        new(source, dark, Color.FromArgb(83, 152, 225), 1, _ => { }, animationsEnabled: false, background: background);

    private static ControlNetworkAdapter[] Adapters() => Enumerable.Range(0, 8).Select(index => new ControlNetworkAdapter(
        new Guid(index + 1, 0, 0, new byte[8]), index == 0 ? "以太网 · Intel Ethernet Controller · Office Network" :
            index == 1 ? "WLAN" : "网络适配器 " + index, "设备描述 " + index,
        index == 2 ? null : index != 1, false, index == 1)).ToArray();

    private static WpfControls.Button Button(NetworkAdaptersPopup popup, string name)
    {
        Layout(popup);
        return Assert.Single(Descendants<WpfControls.Button>(popup.ContentView), button => Automation.GetName(button) == name);
    }

    private static void Click(WpfControls.Button button) => button.RaiseEvent(new Wpf.RoutedEventArgs(WpfControls.Button.ClickEvent));

    private static void Layout(NetworkAdaptersPopup popup)
    {
        popup.ContentView.Measure(new Wpf.Size(360, 408));
        popup.ContentView.Arrange(new Wpf.Rect(0, 0, 360, 408));
        popup.ContentView.UpdateLayout();
    }

    private static void Wait(Task task)
    {
        var elapsed = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(8));
            Wpf.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, Wpf.Threading.DispatcherPriority.Background);
            Thread.Sleep(1);
        }
        task.GetAwaiter().GetResult();
    }

    private static IEnumerable<T> Descendants<T>(Wpf.DependencyObject parent) where T : Wpf.DependencyObject
    {
        if (parent is T match) yield return match;
        for (var index = 0; index < Media.VisualTreeHelper.GetChildrenCount(parent); index++)
            foreach (var child in Descendants<T>(Media.VisualTreeHelper.GetChild(parent, index))) yield return child;
    }

    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception exception) { error = exception; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
