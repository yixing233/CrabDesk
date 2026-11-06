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

    internal static void Upload(CompositionDrawingSurface surface, Bitmap bitmap, Rectangle dirty, UploadBitmap upload)
    {
        var interop = surface.As<ICompositionDrawingSurfaceInterop>();
        var contextId = new Guid("E8F7FE7A-191C-466D-AD95-975678BDA998");
        var bounds = new PixelRect(dirty.Left, dirty.Top, dirty.Right, dirty.Bottom);
        Marshal.StructureToPtr(bounds, upload.RectanglePointer, false);
        interop.BeginDraw(upload.RectanglePointer, ref contextId, out var context, out var offset);
        try
        {
            // ID2D1RenderTarget vtable slots, as declared in the Windows SDK
            // d2d1.h (IUnknown + ID2D1Resource precede CreateBitmap).
            Method<SetDpi>(context, 51)(context, 96, 96);
            upload.EnsureBitmap(context, bitmap.Size);
            var pixels = bitmap.LockBits(dirty,
                ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                // ID2D1Bitmap::CopyFromMemory updates the resident upload bitmap.
                Marshal.ThrowExceptionForHR(Method<CopyFromMemory>(upload.BitmapPointer, 10)(upload.BitmapPointer,
                    ref bounds, pixels.Scan0, (uint)pixels.Stride));
                var transparent = new ColorF();
                Method<Clear>(context, 47)(context, ref transparent);
                var destination = new RectF(offset.X, offset.Y, offset.X + dirty.Width, offset.Y + dirty.Height);
                var source = new RectF(dirty.Left, dirty.Top, dirty.Right, dirty.Bottom);
                Method<DrawBitmap>(context, 26)(context, upload.BitmapPointer, ref destination, 1, 0, ref source);
            }
            finally
            {
                bitmap.UnlockBits(pixels);
            }
        }
        finally
        {
            try { interop.EndDraw(); }
            finally { Marshal.Release(context); }
        }
    }

    internal sealed class UploadBitmap : IDisposable
    {
        internal IntPtr BitmapPointer { get; private set; }
        internal IntPtr RectanglePointer { get; private set; } = Marshal.AllocHGlobal(16);
        private Size _size;

        internal void EnsureBitmap(IntPtr context, Size size)
        {
            if (BitmapPointer != IntPtr.Zero && _size == size) return;
            if (BitmapPointer != IntPtr.Zero) { Marshal.Release(BitmapPointer); BitmapPointer = IntPtr.Zero; }
            var properties = new BitmapProperties(87, 1, 96, 96);
            Marshal.ThrowExceptionForHR(Method<CreateBitmap>(context, 4)(context,
                new PixelSize((uint)size.Width, (uint)size.Height), IntPtr.Zero, 0, ref properties, out var bitmap));
            BitmapPointer = bitmap;
            _size = size;
        }

        public void Dispose()
        {
            if (BitmapPointer != IntPtr.Zero) { Marshal.Release(BitmapPointer); BitmapPointer = IntPtr.Zero; }
            if (RectanglePointer != IntPtr.Zero) { Marshal.FreeHGlobal(RectanglePointer); RectanglePointer = IntPtr.Zero; }
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
    [StructLayout(LayoutKind.Sequential)] private readonly record struct PixelRect(int Left, int Top, int Right, int Bottom);
    [StructLayout(LayoutKind.Sequential)] private struct ColorF { public float R, G, B, A; }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateBitmap(IntPtr self, PixelSize size, IntPtr data, uint pitch, ref BitmapProperties properties, out IntPtr bitmap);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void DrawBitmap(IntPtr self, IntPtr bitmap, ref RectF destination, float opacity, int interpolation, ref RectF source);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CopyFromMemory(IntPtr self, ref PixelRect rectangle, IntPtr data, uint pitch);
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
