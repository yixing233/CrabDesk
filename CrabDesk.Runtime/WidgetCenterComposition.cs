using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using Windows.UI.Composition.Core;
using WinRT;

namespace CrabDesk.Runtime;

internal sealed class WidgetCenterComposition : IDisposable
{
    private readonly CompositorController _controller;
    private readonly DesktopWindowTarget _target;
    private readonly ContainerVisual _root;
    private readonly SpriteVisual _backdrop;
    private readonly SpriteVisual _content;
    private readonly CompositionBackdropBrush _backdropBrush;
    private readonly CompositionSurfaceBrush _contentBrush;
    private readonly CompositionBitmapDevice _device;
    private readonly CompositionBitmapDevice.UploadBitmap _upload = new();
    private readonly CompositionRoundedRectangleGeometry _geometry;
    private readonly CompositionGeometricClip _clip;
    private CompositionDrawingSurface? _surface;
    private Size _size;

    internal WidgetCenterComposition(IntPtr window)
    {
        var margins = new Margins(-1, -1, -1, -1);
        Marshal.ThrowExceptionForHR(DwmExtendFrameIntoClientArea(window, ref margins));
        var enabled = 1;
        Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(window, 17, ref enabled, sizeof(int)));
        // The rounded outline travels with the visual instead of the stationary HWND.
        var borderColor = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
        _ = DwmSetWindowAttribute(window, 34, ref borderColor, sizeof(int));
        _controller = new CompositorController();
        var compositor = _controller.Compositor;
        compositor.As<ICompositorDesktopInterop>().CreateDesktopWindowTarget(window, false, out var target);
        try { _target = MarshalInterface<DesktopWindowTarget>.FromAbi(target); }
        finally { Marshal.Release(target); }
        _root = compositor.CreateContainerVisual();
        _backdropBrush = compositor.CreateHostBackdropBrush();
        _backdrop = compositor.CreateSpriteVisual();
        _backdrop.Brush = _backdropBrush;
        _contentBrush = compositor.CreateSurfaceBrush();
        _contentBrush.Stretch = CompositionStretch.None;
        _contentBrush.HorizontalAlignmentRatio = _contentBrush.VerticalAlignmentRatio = 0;
        _content = compositor.CreateSpriteVisual();
        _content.Brush = _contentBrush;
        _geometry = compositor.CreateRoundedRectangleGeometry();
        _clip = compositor.CreateGeometricClip(_geometry);
        _root.Clip = _clip;
        _root.Children.InsertAtBottom(_backdrop);
        _root.Children.InsertAtTop(_content);
        _device = new CompositionBitmapDevice(compositor);
        _target.Root = _root;
    }

    internal void SetAcrylic(bool enabled) => _backdrop.IsVisible = enabled;

    internal void SetOffset(float offset)
    {
        _root.StopAnimation(nameof(Visual.Offset));
        _root.Offset = new Vector3(offset, 0, 0);
        _controller.Commit();
    }

    internal void AnimateOffset(float offset, TimeSpan duration)
    {
        var compositor = _controller.Compositor;
        using var easing = compositor.CreateCubicBezierEasingFunction(new(1f / 3, 1), new(2f / 3, 1));
        using var animation = compositor.CreateVector3KeyFrameAnimation();
        // StartingValue hands off the compositor's current presentation value
        // when a closing animation is replaced by an opening animation.
        animation.InsertExpressionKeyFrame(0, "this.StartingValue");
        animation.InsertKeyFrame(1, new Vector3(offset, 0, 0), easing);
        animation.Duration = duration;
        _root.StartAnimation(nameof(Visual.Offset), animation);
        _controller.Commit();
    }

    internal void Present(Bitmap bitmap, double scale)
    {
        if (_size != bitmap.Size)
        {
            _contentBrush.Surface = null;
            _surface?.Dispose();
            _size = bitmap.Size;
            _surface = _device.CreateSurface(_size);
            _contentBrush.Surface = _surface;
            _root.Size = _backdrop.Size = _content.Size = _geometry.Size = new Vector2(_size.Width, _size.Height);
            _geometry.CornerRadius = new Vector2((float)(12 * scale));
        }
        CompositionBitmapDevice.Upload(_surface!, bitmap, new Rectangle(Point.Empty, _size), _upload);
        _controller.Commit();
    }

    public void Dispose()
    {
        _target.Root = null;
        _root.Children.RemoveAll();
        _surface?.Dispose();
        _upload.Dispose();
        _device.Dispose();
        _content.Dispose();
        _backdrop.Dispose();
        _contentBrush.Dispose();
        _backdropBrush.Dispose();
        _clip.Dispose();
        _geometry.Dispose();
        _root.Dispose();
        _target.Dispose();
        _controller.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins(int left, int right, int top, int bottom) { public int Left = left, Right = right, Top = top, Bottom = bottom; }
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr window, ref Margins margins);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    [ComImport, Guid("29E691FA-4567-4DCA-B319-D0F207EB6807"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICompositorDesktopInterop
    {
        void CreateDesktopWindowTarget(IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool topmost, out IntPtr target);
    }
}
