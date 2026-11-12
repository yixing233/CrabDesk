using System.Diagnostics;
using System.Drawing;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using CrabDesk.Core;
using CrabDesk.Runtime;
using Xunit;
using Forms = System.Windows.Forms;
using Wpf = System.Windows;
using WpfControls = System.Windows.Controls;
using Media = System.Windows.Media;

namespace CrabDesk.WinUI.Tests;

[Collection("desktop-probe")]
public sealed class PopupCardMaterialTests
{
    public static IEnumerable<object[]> Cases =>
        from lyrics in new[] { false, true }
        from dark in new[] { false, true }
        from animate in new[] { false, true }
        from acrylic in new[] { false, true }
        select new object[] { lyrics, dark, animate, acrylic };

    [Theory]
    [MemberData(nameof(Cases))]
    public void HostedCardsKeepTheirMaterialAndNativeContentAlignedAcrossReopens(bool lyrics, bool dark, bool animate, bool acrylic) => OnSta(() =>
    {
        using Forms.Integration.ElementHost card = lyrics
            ? new LyricsSliderCard("行间距", "恢复默认", 100, 300, 5, 25, 175, 175, "紧凑", "宽松", value => value.ToString(), dark, Color.CornflowerBlue, 1)
            : new TodoDeleteConfirmationCard("需要确认的待办", dark, 1);
        var content = card.Child;
        var appearance = new BoxAppearance { Background = dark ? "#FF24272D" : "#FFF8F9FB", BackgroundOpacity = .4 };
        var settings = new GlobalAppearanceSettings { UseAcrylicBoxes = acrylic, AnimationEnabled = animate, CornerRadius = 8 };
        using var menu = new FluentContextMenuStrip { AnimationsEnabled = animate, OpacityAnimationAllowed = false,
            Padding = Forms.Padding.Empty, ShowImageMargin = false, ShowCheckMargin = false };
        menu.Items.Add(new Forms.ToolStripControlHost(card) { AutoSize = false, Size = card.Size,
            Margin = Forms.Padding.Empty, Padding = Forms.Padding.Empty });
        using var material = new PopupCardMaterial(menu, card, appearance, settings, animate);
        for (var cycle = 0; cycle < 2; cycle++)
        {
            menu.Show(new Point(-12000, -12000));
            menu.Location = new Point(-12000, -12000);
            var target = menu.Location;
            var samples = new List<double>();
            Pump(550, () =>
            {
                samples.Add(menu.Opacity);
                var source = Assert.IsType<Wpf.Interop.HwndSource>(Wpf.PresentationSource.FromVisual(content));
                Assert.True(GetWindowRect(source.Handle, out var child));
                var origin = menu.PointToScreen(Point.Empty);
                Assert.Equal(new Rectangle(origin, menu.ClientSize), Rectangle.FromLTRB(child.Left, child.Top, child.Right, child.Bottom));
            });
            Assert.Equal(target, menu.Location);
            Assert.Equal(1, menu.Opacity);
            var surface = Assert.IsType<WpfControls.Border>(Media.VisualTreeHelper.GetParent(content));
            Assert.Equal(new Wpf.Thickness(0), surface.BorderThickness);
            Assert.Null(surface.BorderBrush);
            Assert.Equal((byte)102, Assert.IsType<Media.SolidColorBrush>(surface.Background).Color.A);
            Assert.Equal(0, DwmGetWindowAttribute(menu.Handle, 14, out var cloak, 4));
            Assert.Equal(0, cloak);
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
            {
                Assert.Equal(0, DwmGetWindowAttribute(menu.Handle, 38, out var backdrop, 4));
                Assert.Equal(acrylic ? 3 : 1, backdrop);
            }
            if (animate && Wpf.SystemParameters.ClientAreaAnimation)
            {
                Assert.Contains(samples, value => value is > .01 and < 1);
                Assert.True(samples.Zip(samples.Skip(1)).All(pair => pair.First <= pair.Second));
            }
            menu.Close();
            Assert.Same(content, card.Child);
        }
    });

    private static void Pump(int milliseconds, Action sample)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < milliseconds)
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
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rectangle);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}
