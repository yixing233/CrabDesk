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

public sealed class MicrophonePopupTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task PopupRendersCaptureControlsAndUnavailableState(bool dark, bool unavailable)
    {
        var source = new Mock<IMicrophone>();
        source.Setup(s => s.Read()).Returns(unavailable ? MicrophoneSnapshot.Empty : MicrophoneControllerTests.Snapshot() with { Muted = false });
        using var controller = new MicrophoneController(source.Object);
        await controller.RefreshAsync();
        OnSta(() =>
        {
            using var popup = new MicrophonePopup(controller, dark, Color.FromArgb(79, 100, 154), 1);
            var view = popup.ContentView;
            view.Measure(new Wpf.Size(320, 194)); view.Arrange(new Wpf.Rect(0, 0, 320, 194)); view.UpdateLayout();
            var slider = Assert.Single(Descendants<WpfControls.Slider>(view));
            Assert.Equal(!unavailable, slider.IsEnabled);
            Assert.True(slider.ActualWidth > 230);
            if (!unavailable) Assert.Equal(35, slider.Value);
            Assert.Contains(Descendants<WpfControls.Button>(view), button => Automation.GetName(button) == "选择麦克风输入设备");
            var bitmap = new Imaging.RenderTargetBitmap(320, 194, 96, 96, Media.PixelFormats.Pbgra32);
            bitmap.Render(view);
            var encoder = new Imaging.PngBitmapEncoder(); encoder.Frames.Add(Imaging.BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(AppContext.BaseDirectory, $"microphone-{(dark ? "dark" : "light")}{(unavailable ? "-empty" : "")}.png"));
            encoder.Save(file);
        });
        source.Verify(s => s.SetVolume(It.IsAny<string>(), It.IsAny<double>()), Times.Never);
        source.Verify(s => s.SetMuted(It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task PopupVolumeAndMuteDispatchTheirControls()
    {
        var state = MicrophoneControllerTests.Snapshot();
        var source = new Mock<IMicrophone>(); source.Setup(s => s.Read()).Returns(() => state);
        source.Setup(s => s.SetVolume("mic", 62)).Returns(true);
        source.Setup(s => s.SetMuted("mic", false)).Returns(true);
        using var controller = new MicrophoneController(source.Object);
        await controller.RefreshAsync();
        OnSta(() =>
        {
            using var popup = new MicrophonePopup(controller, false, Color.RoyalBlue, 1);
            var view = popup.ContentView;
            view.Measure(new Wpf.Size(320, 194)); view.Arrange(new Wpf.Rect(0, 0, 320, 194)); view.UpdateLayout();
            Assert.Single(Descendants<WpfControls.Slider>(view)).Value = 62;
            var mute = Assert.Single(Descendants<WpfControls.Button>(view), button => Automation.GetName(button) == "取消麦克风静音");
            mute.RaiseEvent(new Wpf.RoutedEventArgs(WpfControls.Button.ClickEvent));
        });
        await controller.PendingWrites.WaitAsync(TimeSpan.FromSeconds(5));
        source.Verify(s => s.SetVolume("mic", 62), Times.Once);
        source.Verify(s => s.SetMuted("mic", false), Times.Once);
    }

    private static IEnumerable<T> Descendants<T>(Wpf.DependencyObject root) where T : Wpf.DependencyObject
    {
        for (var index = 0; index < Media.VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = Media.VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception exception) { failure = exception; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
