using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Windows.Graphics.DirectX;
using Windows.UI.Composition;
using WinRT;

namespace CrabDesk.Runtime;

// Upload the existing premultiplied GDI renderer into the same compositor as
// the backdrop. No second HWND submits visible box pixels.
internal sealed class CompositionBitmapDevice : IDisposable
{
    private IntPtr _d3dDevice;
    private IntPtr _d2dDevice;
    private CompositionGraphicsDevice? _graphicsDevice;

    internal CompositionBitmapDevice(Compositor compositor)
    {
        try
        {
            var hr = D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 0x20,
                IntPtr.Zero, 0, 7, out _d3dDevice, out _, out var context);
            if (context != IntPtr.Zero) Marshal.Release(context);
            Marshal.ThrowExceptionForHR(hr);
            var dxgiId = new Guid("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(_d3dDevice, ref dxgiId, out var dxgi));
            try { Marshal.ThrowExceptionForHR(D2D1CreateDevice(dxgi, IntPtr.Zero, out _d2dDevice)); }
            finally { Marshal.Release(dxgi); }
            compositor.As<ICompositorInterop>().CreateGraphicsDevice(_d2dDevice, out var graphics);
            try { _graphicsDevice = MarshalInterface<CompositionGraphicsDevice>.FromAbi(graphics); }
            finally { Marshal.Release(graphics); }
        }
        catch { Dispose(); throw; }
    }

    internal CompositionDrawingSurface CreateSurface(Size size) => _graphicsDevice!.CreateDrawingSurface(
        new Windows.Foundation.Size(size.Width, size.Height),
        DirectXPixelFormat.B8G8R8A8UIntNormalized, DirectXAlphaMode.Premultiplied);

    internal static void Upload(CompositionDrawingSurface surface, Bitmap bitmap)
    {
        var interop = surface.As<ICompositionDrawingSurfaceInterop>();
        var contextId = new Guid("E8F7FE7A-191C-466D-AD95-975678BDA998");
        interop.BeginDraw(IntPtr.Zero, ref contextId, out var context, out var offset);
        try
        {
            // ID2D1RenderTarget vtable slots, as declared in the Windows SDK
            // d2d1.h (IUnknown + ID2D1Resource precede CreateBitmap).
            Method<SetDpi>(context, 51)(context, 96, 96);
            var pixels = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            IntPtr texture = IntPtr.Zero;
            try
            {
                var properties = new BitmapProperties(87, 1, 96, 96);
                Marshal.ThrowExceptionForHR(Method<CreateBitmap>(context, 4)(context,
                    new PixelSize((uint)bitmap.Width, (uint)bitmap.Height), pixels.Scan0,
                    (uint)pixels.Stride, ref properties, out texture));
                var transparent = new ColorF();
                Method<Clear>(context, 47)(context, ref transparent);
                var destination = new RectF(offset.X, offset.Y, offset.X + bitmap.Width, offset.Y + bitmap.Height);
                Method<DrawBitmap>(context, 26)(context, texture, ref destination, 1, 0, IntPtr.Zero);
            }
            finally
            {
                if (texture != IntPtr.Zero) Marshal.Release(texture);
                bitmap.UnlockBits(pixels);
            }
        }
        finally
        {
            try { interop.EndDraw(); }
            finally { Marshal.Release(context); }
        }
    }

    private static T Method<T>(IntPtr instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));

    public void Dispose()
    {
        _graphicsDevice?.Dispose();
        _graphicsDevice = null;
        if (_d2dDevice != IntPtr.Zero) { Marshal.Release(_d2dDevice); _d2dDevice = IntPtr.Zero; }
        if (_d3dDevice != IntPtr.Zero) { Marshal.Release(_d3dDevice); _d3dDevice = IntPtr.Zero; }
    }

    [StructLayout(LayoutKind.Sequential)] private readonly record struct PixelSize(uint Width, uint Height);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct BitmapProperties(int Format, int AlphaMode, float DpiX, float DpiY);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct RectF(float Left, float Top, float Right, float Bottom);
    [StructLayout(LayoutKind.Sequential)] private struct ColorF { public float R, G, B, A; }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateBitmap(IntPtr self, PixelSize size, IntPtr data, uint pitch, ref BitmapProperties properties, out IntPtr bitmap);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void DrawBitmap(IntPtr self, IntPtr bitmap, ref RectF destination, float opacity, int interpolation, IntPtr source);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void Clear(IntPtr self, ref ColorF color);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void SetDpi(IntPtr self, float x, float y);
    [DllImport("d3d11.dll")] private static extern int D3D11CreateDevice(IntPtr adapter, int driver, IntPtr software, uint flags, IntPtr levels, uint levelCount, uint sdk, out IntPtr device, out int featureLevel, out IntPtr context);
    [DllImport("d2d1.dll")] private static extern int D2D1CreateDevice(IntPtr dxgiDevice, IntPtr properties, out IntPtr device);

    [ComImport, Guid("25297D5C-3AD4-4C9C-B5CF-E36A38512330"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICompositorInterop
    {
        void CreateCompositionSurfaceForHandle(IntPtr handle, out IntPtr surface);
        void CreateCompositionSurfaceForSwapChain(IntPtr swapChain, out IntPtr surface);
        void CreateGraphicsDevice(IntPtr device, out IntPtr graphicsDevice);
    }
    [ComImport, Guid("FD04E6E3-FE0C-4C3C-AB19-A07601A576EE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICompositionDrawingSurfaceInterop
    {
        void BeginDraw(IntPtr updateRect, ref Guid iid, out IntPtr updateObject, out Point offset);
        void EndDraw();
    }
}
