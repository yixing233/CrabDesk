using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using CrabDesk.Core;
using CrabDesk.Native;
using CrabDesk.Runtime;
using Wpf = System.Windows;

internal static class PopupMaterialProbe
{
    internal static int RunWidgetCards()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        using var backdrop = new Form { Bounds = new(100, 100, 700, 450), StartPosition = FormStartPosition.Manual,
            TopMost = true, FormBorderStyle = FormBorderStyle.None };
        backdrop.Paint += (_, args) =>
        {
            for (var x = 0; x < backdrop.Width; x += 12)
                args.Graphics.FillRectangle(x % 24 == 0 ? Brushes.Red : Brushes.Cyan, x, 0, 12, backdrop.Height);
        };
        backdrop.Show();
        var output = Path.Combine(Path.GetTempPath(), "CrabDesk-WidgetPopupCards");
        Directory.CreateDirectory(output);
        var passed = true;
        foreach (var lyrics in new[] { false, true })
        {
            using System.Windows.Forms.Integration.ElementHost card = lyrics
                ? new LyricsSliderCard("Line spacing", "Reset", 100, 300, 5, 25, 175, 175, "Compact", "Relaxed",
                    value => (value / 100).ToString("0.00"), true, Color.CornflowerBlue, backdrop.DeviceDpi / 96d)
                : new TodoDeleteConfirmationCard("A task awaiting confirmation", true, backdrop.DeviceDpi / 96d);
            if (card.Child is Wpf.Controls.Border content) content.Background = Wpf.Media.Brushes.Transparent;
            using var menu = new FluentContextMenuStrip { OpacityAnimationAllowed = false, Padding = Padding.Empty,
                ShowImageMargin = false, ShowCheckMargin = false };
            menu.Items.Add(new ToolStripControlHost(card) { AutoSize = false, Size = card.Size, Margin = Padding.Empty, Padding = Padding.Empty });
            var name = lyrics ? "lyrics" : "todo";
            var contrasts = new List<double>();
            foreach (var acrylic in new[] { true, false })
            {
                using var material = new PopupCardMaterial(menu, card,
                    new BoxAppearance { Background = "#FF24272D", BackgroundOpacity = .4 },
                    new GlobalAppearanceSettings { UseAcrylicBoxes = acrylic, CornerRadius = 8 }, true);
                menu.Show(backdrop, new Point(60, 60));
                SetWindowPos(menu.Handle, new IntPtr(-1), menu.Left, menu.Top, menu.Width, menu.Height, 0x0040);
                var samples = new List<double>();
                Pump(650, () => samples.Add(menu.Opacity));
                using var image = new Bitmap(menu.Width, menu.Height);
                using (var graphics = Graphics.FromImage(image)) graphics.CopyFromScreen(menu.Left, menu.Top, 0, 0, image.Size);
                image.Save(Path.Combine(output, $"{name}-{acrylic}.png"));
                var reds = Enumerable.Range(20, image.Width - 40).Select(x => image.GetPixel(x, 5).R).ToArray();
                contrasts.Add(reds.Max() - reds.Min());
                passed &= samples.Any(value => value is > .01 and < 1) && menu.Opacity == 1;
                menu.Close();
            }
            passed &= contrasts[0] < contrasts[1] * .5 && contrasts[1] > 40;
            Console.WriteLine($"{name}: acrylicContrast={contrasts[0]:0}, clearContrast={contrasts[1]:0}");
        }
        Console.WriteLine($"Passed={passed}, output={output}");
        return passed ? 0 : 1;
    }

    internal static int RunLoading(ControlConnectionKind kind, bool dark, bool animate)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        using var backdrop = new Form
        {
            Bounds = new(100, 100, 700, 650), StartPosition = FormStartPosition.Manual,
            TopMost = true, FormBorderStyle = FormBorderStyle.None, BackColor = Color.FromArgb(35, 69, 85)
        };
        backdrop.Show();
        ShowWindow(backdrop.Handle, 4);
        SetWindowPos(backdrop.Handle, new IntPtr(-1), backdrop.Left, backdrop.Top, backdrop.Width, backdrop.Height, 0x0040);
        Pump(100, () => { });
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connections = new ReadOnlyConnections(kind, gate.Task);
        using var controller = new ConnectionDetailsController(connections);
        using var popup = new ConnectionDetailsPopup(controller, dark, Color.CornflowerBlue, backdrop.DeviceDpi / 96.0, _ => { }, animate);
        popup.ApplyPopupMaterial(new BoxAppearance { Background = dark ? "#FF24272D" : "#FFF8F9FB", BackgroundOpacity = .4 },
            new GlobalAppearanceSettings { UseAcrylicBoxes = true, CornerRadius = 8 });
        var output = Path.Combine(Path.GetTempPath(), "CrabDesk-PopupMaterialProbe", "loading", $"{kind}-{dark}-{animate}");
        Directory.CreateDirectory(output);
        popup.Location = new(160, 160);
        popup.TopMost = true;
        popup.Show(backdrop);
        ShowWindow(popup.Handle, 4);
        popup.BringToFront();
        popup.Activate();
        var loading = Find<PopupLoadingPresenter>(popup.ContentView) ?? throw new InvalidOperationException("Loading presenter was not found.");
        var aligned = true;
        var samples = new List<double>();
        void Sample()
        {
            if (popup.IsDisposed || !popup.Visible) throw new InvalidOperationException("Loading popup closed during capture.");
            var source = (Wpf.Interop.HwndSource)Wpf.PresentationSource.FromVisual(popup.ContentView);
            GetWindowRect(source.Handle, out var child);
            var origin = popup.PointToScreen(Point.Empty);
            aligned &= child.Left == origin.X && child.Top == origin.Y
                && child.Right - child.Left == popup.ClientSize.Width && child.Bottom - child.Top == popup.ClientSize.Height;
        }
        Pump(450, Sample);
        var showedSkeleton = loading.IsLoading && loading.SkeletonVisible;
        var bounds = popup.Bounds;
        using (var skeleton = Capture(popup)) skeleton.Save(Path.Combine(output, "skeleton.png"));
        var revealTimer = Stopwatch.StartNew();
        gate.SetResult(true);
        var capturedTransition = false;
        Pump(450, () =>
        {
            Sample();
            aligned &= bounds == popup.Bounds;
            samples.Add(loading.ContentOpacity);
            if (!capturedTransition && revealTimer.ElapsedMilliseconds >= 110 && loading.IsTransitioning
                && loading.ContentOpacity is > 0 and < 1)
            {
                using var entering = Capture(popup);
                entering.Save(Path.Combine(output, "entering.png"));
                capturedTransition = true;
            }
        });
        using (var ready = Capture(popup)) ready.Save(Path.Combine(output, "ready.png"));
        var radio = Find<Wpf.Controls.Primitives.ToggleButton>(popup.ContentView)
            ?? throw new InvalidOperationException("Radio switch was not found.");
        var thumb = (Wpf.Media.TranslateTransform)radio.Template.FindName("ThumbOffset", radio);
        var showedEnabled = radio.IsChecked is true && Math.Abs(thumb.X - 20) < .01;
        connections.Enabled = false;
        controller.RefreshAsync().GetAwaiter().GetResult();
        var switchFrames = new List<double>();
        var switchTimer = Stopwatch.StartNew();
        var capturedSwitch = false;
        Pump(240, () =>
        {
            Sample();
            switchFrames.Add(thumb.X);
            if (!capturedSwitch && switchTimer.ElapsedMilliseconds >= 60 && thumb.X is > 0 and < 20)
            {
                using var moving = Capture(popup);
                moving.Save(Path.Combine(output, "switch-moving.png"));
                capturedSwitch = true;
            }
        });
        using (var disabled = Capture(popup)) disabled.Save(Path.Combine(output, "switch-off.png"));
        var showedDisabled = radio.IsChecked is false && Math.Abs(thumb.X) < .01;
        var switchAnimated = !animate || !Wpf.SystemParameters.ClientAreaAnimation
            || switchFrames.Any(value => value is > 0 and < 20);
        var result = new
        {
            Kind = kind.ToString(), Dark = dark, Animate = animate, Dpi = popup.DeviceDpi,
            ShowedSkeleton = showedSkeleton,
            RevealedContent = !loading.IsLoading && !loading.IsTransitioning && !loading.SkeletonVisible && loading.ContentOpacity == 1,
            IntermediateFrames = samples.Count(value => value is > 0 and < 1),
            SwitchOn = showedEnabled, SwitchOff = showedDisabled,
            SwitchIntermediateFrames = switchFrames.Count(value => value is > 0 and < 20),
            ContentAligned = aligned,
            Passed = showedSkeleton && !loading.IsLoading && !loading.IsTransitioning && !loading.SkeletonVisible
                && loading.ContentOpacity == 1 && aligned
                && showedEnabled && showedDisabled && switchAnimated
                && (!animate || !Wpf.SystemParameters.ClientAreaAnimation || capturedTransition)
        };
        File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(result));
        Console.WriteLine(JsonSerializer.Serialize(result));
        Console.WriteLine(output);
        return result.Passed ? 0 : 1;
    }

    private static T? Find<T>(Wpf.DependencyObject root) where T : Wpf.DependencyObject
    {
        if (root is T found) return found;
        for (var index = 0; index < Wpf.Media.VisualTreeHelper.GetChildrenCount(root); index++)
            if (Find<T>(Wpf.Media.VisualTreeHelper.GetChild(root, index)) is { } result) return result;
        return null;
    }

    internal static int Run(ControlConnectionKind? connectionKind = null, bool animate = true)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        using var backdrop = new Form
        {
            Bounds = new(100, 100, 700, 650), StartPosition = FormStartPosition.Manual,
            TopMost = true, FormBorderStyle = FormBorderStyle.None
        };
        var painted = 0;
        backdrop.Paint += (_, args) =>
        {
            painted++;
            for (var x = 0; x < backdrop.Width; x += 12)
                args.Graphics.FillRectangle(x % 24 == 0 ? Brushes.Red : Brushes.Cyan, x, 0, 12, backdrop.Height);
        };
        backdrop.Show();
        ShowWindow(backdrop.Handle, 4);
        backdrop.Activate();
        SetWindowPos(backdrop.Handle, new IntPtr(-1), backdrop.Left, backdrop.Top, backdrop.Width, backdrop.Height, 0x0040);
        Pump(150, () => { });
        DwmGetWindowAttribute(backdrop.Handle, 14, out var cloaked, sizeof(int));
        Console.WriteLine($"Pattern: painted={painted}, visible={IsWindowVisible(backdrop.Handle)}, cloak={cloaked}, bounds={backdrop.Bounds}, HWND={backdrop.Handle:X}, pointHWND={WindowFromPoint(new(180, 180)):X}");
        var output = Path.Combine(Path.GetTempPath(), "CrabDesk-PopupMaterialProbe",
            connectionKind is { } kind ? $"{kind}-{animate}" : "mixer");
        Directory.CreateDirectory(output);
        using (var pattern = Capture(backdrop)) pattern.Save(Path.Combine(output, "pattern.png"));
        var appearance = new BoxAppearance { Background = "#FF24272D", BackgroundOpacity = .4 };
        var settings = new GlobalAppearanceSettings { UseAcrylicBoxes = true, CornerRadius = 8 };
        using DesktopWidgetInputForm popup = connectionKind is { } connection
            ? new ConnectionDetailsPopup(new ConnectionDetailsController(new ReadOnlyConnections(connection)),
                true, Color.CornflowerBlue, backdrop.DeviceDpi / 96.0, _ => { }, animate)
            : new AudioMixerPopup(new AudioMixerController(new ReadOnlyMixer()), true, Color.CornflowerBlue, 1);
        var content = popup is ConnectionDetailsPopup details ? details.ContentView : ((AudioMixerPopup)popup).ContentView;
        popup.ApplyPopupMaterial(appearance, settings);
        popup.Location = new(160, 160);
        popup.TopMost = true;
        popup.Show(backdrop);
        ShowWindow(popup.Handle, 4);
        popup.BringToFront();
        popup.Activate();
        var samples = new List<double>();
        var geometry = new List<object>();
        var aligned = true;
        Pump(700, () =>
        {
            if (popup.IsDisposed || !popup.Visible) throw new InvalidOperationException("Material popup closed during capture.");
            samples.Add(popup.Opacity);
            var contentSource = (Wpf.Interop.HwndSource)Wpf.PresentationSource.FromVisual(content);
            GetWindowRect(contentSource.Handle, out var child);
            var origin = popup.PointToScreen(Point.Empty);
            aligned &= child.Left == origin.X && child.Top == origin.Y
                && child.Right - child.Left == popup.ClientSize.Width
                && child.Bottom - child.Top == popup.ClientSize.Height;
            geometry.Add(new { popup.Opacity, ParentX = origin.X, ParentY = origin.Y, popup.ClientSize,
                ChildX = child.Left, ChildY = child.Top, ChildWidth = child.Right - child.Left, ChildHeight = child.Bottom - child.Top });
        });
        File.WriteAllText(Path.Combine(output, "geometry.json"), JsonSerializer.Serialize(geometry));
        if (connectionKind is not null)
        {
            using var card = Capture(popup);
            card.Save(Path.Combine(output, "settled.png"));
            var geometryResult = new { Kind = connectionKind.ToString(), Animate = animate, Dpi = popup.DeviceDpi,
                ContentAlignedDuringEntrance = aligned, Passed = aligned };
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(geometryResult));
            Console.WriteLine(JsonSerializer.Serialize(geometryResult));
            Console.WriteLine(output);
            return aligned ? 0 : 1;
        }
        using var acrylic = Capture(popup);
        acrylic.Save(Path.Combine(output, "acrylic.png"));
        settings.UseAcrylicBoxes = false;
        popup.ApplyPopupMaterial(appearance, settings);
        Pump(400, () => { });
        using var transparent = Capture(popup);
        transparent.Save(Path.Combine(output, "transparent.png"));
        appearance.BackgroundOpacity = 1;
        popup.ApplyPopupMaterial(appearance, settings);
        Pump(400, () => { });
        using var opaque = Capture(popup);
        opaque.Save(Path.Combine(output, "opaque.png"));
        var blurContrast = Contrast(acrylic);
        var clearContrast = Contrast(transparent);
        var opaqueContrast = Contrast(opaque);
        var solid = opaque.GetPixel(20, opaque.Height - 50);
        var animated = !Wpf.SystemParameters.ClientAreaAnimation || samples.Any(alpha => alpha is > .05 and < .95);
        var source = (Wpf.Interop.HwndSource)Wpf.PresentationSource.FromVisual(content);
        var focused = Wpf.Input.Keyboard.FocusedElement;
        PostMessage(source.Handle, 0x0100, new IntPtr(0x09), new IntPtr(1));
        PostMessage(source.Handle, 0x0101, new IntPtr(0x09), new IntPtr(unchecked((int)0xC0000001)));
        Pump(100, () => { });
        var tab = Wpf.Input.Keyboard.FocusedElement != focused;
        PostMessage(source.Handle, 0x0100, new IntPtr(0x1B), new IntPtr(1));
        Pump(100, () => { });
        var escape = popup.IsDisposed;
        var result = new
        {
            Animated = animated, BlurContrast = blurContrast, ClearContrast = clearContrast,
            OpaqueContrast = opaqueContrast, OpaqueColor = solid.ToString(),
            KeyboardTab = tab, KeyboardEscape = escape,
            ContentAlignedDuringEntrance = aligned,
            Passed = animated && blurContrast < clearContrast * .35 && clearContrast > 50
                && opaqueContrast < 2 && Math.Abs(solid.R - 36) < 3 && tab && escape && aligned
        };
        File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(result));
        Console.WriteLine(JsonSerializer.Serialize(result));
        Console.WriteLine(output);
        return result.Passed ? 0 : 1;
    }

    private static double Contrast(Bitmap image)
    {
        var row = image.Height - 50;
        var reds = Enumerable.Range(20, image.Width - 40).Select(x => image.GetPixel(x, row).R).ToArray();
        return reds.Max() - reds.Min();
    }

    private static Bitmap Capture(Form popup)
    {
        var image = new Bitmap(popup.Width, popup.Height);
        using var graphics = Graphics.FromImage(image);
        graphics.CopyFromScreen(popup.Left, popup.Top, 0, 0, image.Size);
        return image;
    }

    private static void Pump(int milliseconds, Action sample)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < milliseconds)
        {
            Application.DoEvents();
            Wpf.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, Wpf.Threading.DispatcherPriority.Background);
            sample();
            Thread.Sleep(8);
        }
    }

    private sealed class ReadOnlyMixer : IAudioMixer
    {
        public AudioMixerSnapshot Read() => new("probe", "Headphones", 50, false, []);
        public bool SetVolume(string deviceId, string? applicationId, double percent) => throw new NotSupportedException();
        public bool SetMuted(string deviceId, string? applicationId, bool muted) => throw new NotSupportedException();
    }

    private sealed class ReadOnlyConnections(ControlConnectionKind kind, Task? ready = null) : IControlConnectionService
    {
        public ControlConnectionKind Kind => kind;
        internal bool Enabled { get; set; } = true;
        public async Task<ControlConnectionSnapshot> ReadAsync(bool scan, CancellationToken token = default)
        {
            if (ready is not null) await ready.WaitAsync(token);
            return new ControlConnectionSnapshot(Enabled, Enumerable.Range(1, 6).Select(index =>
                new ControlConnectionItem(index.ToString(), kind == ControlConnectionKind.WiFi ? $"Network {index}" : $"Device {index}",
                    kind == ControlConnectionKind.WiFi ? "WPA2 - 80%" : "Paired - Disconnected", false, true, false)).ToArray());
        }
        public Task<ControlOperationResult> SetEnabledAsync(bool enabled, CancellationToken token = default) => throw new NotSupportedException();
        public Task<ControlOperationResult> ConnectAsync(string id, string? password, CancellationToken token = default) => throw new NotSupportedException();
        public Task<ControlOperationResult> DisconnectAsync(string id, CancellationToken token = default) => throw new NotSupportedException();
        public void Dispose() { }
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rectangle);
}
