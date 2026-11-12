using System.Diagnostics;
using System.Drawing;
using System.Runtime.ExceptionServices;
using CrabDesk.Core;
using CrabDesk.Native;
using CrabDesk.Runtime;
using Moq;
using Xunit;
using Forms = System.Windows.Forms;
using Wpf = System.Windows;
using WpfControls = System.Windows.Controls;
using Media = System.Windows.Media;

namespace CrabDesk.WinUI.Tests;

[Collection("desktop-probe")]
public sealed class PopupLoadingPresenterTests
{
    public static IEnumerable<object[]> Cases =>
        from kind in new[] { "wifi", "bluetooth", "adapters", "display", "mixer", "microphone" }
        from animate in new[] { false, true }
        select new object[] { kind, animate };

    [Theory]
    [MemberData(nameof(Cases))]
    public void FirstReadRevealsContentWithoutResizingAndRefreshKeepsItVisible(string kind, bool animate) => OnSta(() =>
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = Create(kind, animate, gate);
        using var popup = fixture.Popup;
        try
        {
            popup.Location = new Point(-12000, -12000);
            popup.Show();
            var loading = Assert.Single(Descendants<PopupLoadingPresenter>(fixture.View));
            Pump(350, () => Assert.True(loading.IsLoading && loading.SkeletonVisible));
            var bounds = popup.Bounds;
            var bodySize = loading.RenderSize;
            Assert.Contains(Descendants<WpfControls.Button>(fixture.View), button => button.IsVisible && button.IsEnabled);
            var samples = new List<double>();
            gate.SetResult(true);
            Until(() => !loading.IsLoading, () => { });
            Pump(350, () =>
            {
                samples.Add(loading.ContentOpacity);
                Assert.Equal(bounds, popup.Bounds);
                Assert.Equal(bodySize, loading.RenderSize);
            });
            Assert.False(loading.IsTransitioning);
            Assert.False(loading.SkeletonVisible);
            Assert.Equal(1, loading.ContentOpacity);
            if (animate && Wpf.SystemParameters.ClientAreaAnimation)
                Assert.Contains(samples, value => value is > 0 and < 1);
            else
                Assert.All(samples, value => Assert.Equal(1, value));

            var controls = Descendants<WpfControls.Button>(fixture.View).ToArray();
            var scroll = Descendants<WpfControls.ScrollViewer>(fixture.View).FirstOrDefault();
            scroll?.ScrollToVerticalOffset(70);
            fixture.View.UpdateLayout();
            var offset = scroll?.VerticalOffset;
            var refresh = fixture.Refresh();
            Until(() => refresh.IsCompleted, () =>
            {
                Assert.False(loading.IsLoading || loading.IsTransitioning || loading.SkeletonVisible);
                Assert.Equal(1, loading.ContentOpacity);
            });
            refresh.GetAwaiter().GetResult();
            fixture.View.UpdateLayout();
            Assert.Equal(controls, Descendants<WpfControls.Button>(fixture.View).ToArray());
            Assert.Equal(offset, scroll?.VerticalOffset);
        }
        finally { gate.TrySetResult(true); }
    });

    [Theory]
    [InlineData("wifi")]
    [InlineData("bluetooth")]
    [InlineData("adapters")]
    [InlineData("display")]
    [InlineData("mixer")]
    [InlineData("microphone")]
    public void FailedFirstReadReleasesSkeletonAndShowsItsError(string kind) => OnSta(() =>
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = Create(kind, false, gate, fail: true);
        using var popup = fixture.Popup;
        try
        {
            popup.Location = new Point(-12000, -12000);
            popup.Show();
            var loading = Assert.Single(Descendants<PopupLoadingPresenter>(fixture.View));
            Pump(150, () => Assert.True(loading.IsLoading));
            gate.SetResult(true);
            Until(() => !loading.IsLoading, () => { });
            Assert.False(loading.SkeletonVisible);
            Assert.Equal(1, loading.ContentOpacity);
            Assert.Contains(Descendants<WpfControls.TextBlock>(fixture.View),
                text => text.IsVisible && (text.Text.Contains("失败") || text.Text.Contains("无法")));
        }
        finally { gate.TrySetResult(true); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClosingWhileReadingOrRevealingStopsAnimationsAndIgnoresLateResults(bool revealing) => OnSta(() =>
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = Create("wifi", true, gate);
        using var popup = fixture.Popup;
        try
        {
            popup.Location = new Point(-12000, -12000);
            popup.Show();
            var loading = Assert.Single(Descendants<PopupLoadingPresenter>(fixture.View));
            Pump(180, () => { });
            if (revealing)
            {
                gate.SetResult(true);
                Until(() => !loading.IsLoading, () => { });
            }
            popup.Close();
            gate.TrySetResult(true);
            Pump(350, () =>
            {
                Assert.True(popup.IsDisposed);
                Assert.False(loading.IsTransitioning || loading.SkeletonVisible);
            });
        }
        finally { gate.TrySetResult(true); }
    });

    [Fact]
    public void CachedAndEmptyResultsAreReadyForTheFirstFrame() => OnSta(() =>
    {
        var source = new Mock<IControlConnectionService>(MockBehavior.Strict);
        source.SetupGet(service => service.Kind).Returns(ControlConnectionKind.WiFi);
        source.Setup(service => service.ReadAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ControlConnectionSnapshot(true, []));
        source.Setup(service => service.Dispose());
        using var controller = new ConnectionDetailsController(source.Object);
        controller.RefreshAsync().GetAwaiter().GetResult();
        using var popup = new ConnectionDetailsPopup(controller, true, Color.CornflowerBlue, 1, _ => { });
        popup.ContentView.Measure(new Wpf.Size(344, 408));
        popup.ContentView.Arrange(new Wpf.Rect(0, 0, 344, 408));
        var loading = Assert.Single(Descendants<PopupLoadingPresenter>(popup.ContentView));
        Assert.False(loading.IsLoading || loading.IsTransitioning || loading.SkeletonVisible);
        Assert.Equal(1, loading.ContentOpacity);
        Assert.Contains(Descendants<WpfControls.TextBlock>(popup.ContentView), label => label.Text == "暂无可用网络");
    });

    private sealed record Fixture(Forms.Form Popup, Wpf.FrameworkElement View, Func<Task> Refresh);

    private static Fixture Create(string kind, bool animate, TaskCompletionSource<bool> gate, bool fail = false)
    {
        void Wait()
        {
            if (!gate.Task.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Test read was not released.");
            if (fail) throw new InvalidOperationException("Test read failed.");
        }
        if (kind is "wifi" or "bluetooth")
        {
            var source = new Mock<IControlConnectionService>(MockBehavior.Strict);
            source.SetupGet(service => service.Kind).Returns(kind == "wifi" ? ControlConnectionKind.WiFi : ControlConnectionKind.Bluetooth);
            source.Setup(service => service.ReadAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .Returns(async (bool scan, CancellationToken token) =>
                {
                    await gate.Task.WaitAsync(token);
                    if (fail) throw new InvalidOperationException("Test read failed.");
                    return new ControlConnectionSnapshot(true, Enumerable.Range(0, 8)
                        .Select(index => new ControlConnectionItem(index.ToString(), $"Device {index}", "Available", false, true, false)).ToArray());
                });
            source.Setup(service => service.Dispose());
            var controller = new ConnectionDetailsController(source.Object);
            var popup = new ConnectionDetailsPopup(controller, true, Color.CornflowerBlue, 1, _ => { }, animate);
            return new(popup, popup.ContentView, () => controller.RefreshAsync());
        }
        if (kind == "adapters")
        {
            var source = new Mock<INetworkAdapterControl>(MockBehavior.Strict);
            var adapters = Enumerable.Range(0, 8).Select(index =>
                new ControlNetworkAdapter(Guid.NewGuid(), $"Adapter {index}", "Ethernet", true, true, false)).ToArray();
            source.Setup(control => control.Read()).Returns(() => { Wait(); return adapters; });
            var popup = new NetworkAdaptersPopup(source.Object, true, Color.CornflowerBlue, 1, _ => { }, animate);
            return new(popup, popup.ContentView, popup.RefreshAsync);
        }
        if (kind == "display")
        {
            var source = new Mock<IDisplayControl>(MockBehavior.Strict);
            source.Setup(control => control.Read()).Returns(() => { Wait(); return new(ControlDisplayMode.Extend, 2); });
            var popup = new DisplayControlsPopup(source.Object, true, Color.CornflowerBlue, 1, animationsEnabled: animate);
            return new(popup, popup.ContentView, popup.RefreshAsync);
        }
        if (kind == "mixer")
        {
            var source = new Mock<IAudioMixer>(MockBehavior.Strict);
            source.Setup(control => control.Read()).Returns(() =>
            {
                Wait();
                return new("output", "Headphones", 50, false, Enumerable.Range(0, 8)
                    .Select(index => new AudioMixerApplication(index.ToString(), $"Application {index}", "", 40, false)).ToArray());
            });
            var controller = new AudioMixerController(source.Object);
            var popup = new AudioMixerPopup(controller, true, Color.CornflowerBlue, 1, animationsEnabled: animate);
            return new(popup, popup.ContentView, controller.RefreshAsync);
        }
        var microphone = new Mock<IMicrophone>(MockBehavior.Strict);
        microphone.Setup(control => control.Read()).Returns(() => { Wait(); return new("input", "Microphone", 60, false, 10, [new("input", "Microphone")]); });
        var micController = new MicrophoneController(microphone.Object);
        var micPopup = new MicrophonePopup(micController, true, Color.CornflowerBlue, 1, animationsEnabled: animate);
        return new(micPopup, micPopup.ContentView, micController.RefreshAsync);
    }

    private static IEnumerable<T> Descendants<T>(Wpf.DependencyObject root) where T : Wpf.DependencyObject
    {
        if (root is T match) yield return match;
        for (var index = 0; index < Media.VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants<T>(Media.VisualTreeHelper.GetChild(root, index))) yield return child;
    }

    private static void Until(Func<bool> done, Action sample)
    {
        var timer = Stopwatch.StartNew();
        while (!done())
        {
            Assert.True(timer.ElapsedMilliseconds < 5000, "Popup did not finish loading.");
            Pump(8, sample);
        }
    }

    private static void Pump(int milliseconds, Action sample)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < milliseconds)
        {
            Forms.Application.DoEvents();
            Wpf.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, Wpf.Threading.DispatcherPriority.Background);
            sample();
            Thread.Sleep(8);
        }
    }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(
                    new Wpf.Threading.DispatcherSynchronizationContext(Wpf.Threading.Dispatcher.CurrentDispatcher));
                action();
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
