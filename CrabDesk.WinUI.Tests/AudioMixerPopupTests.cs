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

public sealed class AudioMixerPopupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CardRendersScrollableApplicationRows(bool dark)
    {
        var snapshot = new AudioMixerSnapshot("headphones", "耳机 · 主音量", 35, false,
            Enumerable.Range(0, 8).Select(index => new AudioMixerApplication("app" + index,
                new[] { "音乐播放器", "浏览器", "聊天应用", "系统声音" }[index % 4], "", 100 - index * 10, index == 2)).ToArray());
        var source = new Mock<IAudioMixer>();
        source.Setup(s => s.Read()).Returns(snapshot);
        using var controller = new AudioMixerController(source.Object);
        await controller.RefreshAsync();
        OnSta(() =>
        {
            using var popup = new AudioMixerPopup(controller, dark, Color.FromArgb(79, 100, 154), 1);
            var view = popup.ContentView;
            var size = popup.ClientSize;
            view.Measure(new Wpf.Size(size.Width, size.Height));
            view.Arrange(new Wpf.Rect(0, 0, size.Width, size.Height));
            view.UpdateLayout();
            var sliders = Descendants<WpfControls.Slider>(view).ToArray();
            Assert.Equal(9, sliders.Length);
            Assert.Equal(35, sliders[0].Value);
            Assert.Equal(100, sliders[1].Value);
            Assert.All(sliders, slider => Assert.True(slider.ActualWidth > 180));
            Assert.True(Assert.Single(Descendants<WpfControls.ScrollViewer>(view)).ScrollableHeight > 0);
            Assert.Contains(Descendants<WpfControls.Button>(view), button => Automation.GetName(button).Contains("取消静音"));

            var bitmap = new Imaging.RenderTargetBitmap(size.Width, size.Height, 96, 96, Media.PixelFormats.Pbgra32);
            bitmap.Render(view);
            var encoder = new Imaging.PngBitmapEncoder();
            encoder.Frames.Add(Imaging.BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(AppContext.BaseDirectory, dark ? "audio-mixer-dark.png" : "audio-mixer-light.png"));
            encoder.Save(file);
            source.Verify(s => s.SetVolume(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<double>()), Times.Never);
        });
    }

    [Fact]
    public async Task ApplicationSliderDispatchesItsApplicationId()
    {
        var source = new Mock<IAudioMixer>();
        source.Setup(s => s.Read()).Returns(AudioMixerControllerTests.Snapshot());
        source.Setup(s => s.SetVolume(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<double>())).Returns(true);
        source.Setup(s => s.SetMuted(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>())).Returns(true);
        using var controller = new AudioMixerController(source.Object);
        await controller.RefreshAsync();
        OnSta(() =>
        {
            using var popup = new AudioMixerPopup(controller, false, Color.RoyalBlue, 1);
            var view = popup.ContentView;
            var size = popup.ClientSize;
            view.Measure(new Wpf.Size(size.Width, size.Height));
            view.Arrange(new Wpf.Rect(0, 0, size.Width, size.Height));
            view.UpdateLayout();
            var slider = Assert.Single(Descendants<WpfControls.Slider>(view), item => Automation.GetName(item) == "Player音量");
            slider.Value = 37;
        });
        await controller.PendingWrites.WaitAsync(TimeSpan.FromSeconds(5));
        source.Verify(s => s.SetVolume("headphones", "player", 37), Times.Once);
        source.Verify(s => s.SetVolume(It.IsAny<string>(), null, It.IsAny<double>()), Times.Never);
    }

    [Theory]
    [InlineData(1870, 1030, 0, 0, 1920, 1080)]
    [InlineData(-1900, 50, -1920, 0, 1920, 1080)]
    [InlineData(10, 10, 0, 0, 800, 600)]
    public void PopupStaysInsideItsMonitor(int x, int y, int left, int top, int width, int height)
    {
        var workArea = new Rectangle(left, top, width, height);
        var size = new Size(350, 390);
        var location = AudioMixerPopup.PopupLocation(new(x, y, 22, 20), size, workArea);
        Assert.True(workArea.Contains(new Rectangle(location, size)));
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
