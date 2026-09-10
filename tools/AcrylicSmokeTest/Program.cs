using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CrabDesk.Core;
using CrabDesk.Native;
using CrabDesk.Runtime;

// Runs against in-memory boxes and an isolated data directory. --wallpaper
// briefly exposes the desktop, but never hides Explorer icons or starts a
// second desktop takeover. The default source is a moving stripe window.
internal static class Program
{
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Hidden)!.SetValue(target, value);
    private static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, Hidden)!.GetValue(target)!;
    private static void Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, Hidden)!.Invoke(target, args);

    [STAThread]
    private static int Main(string[] args)
    {
        var wallpaper = args.Contains("--wallpaper");
        var fullMonitor = args.Contains("--full-monitor");
        var desktopCycle = args.Contains("--desktop-cycle");
        var interaction = args.Contains("--interaction");
        var drag = args.Contains("--drag");
        var previousCursor = Cursor.Position;
        var output = Path.Combine(Environment.GetEnvironmentVariable("PI_SCRATCH_DIR") ?? Path.GetTempPath(),
            "CrabDesk-AcrylicSmokeTest", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("CRABDESK_DATA_DIR", output);
        Console.WriteLine($"Evidence: {output}");
        var desktopShown = false;
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            if (interaction && desktopCycle)
                throw new ArgumentException("Run pointer interaction and Show Desktop validation separately.");
            if (interaction) Cursor.Position = new Point(550, 200);
            if (!DesktopAcrylicHost.IsSupported) throw new PlatformNotSupportedException("Windows 11 required.");
            using var source = new PatternSource();
            if (!wallpaper) source.Show();
            if (wallpaper) { SetDesktopVisible(true); desktopShown = true; }
            var pixelBounds = fullMonitor ? Screen.PrimaryScreen!.Bounds : new Rectangle(570, 210, 800, 460);
            var workArea = fullMonitor ? Screen.PrimaryScreen!.WorkingArea : pixelBounds;
            var monitor = new MonitorLayout
            {
                Id = "smoke", DeviceName = "smoke", DpiScale = 1,
                Bounds = new(0, 0, pixelBounds.Width, pixelBounds.Height),
                WorkArea = new(workArea.X - pixelBounds.X, workArea.Y - pixelBounds.Y, workArea.Width, workArea.Height),
                PixelBounds = new(pixelBounds.X, pixelBounds.Y, pixelBounds.Width, pixelBounds.Height),
                PixelWorkArea = new(workArea.X, workArea.Y, workArea.Width, workArea.Height)
            };
            var runtime = (CrabDeskRuntime)RuntimeHelpers.GetUninitializedObject(typeof(CrabDeskRuntime));
            var state = new CrabDeskState();
            state.Settings.Appearance.UseAcrylicBoxes = true;
            Set(runtime, "<State>k__BackingField", state);
            Set(runtime, "<Items>k__BackingField", Array.Empty<DesktopItemRef>());
            Set(runtime, "<Monitors>k__BackingField", new[] { monitor });
            var a = new DesktopBox { Title = "Acrylic A", MonitorId = "smoke", ExpandOnHover = true, Bounds = new(610 - pixelBounds.X, 275 - pixelBounds.Y, 320, 300) };
            var b = new DesktopBox { Title = "Acrylic B", MonitorId = "smoke", ExpandOnHover = true, Bounds = new(990 - pixelBounds.X, 275 - pixelBounds.Y, 320, 300) };
            a.Appearance.Opacity = b.Appearance.Opacity = 0.5;
            state.Boxes.AddRange([a, b]);
            if (args.Contains("--manager"))
            {
                if (wallpaper) throw new ArgumentException("Use --manager with the synthetic source.");
                using var fakeListView = new Panel { Size = new Size(1, 1) };
                source.Controls.Add(fakeListView);
                var desktopHost = new DesktopHostService();
                desktopHost.Apply(new DesktopHostSnapshot(source.Handle, fakeListView.Handle, source.Handle));
                for (var cycle = 0; cycle < 3; cycle++)
                {
                    using var manager = new DesktopSurfaceManager(runtime, desktopHost, [monitor]);
                    manager.EnsureReady();
                    if (Get<DesktopAcrylicHost?>(manager, "_acrylicHost") is null)
                        throw new InvalidOperationException("Manager fell back instead of creating acrylic.");
                    var managerIcons = Get<List<DesktopIconSurface>>(manager, "_iconSurfaces");
                    var managerBoxes = Get<List<DesktopBoxForm>>(manager, "_surfaces");
                    var managerHost = Get<DesktopAcrylicHost>(manager, "_acrylicHost");
                    if (GetParent(managerIcons.Single().Handle) != source.Handle)
                        throw new InvalidOperationException("Desktop icons are not below the acrylic host.");
                    if (GetParent(managerBoxes.Single().Handle) != managerHost.Handle)
                        throw new InvalidOperationException("Box foreground is not above the acrylic host.");
                    manager.SetDesktopIconsVisible(false);
                    manager.SetVisible(false);
                    manager.SetVisible(true);
                    manager.SetDesktopIconsVisible(true);
                    manager.EnsureReady();
                    manager.Refresh();
                    Console.WriteLine($"Manager cycle {cycle}: initialization, hide/show, and refresh passed.");
                }
                return 0;
            }
            using var host = new DesktopAcrylicHost(wallpaper ? DesktopHostService.FindDesktopView() : source.Handle, [monitor]);
            host.InitializeComposition();
            using var boxes = new DesktopBoxForm(runtime, monitor);
            boxes.SetAcrylicBackground(host.EffectsEnabled);
            _ = boxes.Handle;
            if (boxes.IsLayerReady) throw new InvalidOperationException("Box pixels were submitted before desktop attachment.");
            boxes.AttachToDesktop(host.Handle);
            DesktopWindowTools.PositionAboveDesktop(boxes.Handle, host.AnchorHandle, 0, 0, pixelBounds.Width, pixelBounds.Height);
            boxes.SetAcrylicFramePresenter((bitmap, origin, regions) =>
            {
                if (drag)
                {
                    var region = regions.Single(r => r.Id == a.Id);
                    using var graphics = Graphics.FromImage(bitmap);
                    graphics.FillRectangle(Brushes.Lime, region.ScreenBounds.X - origin.X + 16,
                        region.ScreenBounds.Y - origin.Y + 12, 12, 12);
                }
                host.PresentForeground(monitor.Id, bitmap, origin, regions);
            });
            var expanded = Get<HashSet<Guid>>(boxes, "_hoverExpandedBoxes");
            expanded.Add(a.Id); expanded.Add(b.Id);
            if (!boxes.UpdateInteractionRegion()) throw new InvalidOperationException("Initial frame failed.");
            host.ShowAtDesktop();
            boxes.Show();
            DesktopWindowTools.ShowAboveDesktop(boxes.Handle, host.AnchorHandle);
            Console.WriteLine($"host effects={host.EffectsEnabled}; boxReady={DesktopWindowTools.IsDesktopSurfaceReady(boxes.Handle, host.AnchorHandle)}");
            var clock = System.Diagnostics.Stopwatch.StartNew();
            using var timer = new System.Windows.Forms.Timer { Interval = 50 };
            var stage = 0;
            Exception? validationError = null;
            var framePending = false;
            var dragFrame = 0;
            var initialBounds = a.Bounds;
            timer.Tick += async (_, _) =>
            {
                if (framePending) return;
                framePending = true;
                try
                {
                    if (drag)
                    {
                        if (dragFrame == 0)
                        {
                            Set(boxes, "_movingBox", a);
                            Set(boxes, "_startBounds", a.Bounds);
                        }
                        var step = dragFrame < 30 ? dragFrame : 59 - dragFrame;
                        a.Bounds = initialBounds with { X = initialBounds.X + step * 8, Y = initialBounds.Y + step * 3 };
                        if (!boxes.UpdateInteractionRegion()) throw new InvalidOperationException(boxes.LayerDiagnostic);
                        await host.WaitForCommitAsync();
                        DwmFlush();
                        using var shot = new Bitmap(800, 460);
                        using (var graphics = Graphics.FromImage(shot)) graphics.CopyFromScreen(570, 210, 0, 0, shot.Size);
                        var region = boxes.GetAcrylicRegions().Single(r => r.Id == a.Id);
                        var marker = shot.GetPixel((int)region.ScreenBounds.X - 570 + 22,
                            (int)region.ScreenBounds.Y - 210 + 18);
                        if (marker.G < 240 || marker.R > 10 || marker.B > 10)
                        {
                            shot.Save(Path.Combine(output, $"drag-failure-{dragFrame}.png"));
                            throw new InvalidOperationException($"Foreground pixel missed backdrop bounds at drag frame {dragFrame}: {marker}; {region.ScreenBounds}");
                        }
                        if (dragFrame % 10 == 0) shot.Save(Path.Combine(output, $"drag-{dragFrame}.png"));
                        dragFrame++;
                        if (dragFrame == 60)
                        {
                            Set(boxes, "_movingBox", null!);
                            a.Bounds = initialBounds;
                            boxes.UpdateInteractionRegion();
                            Console.WriteLine("60 consecutive/reverse drag frames passed onscreen foreground alignment checks.");
                            Application.ExitThread();
                        }
                        return;
                    }
                    if (clock.Elapsed.TotalSeconds < stage + 1) return;
                    stage++;
                    Console.WriteLine($"stage={stage}; regions={string.Join(";", boxes.GetAcrylicRegions().Select(r => r.ScreenBounds))}");
                    Console.WriteLine($"box origin={boxes.PointToScreen(Point.Empty)} size={boxes.Size} layered={DesktopWindowTools.GetSurfaceExtendedStyle(boxes.Handle):X} ready={boxes.IsLayerReady} diagnostic={boxes.LayerDiagnostic}; hit={WindowFromPoint(new Point(640,290)):X} box={boxes.Handle:X} host={host.Handle:X}");
                    if (stage is 2 or 4 or 6 or 8 or 10)
                    {
                        await host.WaitForCommitAsync();
                        DwmFlush();
                        if (!DesktopAcrylicWindowTools.IsReadyAtDesktop(host.Handle,
                                wallpaper ? DesktopHostService.FindDesktopView() : source.Handle) ||
                            !IsWindowVisible(boxes.Handle))
                            throw new InvalidOperationException($"Acrylic host/box visibility failed at stage {stage}.");
                        using var shot = new Bitmap(800, 460);
                        using (var graphics = Graphics.FromImage(shot)) graphics.CopyFromScreen(570, 210, 0, 0, shot.Size);
                        shot.Save(Path.Combine(output, $"frame-{stage}.png"));
                        Console.WriteLine($"pointer target classification={DesktopAcrylicWindowTools.IsAcrylicSurface(WindowFromPoint(new Point(640,290)))}");
                    }
                    if (stage == 3)
                    {
                        expanded.Remove(a.Id);
                        Call(boxes, "StartBoxHeightAnimation", a, 300d);
                        Set(boxes, "_geometryDirty", true);
                        boxes.RequestRender();
                    }
                    if (stage == 5)
                    {
                        expanded.Add(a.Id); expanded.Remove(b.Id);
                        Call(boxes, "StartBoxHeightAnimation", a, a.Appearance.TitleBarHeight);
                        Call(boxes, "StartBoxHeightAnimation", b, 300d);
                        Set(boxes, "_geometryDirty", true);
                        boxes.RequestRender();
                    }
                    if (interaction && stage == 7) Cursor.Position = new Point(1150, 290);
                    if (interaction && stage == 8)
                    {
                        if (WindowFromPoint(Cursor.Position) != boxes.Handle ||
                            boxes.GetAcrylicRegions().Single(r => r.Id == b.Id).ScreenBounds.Height != 300)
                            throw new InvalidOperationException("Physical pointer did not expand box B.");
                        Console.WriteLine("Physical pointer hover expansion passed.");
                    }
                    if (interaction && stage == 9) Cursor.Position = new Point(550, 200);
                    if (interaction && stage == 10)
                    {
                        if (boxes.GetAcrylicRegions().Single(r => r.Id == b.Id).ScreenBounds.Height != b.Appearance.TitleBarHeight)
                            throw new InvalidOperationException("Box B did not collapse after pointer exit.");
                        if (Get<System.Windows.Forms.Timer>(boxes, "_hoverTimer").Enabled)
                            throw new InvalidOperationException("Hover reconciliation did not stop after collapse.");
                        Console.WriteLine("Physical pointer exit collapse passed.");
                    }
                    if (desktopCycle && stage is 7 or 9) DesktopWindowTools.ToggleDesktop();
                    if (stage == (desktopCycle || interaction ? 11 : 7)) Application.ExitThread();
                }
                catch (Exception exception) { validationError = exception; Application.ExitThread(); }
                finally { framePending = false; }
            };
            timer.Start();
            Application.Run(host);
            if (validationError is not null) throw validationError;
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        finally
        {
            if (interaction) Cursor.Position = previousCursor;
            if (desktopShown) SetDesktopVisible(false);
        }
    }

    private static void SetDesktopVisible(bool visible)
    {
        var type = Type.GetTypeFromProgID("Shell.Application")!;
        var shell = Activator.CreateInstance(type)!;
        try { type.InvokeMember(visible ? "MinimizeAll" : "UndoMinimizeAll", BindingFlags.InvokeMethod, null, shell, null); }
        finally { Marshal.FinalReleaseComObject(shell); }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hwnd);
}

internal sealed class PatternSource : Form
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 40 };
    private int _frame;
    internal PatternSource()
    {
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual; Bounds = new Rectangle(570, 210, 800, 460);
        DoubleBuffered = true; TopMost = true;
        _timer.Tick += (_, _) => { _frame++; Invalidate(); };
        _timer.Start();
    }
    protected override bool ShowWithoutActivation => true;
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear((_frame / 30) % 2 == 0 ? Color.Crimson : Color.RoyalBlue);
        for (var x = -40 + (_frame * 3) % 40; x < Width; x += 40)
            e.Graphics.FillRectangle(Brushes.White, x, 0, 8, Height);
    }
    protected override void Dispose(bool disposing) { if (disposing) _timer.Dispose(); base.Dispose(disposing); }
}
