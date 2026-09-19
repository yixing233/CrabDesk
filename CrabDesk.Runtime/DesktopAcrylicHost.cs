using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;
using CrabDesk.Core;
using CrabDesk.Native;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using Windows.UI.Composition.Core;
using WinRT;
using Forms = System.Windows.Forms;

namespace CrabDesk.Runtime;

internal readonly record struct AcrylicBoxRegion(Guid Id, RectangleF ScreenBounds, float CornerRadius);

/// <summary>
/// Hosts the live backdrop and sharp box pixels in one explicitly committed
/// composition frame. Layered child windows supply only mouse input.
/// </summary>
internal sealed class DesktopAcrylicHost : Forms.Form
{
    private readonly IntPtr _desktopView;
    private readonly Forms.Panel _anchor = new() { Visible = false, Size = new Size(1, 1) };
    private readonly Forms.Timer _orderTimer = new() { Interval = 500 };
    private readonly Dictionary<Guid, BoxVisual> _boxes = [];
    private readonly Rectangle _screenBounds;
    private Compositor? _compositor;
    private CompositorController? _controller;
    private DesktopWindowTarget? _target;
    private ContainerVisual? _root;
    private ContainerVisual? _backdropRoot;
    private CompositionBitmapDevice? _bitmapDevice;
    private readonly Dictionary<string, ForegroundFrame> _foregrounds = [];
    private CompositionBackdropBrush? _backdrop;
    private Windows.System.DispatcherQueueController? _queueController;
    private readonly Windows.UI.ViewManagement.UISettings _uiSettings = new();
    private DesktopDropForwarder? _dropForwarder;
    private bool _effectsEnabled;
    private bool _desktopDisplayRequested;
    private bool _restoreQueued;
    private bool _restoring;
    private bool _restoreFailed;

    internal static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);
    internal IntPtr AnchorHandle => _anchor.Handle;
    internal bool EffectsEnabled => _effectsEnabled;
    internal Action? EffectsChanged { get; set; }

    internal DesktopAcrylicHost(IntPtr desktopView, IReadOnlyList<MonitorLayout> monitors)
    {
        _desktopView = desktopView;
        _screenBounds = monitors.Select(m => Rectangle.Round(new RectangleF(
                (float)m.PixelBounds.X, (float)m.PixelBounds.Y,
                (float)m.PixelBounds.Width, (float)m.PixelBounds.Height)))
            .Aggregate(Rectangle.Union);
        FormBorderStyle = Forms.FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = Forms.FormStartPosition.Manual;
        AutoScaleMode = Forms.AutoScaleMode.None;
        Text = "CrabDesk Acrylic Desktop";
        BackColor = Color.Black;
        Bounds = _screenBounds;
        Controls.Add(_anchor);
        var region = new Region();
        region.MakeEmpty();
        foreach (var monitor in monitors)
            region.Union(new RectangleF((float)monitor.PixelWorkArea.X - _screenBounds.X,
                (float)monitor.PixelWorkArea.Y - _screenBounds.Y,
                (float)monitor.PixelWorkArea.Width, (float)monitor.PixelWorkArea.Height));
        Region = region;
        _orderTimer.Tick += (_, _) =>
        {
            // Explorer may hide owned top-level windows during Win+D. Actual
            // HWND visibility is not the application's requested visibility.
            if (_desktopDisplayRequested) TryRestoreDesktopPlacement();
            var enabled = _uiSettings.AdvancedEffectsEnabled && !Forms.SystemInformation.HighContrast;
            if (enabled == _effectsEnabled) return;
            _effectsEnabled = enabled;
            if (_backdropRoot is not null) _backdropRoot.IsVisible = enabled;
            EffectsChanged?.Invoke();
            _controller?.Commit();
        };
    }

    internal void InitializeComposition()
    {
        DesktopAcrylicWindowTools.Initialize(Handle, _desktopView);
        if (Windows.System.DispatcherQueue.GetForCurrentThread() is null)
        {
            Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(new(12, 2, 2), out var rawQueue));
            try { _queueController = MarshalInterface<Windows.System.DispatcherQueueController>.FromAbi(rawQueue); }
            finally { Marshal.Release(rawQueue); }
        }
        _controller = new CompositorController();
        _compositor = _controller.Compositor;
        _compositor.As<ICompositorDesktopInterop>().CreateDesktopWindowTarget(Handle, false, out var rawTarget);
        try { _target = MarshalInterface<DesktopWindowTarget>.FromAbi(rawTarget); }
        finally { Marshal.Release(rawTarget); }
        _root = _compositor.CreateContainerVisual();
        _backdropRoot = _compositor.CreateContainerVisual();
        _root.Children.InsertAtBottom(_backdropRoot);
        _bitmapDevice = new CompositionBitmapDevice(_compositor);
        _backdrop = _compositor.CreateHostBackdropBrush();
        _target.Root = _root;
        _effectsEnabled = _uiSettings.AdvancedEffectsEnabled && !Forms.SystemInformation.HighContrast;
        _backdropRoot.IsVisible = _effectsEnabled;
        _ = AnchorHandle;
        _controller.Commit();
    }

    private void Synchronize(IEnumerable<AcrylicBoxRegion> regions)
    {
        if (_root is null || _compositor is null) return;
        var seen = new HashSet<Guid>();
        foreach (var region in regions)
        {
            if (region.ScreenBounds.Width <= 0 || region.ScreenBounds.Height <= 0) continue;
            if (!seen.Add(region.Id)) continue;
            if (!_boxes.TryGetValue(region.Id, out var box))
            {
                box = new BoxVisual(_compositor, _backdrop!);
                _boxes.Add(region.Id, box);
                _backdropRoot!.Children.InsertAtTop(box.Visual);
            }
            box.Update(region, _screenBounds.Location);
        }
        foreach (var id in _boxes.Keys.Where(id => !seen.Contains(id)).ToArray())
        {
            var box = _boxes[id];
            _backdropRoot!.Children.Remove(box.Visual);
            box.Dispose();
            _boxes.Remove(id);
        }
    }

    internal void PresentForeground(string monitorId, Bitmap bitmap, Point origin, IReadOnlyList<AcrylicBoxRegion> regions)
    {
        if (_compositor is null || _bitmapDevice is null || _root is null)
            throw new InvalidOperationException("The acrylic compositor is not initialized.");
        if (!_foregrounds.TryGetValue(monitorId, out var frame))
        {
            frame = new ForegroundFrame(_compositor);
            _foregrounds.Add(monitorId, frame);
            _root.Children.InsertAtTop(frame.Visual);
        }
        // Upload into a back surface, then publish pixels and backdrop geometry
        // in this compositor transaction. The displayed frame is never mutated.
        frame.Upload(_bitmapDevice, bitmap);
        frame.Regions = regions.ToArray();
        // A monitor handoff transfers the region with the new foreground frame.
        var currentIds = regions.Select(region => region.Id).ToHashSet();
        foreach (var other in _foregrounds.Values.Where(value => value != frame))
            other.Regions = other.Regions.Where(region => !currentIds.Contains(region.Id)).ToArray();
        Synchronize(_foregrounds.Values.SelectMany(value => value.Regions));
        frame.Publish(new Point(origin.X - _screenBounds.X, origin.Y - _screenBounds.Y), bitmap.Size);
        _controller!.Commit();
    }

    internal async Task WaitForCommitAsync()
    {
        if (_controller is not null) await _controller.EnsurePreviousCommitCompletedAsync();
    }

    internal void ShowAtDesktop()
    {
        _desktopDisplayRequested = true;
        Show();
        _orderTimer.Start();
        EnsureDesktopPlacement();
    }

    /// <summary>
    /// Registers this host as an OLE drop target and routes every drag event it
    /// receives to the desktop surface under the pointer. Required because an
    /// external process's drag hit-tests this top-level window directly; see
    /// <see cref="DesktopDropForwarder"/>.
    /// </summary>
    internal void SetDropForwarding(Func<Point, IDesktopDropForwardTarget?> resolve)
    {
        _dropForwarder = new DesktopDropForwarder(resolve);
        AllowDrop = true;
    }

    protected override void OnDragEnter(Forms.DragEventArgs drgevent)
    {
        base.OnDragEnter(drgevent);
        _dropForwarder?.DragOver(drgevent);
    }

    protected override void OnDragOver(Forms.DragEventArgs drgevent)
    {
        base.OnDragOver(drgevent);
        _dropForwarder?.DragOver(drgevent);
    }

    protected override void OnDragLeave(EventArgs e)
    {
        base.OnDragLeave(e);
        _dropForwarder?.DragLeave();
    }

    protected override void OnDragDrop(Forms.DragEventArgs drgevent)
    {
        base.OnDragDrop(drgevent);
        _dropForwarder?.DragDrop(drgevent);
    }

    internal void HideFromDesktop()
    {
        _desktopDisplayRequested = false;
        _orderTimer.Stop();
        Hide();
    }

    internal void EnsureDesktopPlacement()
    {
        if (_desktopDisplayRequested && !TryRestoreDesktopPlacement())
            throw new InvalidOperationException("The acrylic desktop host could not be made visible above Explorer.");
    }

    private bool TryRestoreDesktopPlacement()
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return false;
        if (_restoring) return true;
        _restoring = true;
        try
        {
            var restored = DesktopAcrylicWindowTools.PlaceAtDesktop(Handle, _desktopView);
            if (!restored && !_restoreFailed)
                DiagnosticLog.Error("Acrylic desktop placement failed",
                    new InvalidOperationException("The desktop host is hidden, minimized, or no longer attached to Explorer."));
            _restoreFailed = !restored;
            return restored;
        }
        finally { _restoring = false; }
    }

    private void QueueDesktopRestore()
    {
        if (!_desktopDisplayRequested || _restoreQueued || _restoring || IsDisposed || Disposing || !IsHandleCreated)
            return;
        if (DesktopAcrylicWindowTools.IsReadyAtDesktop(Handle, _desktopView)) return;
        _restoreQueued = true;
        BeginInvoke((Action)(() =>
        {
            _restoreQueued = false;
            if (_desktopDisplayRequested) TryRestoreDesktopPlacement();
        }));
    }

    protected override bool ShowWithoutActivation => true;
    protected override Forms.CreateParams CreateParams
    {
        get
        {
            var p = base.CreateParams;
            // Keep redirection: NOREDIRECTIONBITMAP prevents the existing
            // UpdateLayeredWindow child surfaces from appearing on this host.
            p.ExStyle |= 0x08000000 | 0x00000080;
            return p;
        }
    }
    protected override void OnPaintBackground(Forms.PaintEventArgs e) { }
    protected override void OnPaint(Forms.PaintEventArgs e) { }
    protected override void WndProc(ref Forms.Message message)
    {
        if (message.Msg == 0x0021) { message.Result = new IntPtr(3); return; } // MA_NOACTIVATE
        // HTTRANSPARENT hands mouse input to the layered children on this
        // thread. It does not reach an OLE drag from another process; those
        // arrive through the drop target registered in SetDropForwarding.
        if (message.Msg == 0x0084) { message.Result = new IntPtr(-1); return; }
        base.WndProc(ref message);
        if (message.Msg is 0x0018 or 0x0047) QueueDesktopRestore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _desktopDisplayRequested = false;
            _dropForwarder = null;
            _orderTimer.Dispose();
            EffectsChanged = null;
            if (IsHandleCreated) DesktopAcrylicWindowTools.Release(Handle);
            _root?.Children.RemoveAll();
            foreach (var frame in _foregrounds.Values) frame.Dispose();
            _foregrounds.Clear();
            _backdropRoot?.Children.RemoveAll();
            foreach (var box in _boxes.Values) box.Dispose();
            _boxes.Clear();
            if (_target is not null) _target.Root = null;
            _backdrop?.Dispose();
            _backdropRoot?.Dispose();
            _root?.Dispose();
            _target?.Dispose();
            _bitmapDevice?.Dispose();
            _controller?.Dispose();
            _controller = null;
            _backdropRoot = null; _bitmapDevice = null;
            _target = null; _root = null; _compositor = null; _backdrop = null;
            if (_queueController is not null)
            {
                _ = _queueController.ShutdownQueueAsync();
                _queueController = null;
            }
        }
        base.Dispose(disposing);
    }

    private sealed class ForegroundFrame : IDisposable
    {
        internal readonly SpriteVisual Visual;
        private readonly CompositionSurfaceBrush _brush;
        private CompositionDrawingSurface? _front;
        private CompositionDrawingSurface? _back;
        private Size _size;
        internal IReadOnlyList<AcrylicBoxRegion> Regions = Array.Empty<AcrylicBoxRegion>();

        internal ForegroundFrame(Compositor compositor)
        {
            Visual = compositor.CreateSpriteVisual();
            _brush = compositor.CreateSurfaceBrush();
            _brush.Stretch = CompositionStretch.None;
            _brush.HorizontalAlignmentRatio = _brush.VerticalAlignmentRatio = 0;
            Visual.Brush = _brush;
        }

        internal void Upload(CompositionBitmapDevice device, Bitmap bitmap)
        {
            if (_size != bitmap.Size)
            {
                _back?.Dispose();
                _back = null;
                _size = bitmap.Size;
            }
            _back ??= device.CreateSurface(bitmap.Size);
            CompositionBitmapDevice.Upload(_back, bitmap);
        }

        internal void Publish(Point origin, Size size)
        {
            (_front, _back) = (_back, _front);
            _brush.Surface = _front;
            Visual.Offset = new Vector3(origin.X, origin.Y, 0);
            Visual.Size = new Vector2(size.Width, size.Height);
            if (_back is not null && (_back.Size.Width != size.Width || _back.Size.Height != size.Height))
            {
                _back.Dispose();
                _back = null;
            }
        }

        public void Dispose()
        {
            Visual.Dispose(); _brush.Dispose(); _front?.Dispose(); _back?.Dispose();
        }
    }

    private sealed class BoxVisual : IDisposable
    {
        internal readonly SpriteVisual Visual;
        private readonly CompositionRoundedRectangleGeometry _geometry;
        private readonly CompositionGeometricClip _clip;
        private AcrylicBoxRegion? _previous;
        internal BoxVisual(Compositor compositor, CompositionBrush brush)
        {
            Visual = compositor.CreateSpriteVisual();
            Visual.Brush = brush;
            _geometry = compositor.CreateRoundedRectangleGeometry();
            _clip = compositor.CreateGeometricClip(_geometry);
            Visual.Clip = _clip;
        }
        internal void Update(AcrylicBoxRegion region, Point origin)
        {
            if (_previous == region) return;
            _previous = region;
            var bounds = region.ScreenBounds;
            Visual.Offset = new Vector3(bounds.X - origin.X, bounds.Y - origin.Y, 0);
            Visual.Size = _geometry.Size = new Vector2(bounds.Width, bounds.Height);
            _geometry.CornerRadius = new Vector2(Math.Min(region.CornerRadius, Math.Min(bounds.Width, bounds.Height) / 2));
        }
        public void Dispose() { Visual.Dispose(); _clip.Dispose(); _geometry.Dispose(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct QueueOptions(int Size, int ThreadType, int ApartmentType);
    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(QueueOptions options, out IntPtr controller);
    [ComImport, Guid("29E691FA-4567-4DCA-B319-D0F207EB6807"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICompositorDesktopInterop
    {
        void CreateDesktopWindowTarget(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool topmost, out IntPtr target);
    }
}
