using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using CrabDesk.Core;
using CrabDesk.Native;
using CrabDesk.Runtime;

internal static class PopupEntranceProbe
{
    internal static int Run()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        var area = Screen.PrimaryScreen!.WorkingArea;
        using var backdrop = new Form
        {
            BackColor = Color.White, FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual, ShowInTaskbar = false, TopMost = true,
            Bounds = new Rectangle(area.Left + 100, area.Top + 100, 500, 600)
        };
        backdrop.Show();
        ShowWindow(backdrop.Handle, 4);
        Pump(150, () => { });
        using var popup = new AudioMixerPopup(new AudioMixerController(new ReadOnlyMixer()), true, Color.CornflowerBlue, 1);
        popup.Location = new Point(backdrop.Left + 40, backdrop.Top + 40);
        using var pixel = new Bitmap(1, 1);
        using var graphics = Graphics.FromImage(pixel);
        var samples = new List<(double Opacity, int Red)>();
        var output = Path.Combine(Path.GetTempPath(), "CrabDesk-PopupEntranceProbe");
        Directory.CreateDirectory(output);
        popup.Show(backdrop);
        ShowWindow(popup.Handle, 4);
        var saved = false;
        Pump(700, () =>
        {
            if (popup.IsDisposed) throw new InvalidOperationException("Popup closed during the visual probe.");
            var frame = Frame(popup.Handle);
            graphics.CopyFromScreen(frame.Left + 8, frame.Top + 60, 0, 0, new Size(1, 1));
            samples.Add((popup.Opacity, pixel.GetPixel(0, 0).R));
            if (!saved && popup.Opacity is > .25 and < .75)
            {
                Capture(popup.Handle, Path.Combine(output, "entering.png"));
                saved = true;
            }
        });
        Capture(popup.Handle, Path.Combine(output, "settled.png"));
        File.WriteAllText(Path.Combine(output, "frames.json"), JsonSerializer.Serialize(
            samples.Select(sample => new { sample.Opacity, sample.Red })));
        var blended = samples.Any(sample => sample.Opacity is > .1 and < .6 && sample.Red is > 100 and < 240);
        var settled = samples[^1].Red is >= 30 and <= 45;
        Console.WriteLine($"Popup native pixels: blended={blended}, settled={settled}, finalRed={samples[^1].Red}, evidence={output}");
        return settled && (!System.Windows.SystemParameters.ClientAreaAnimation || blended) ? 0 : 1;
    }

    private static void Capture(IntPtr hwnd, string path)
    {
        var frame = Frame(hwnd);
        using var image = new Bitmap(frame.Right - frame.Left, frame.Bottom - frame.Top);
        using var graphics = Graphics.FromImage(image);
        graphics.CopyFromScreen(frame.Left, frame.Top, 0, 0, image.Size);
        image.Save(path);
    }

    private static NativeRect Frame(IntPtr hwnd)
    {
        Marshal.ThrowExceptionForHR(DwmGetWindowAttribute(hwnd, 9, out var frame, Marshal.SizeOf<NativeRect>()));
        return frame;
    }

    private static void Pump(int milliseconds, Action sample)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < milliseconds)
        {
            Application.DoEvents();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { },
                System.Windows.Threading.DispatcherPriority.Background);
            sample();
            Thread.Sleep(8);
        }
    }

    private sealed class ReadOnlyMixer : IAudioMixer
    {
        public AudioMixerSnapshot Read() => AudioMixerSnapshot.Empty;
        public bool SetVolume(string deviceId, string? applicationId, double percent) => throw new NotSupportedException();
        public bool SetMuted(string deviceId, string? applicationId, bool muted) => throw new NotSupportedException();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out NativeRect value, int size);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);
}
