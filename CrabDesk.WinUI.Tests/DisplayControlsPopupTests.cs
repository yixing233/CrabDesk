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
using Imaging = System.Windows.Media.Imaging;
using Automation = System.Windows.Automation.AutomationProperties;

namespace CrabDesk.WinUI.Tests;

public sealed class DisplayControlsPopupTests
{
    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    public void CardRendersSelectedModeAndAvailableActions(bool dark, int displays) => OnSta(() =>
    {
        var source = new Mock<IDisplayControl>(MockBehavior.Strict);
        using var popup = new DisplayControlsPopup(source.Object, dark, Color.FromArgb(79, 100, 154), 1);
        var selected = displays == 2 ? ControlDisplayMode.Extend : ControlDisplayMode.Internal;
        popup.ApplySnapshot(new(selected, displays));
        Layout(popup);
        var modes = ModeButtons(popup);
        Assert.Equal(4, modes.Length);
        Assert.Equal(selected, Assert.Single(modes, button => Automation.GetItemStatus(button) == "已选中").Tag);
        Assert.All(modes, button => Assert.Equal(displays >= 2 || (ControlDisplayMode)button.Tag == ControlDisplayMode.Internal, button.IsEnabled));
        Assert.All(modes, button => Assert.True(button.ActualWidth > 285));
        Assert.Contains(Descendants<WpfControls.TextBlock>(popup.ContentView), label => label.Text == $"已连接 {displays} 台屏幕");
        Assert.True(Button(popup, "连接无线显示器").IsEnabled);
        Assert.True(Button(popup, "打开显示设置").IsEnabled);
        Assert.Equal(0, Assert.Single(Descendants<WpfControls.ScrollViewer>(popup.ContentView)).ScrollableHeight);

        var bitmap = new Imaging.RenderTargetBitmap(344, 330, 96, 96, Media.PixelFormats.Pbgra32);
        bitmap.Render(popup.ContentView);
        var encoder = new Imaging.PngBitmapEncoder(); encoder.Frames.Add(Imaging.BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(AppContext.BaseDirectory,
            $"display-controls-{(dark ? "dark" : "light")}{(displays == 1 ? "-single" : "")}.png"));
        encoder.Save(file);
        source.VerifyNoOtherCalls();
    });

    [Fact]
    public void ModeClickAppliesOffTheUiThreadAndReadsBackConfirmedSelection() => OnSta(() =>
    {
        var uiThread = Environment.CurrentManagedThreadId;
        var applyThread = uiThread;
        var state = new ControlDisplaySnapshot(ControlDisplayMode.Extend, 2);
        using var release = new ManualResetEventSlim();
        var source = new Mock<IDisplayControl>();
        source.Setup(s => s.Read()).Returns(() => state);
        source.Setup(s => s.SetMode(ControlDisplayMode.Clone)).Returns(() =>
        {
            applyThread = Environment.CurrentManagedThreadId;
            release.Wait(TimeSpan.FromSeconds(5));
            state = state with { Mode = ControlDisplayMode.Clone };
            return ControlOperationResult.Success;
        });
        using var popup = new DisplayControlsPopup(source.Object, false, Color.RoyalBlue, 1);
        popup.ApplySnapshot(state); Layout(popup);
        try
        {
            Click(Button(popup, "复制"));
            Assert.All(ModeButtons(popup), button => Assert.False(button.IsEnabled));
            Assert.False(Button(popup, "连接无线显示器").IsEnabled);
            Assert.Equal(ControlDisplayMode.Extend,
                Assert.Single(ModeButtons(popup), button => Automation.GetItemStatus(button) == "已选中").Tag);
        }
        finally { release.Set(); }
        Wait(popup.PendingOperation);
        Assert.NotEqual(uiThread, applyThread);
        Assert.Equal(ControlDisplayMode.Clone,
            Assert.Single(ModeButtons(popup), button => Automation.GetItemStatus(button) == "已选中").Tag);
        Assert.All(ModeButtons(popup), button => Assert.True(button.IsEnabled));
        source.Verify(s => s.SetMode(ControlDisplayMode.Clone), Times.Once);
        source.Verify(s => s.Read(), Times.Once);
    });

    [Fact]
    public void FailedModeChangeKeepsTheReadBackModeAndExplainsTheFailure() => OnSta(() =>
    {
        var state = new ControlDisplaySnapshot(ControlDisplayMode.Extend, 2);
        var source = new Mock<IDisplayControl>(); source.Setup(s => s.Read()).Returns(state);
        source.Setup(s => s.SetMode(ControlDisplayMode.Clone)).Returns(new ControlOperationResult(false, "屏幕不支持复制模式"));
        using var popup = new DisplayControlsPopup(source.Object, true, Color.RoyalBlue, 1);
        popup.ApplySnapshot(state); Layout(popup);
        Click(Button(popup, "复制")); Wait(popup.PendingOperation);
        Assert.Equal(ControlDisplayMode.Extend,
            Assert.Single(ModeButtons(popup), button => Automation.GetItemStatus(button) == "已选中").Tag);
        Assert.Contains(Descendants<WpfControls.TextBlock>(popup.ContentView), label => label.Text == "屏幕不支持复制模式");
        source.Verify(s => s.Read(), Times.Once);
    });

    [Fact]
    public void UnavailableSecondScreenCannotDispatchOtherModes() => OnSta(() =>
    {
        var source = new Mock<IDisplayControl>(MockBehavior.Strict);
        using var popup = new DisplayControlsPopup(source.Object, false, Color.RoyalBlue, 1);
        popup.ApplySnapshot(new(ControlDisplayMode.Internal, 1)); Layout(popup);
        // Routed events can bypass WPF's input filtering; the action guard must
        // still reject an unavailable mode.
        foreach (var button in ModeButtons(popup)) { Click(button); Wait(popup.PendingOperation); }
        source.Verify(s => s.SetMode(It.IsAny<ControlDisplayMode>()), Times.Never);
    });

    [Fact]
    public void ReadFailureDisablesModeChangesAndLeavesShellActionsAvailable() => OnSta(() =>
    {
        var source = new Mock<IDisplayControl>(); source.Setup(s => s.Read()).Throws(new InvalidOperationException("显示服务不可用"));
        using var popup = new DisplayControlsPopup(source.Object, false, Color.RoyalBlue, 1);
        Wait(popup.RefreshAsync()); Layout(popup);
        Assert.All(ModeButtons(popup), button => Assert.False(button.IsEnabled));
        Assert.DoesNotContain(ModeButtons(popup), button => Automation.GetItemStatus(button) == "已选中");
        Assert.Contains(Descendants<WpfControls.TextBlock>(popup.ContentView), label => label.Text.Contains("显示服务不可用"));
        Assert.True(Button(popup, "连接无线显示器").IsEnabled);
        Assert.True(Button(popup, "打开显示设置").IsEnabled);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ShellActionRunsAfterDismissalAndReportsFailureToTheHost(bool cast) => OnSta(() =>
    {
        var source = new Mock<IDisplayControl>();
        using var popup = new DisplayControlsPopup(source.Object, false, Color.RoyalBlue, 1);
        var closedBeforeAction = false;
        string? failure = null;
        ControlOperationResult Open()
        {
            closedBeforeAction = popup.IsDisposed;
            return new(false, "无法打开系统界面");
        }
        source.Setup(s => s.OpenCast()).Returns(Open);
        source.Setup(s => s.OpenSettings()).Returns(Open);
        popup.ExternalActionFailed += error => failure = error;
        popup.ApplySnapshot(new(ControlDisplayMode.Internal, 1)); Layout(popup);
        Click(Button(popup, cast ? "连接无线显示器" : "打开显示设置"));
        Wait(popup.PendingOperation);
        Assert.True(closedBeforeAction);
        Assert.Equal("无法打开系统界面", failure);
        source.Verify(s => s.OpenCast(), cast ? Times.Once() : Times.Never());
        source.Verify(s => s.OpenSettings(), cast ? Times.Never() : Times.Once());
    });

    [Fact]
    public void DismissedCardDoesNotReadOrUpdateItsUiAfterAnApplyCompletes() => OnSta(() =>
    {
        using var release = new ManualResetEventSlim();
        var source = new Mock<IDisplayControl>();
        source.Setup(s => s.SetMode(ControlDisplayMode.Clone)).Returns(() =>
        {
            release.Wait(TimeSpan.FromSeconds(5));
            return ControlOperationResult.Success;
        });
        using var popup = new DisplayControlsPopup(source.Object, false, Color.RoyalBlue, 1);
        popup.ApplySnapshot(new(ControlDisplayMode.Extend, 2)); Layout(popup);
        try { Click(Button(popup, "复制")); popup.Dismiss(); }
        finally { release.Set(); }
        Wait(popup.PendingOperation);
        source.Verify(s => s.Read(), Times.Never);
    });

    private static void Layout(DisplayControlsPopup popup)
    {
        popup.ContentView.Measure(new Wpf.Size(344, 330));
        popup.ContentView.Arrange(new Wpf.Rect(0, 0, 344, 330));
        popup.ContentView.UpdateLayout();
    }

    private static WpfControls.Button[] ModeButtons(DisplayControlsPopup popup) =>
        Descendants<WpfControls.Button>(popup.ContentView).Where(button => button.Tag is ControlDisplayMode).ToArray();

    private static WpfControls.Button Button(DisplayControlsPopup popup, string name) =>
        Assert.Single(Descendants<WpfControls.Button>(popup.ContentView), button => Automation.GetName(button) == name);

    private static void Click(WpfControls.Button button) => button.RaiseEvent(new Wpf.RoutedEventArgs(WpfControls.Button.ClickEvent));

    private static IEnumerable<T> Descendants<T>(Wpf.DependencyObject root) where T : Wpf.DependencyObject
    {
        for (var index = 0; index < Media.VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = Media.VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void Wait(Task task)
    {
        var watch = Stopwatch.StartNew();
        while (!task.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(8))
        {
            Forms.Application.DoEvents();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            Thread.Sleep(5);
        }
        Assert.True(task.IsCompleted, "Display popup operation did not finish.");
        task.GetAwaiter().GetResult();
    }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
