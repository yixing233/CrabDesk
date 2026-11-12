using System.Diagnostics;
using System.Drawing;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using CrabDesk.Core;
using CrabDesk.Native;
using CrabDesk.Runtime;
using Moq;
using Xunit;
using Forms = System.Windows.Forms;
using Wpf = System.Windows;

namespace CrabDesk.WinUI.Tests;

[Collection("desktop-probe")]
public sealed class PopupEntranceTests
{
    public static IEnumerable<object[]> Cases =>
        from kind in new[] { "wifi", "bluetooth", "adapters", "display", "editor", "mixer", "microphone" }
        from animate in new[] { false, true }
        from acrylic in new[] { false, true }
        select new object[] { kind, animate, acrylic };

    [Theory]
    [MemberData(nameof(Cases))]
    public void EveryDetailCardAnimatesItsNativeSurfaceAndReturnsToItsAnchor(string kind, bool animate, bool acrylic) => OnSta(() =>
    {
        using var popup = (DesktopWidgetInputForm)Create(kind, animate);
        popup.ApplyPopupMaterial(new BoxAppearance { BackgroundOpacity = .4 },
            new GlobalAppearanceSettings { UseAcrylicBoxes = acrylic });
        popup.Location = new Point(-12000, -12000);
        var target = popup.Bounds;
        popup.Show();
        var samples = new List<(double Opacity, Point Position, byte Alpha)>();
        Pump(TimeSpan.FromMilliseconds(650), () =>
        {
            Assert.False(popup.IsDisposed);
            Assert.Equal(target.Size, popup.Size);
            var layered = GetLayeredWindowAttributes(popup.Handle, out _, out var alpha, out _);
            samples.Add((popup.Opacity, popup.Location, layered ? alpha : (byte)255));
        });
        Assert.Equal(target, popup.Bounds);
        Assert.Equal(1, popup.Opacity);
        var surface = (Wpf.Media.Visual)typeof(DesktopWidgetInputForm)
            .GetField("_entranceSurface", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(popup)!;
        var contentSource = Assert.IsType<Wpf.Interop.HwndSource>(Wpf.PresentationSource.FromVisual(surface));
        var shell = Assert.IsType<System.Windows.Controls.Border>(surface);
        Assert.Equal(new Wpf.Thickness(0), shell.BorderThickness);
        Assert.Null(shell.BorderBrush);
        Assert.True(GetWindowRect(contentSource.Handle, out var contentBounds));
        Assert.Equal(new Rectangle(popup.PointToScreen(Point.Empty), popup.ClientSize),
            Rectangle.FromLTRB(contentBounds.Left, contentBounds.Top, contentBounds.Right, contentBounds.Bottom));
        Assert.Equal(0, DwmGetWindowAttribute(popup.Handle, 14, out int cloaked, sizeof(int)));
        Assert.Equal(0, cloaked);
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
        {
            Assert.Equal(0, DwmGetWindowAttribute(popup.Handle, 38, out int backdrop, sizeof(int)));
            Assert.Equal(acrylic ? 3 : 1, backdrop);
        }
        if (animate && Wpf.SystemParameters.ClientAreaAnimation)
        {
            Assert.Contains(samples, sample => sample.Opacity is > 0 and < 1 && sample.Alpha is > 0 and < 255);
            Assert.Contains(samples, sample => sample.Position != target.Location);
            var opacities = samples.Select(sample => sample.Opacity).ToArray();
            Assert.True(opacities.Zip(opacities.Skip(1)).All(pair => pair.First <= pair.Second));
        }
        else
            Assert.All(samples, sample => Assert.Equal((1d, target.Location, (byte)255), sample));

        Pump(TimeSpan.FromMilliseconds(80), () =>
        {
            Assert.Equal(target, popup.Bounds);
            Assert.Equal(1, popup.Opacity);
        });
    });

    [Theory]
    [InlineData("display")]
    [InlineData("mixer")]
    [InlineData("microphone")]
    public void ClosingDuringEntranceCancelsPendingFrames(string kind) => OnSta(() =>
    {
        using var popup = Create(kind, true);
        popup.Location = new Point(-12000, -12000);
        popup.Show();
        Pump(TimeSpan.FromMilliseconds(80), () => { });
        popup.Close();
        Pump(TimeSpan.FromMilliseconds(350), () => Assert.True(popup.IsDisposed));
    });

    private static Forms.Form Create(string kind, bool animate)
    {
        if (kind is "wifi" or "bluetooth")
        {
            var source = new Mock<IControlConnectionService>();
            source.SetupGet(service => service.Kind).Returns(kind == "wifi" ? ControlConnectionKind.WiFi : ControlConnectionKind.Bluetooth);
            source.Setup(service => service.ReadAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ControlConnectionSnapshot(true, []));
            return new ConnectionDetailsPopup(new ConnectionDetailsController(source.Object), true, Color.CornflowerBlue, 1,
                _ => { }, animate);
        }
        if (kind == "adapters")
        {
            var source = new Mock<INetworkAdapterControl>();
            source.Setup(control => control.Read()).Returns(Array.Empty<ControlNetworkAdapter>());
            return new NetworkAdaptersPopup(source.Object, true, Color.CornflowerBlue, 1, _ => { }, animate);
        }
        if (kind == "display")
        {
            var source = new Mock<IDisplayControl>();
            source.Setup(control => control.Read()).Returns(new ControlDisplaySnapshot(ControlDisplayMode.Extend, 2));
            return new DisplayControlsPopup(source.Object, true, Color.CornflowerBlue, 1, animationsEnabled: animate);
        }
        if (kind == "editor")
            return new QuickActionsEditorPopup([ControlCenterQuickAction.WiFi], _ => { }, true, Color.CornflowerBlue, 1,
                animationsEnabled: animate);
        if (kind == "mixer")
        {
            var source = new Mock<IAudioMixer>();
            source.Setup(control => control.Read()).Returns(AudioMixerSnapshot.Empty);
            return new AudioMixerPopup(new AudioMixerController(source.Object), true, Color.CornflowerBlue, 1,
                animationsEnabled: animate);
        }
        var microphone = new Mock<IMicrophone>();
        microphone.Setup(control => control.Read()).Returns(MicrophoneSnapshot.Empty);
        return new MicrophonePopup(new MicrophoneController(microphone.Object), true, Color.CornflowerBlue, 1,
            animationsEnabled: animate);
    }

    private static void Pump(TimeSpan duration, Action sample)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < duration)
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
        var thread = new Thread(() => { try { action(); } catch (Exception exception) { failure = exception; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLayeredWindowAttributes(IntPtr hwnd, out uint color, out byte alpha, out uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rectangle);
}
