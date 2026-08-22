using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace CrabDesk.Native;

/// <summary>
/// Presents a 32-bit premultiplied-alpha bitmap as a child layered window.
/// Alpha-zero pixels reveal the real desktop below them, unlike a WinForms
/// transparency key which is not reliable after a form becomes an Explorer
/// child window.
/// </summary>
public static class LayeredWindowPresenter
{
    private const uint UlwAlpha = 0x00000002;
    private const uint BiRgb = 0;
    private const uint DibRgbColors = 0;
    private const byte AcSrcOver = 0;
    private const byte AcSrcAlpha = 1;
    private static readonly object SurfaceLock = new();
    private static readonly Dictionary<IntPtr, PresentationSurface> Surfaces = [];

    public static bool TryPresent(
        IntPtr hwnd,
        Bitmap bitmap,
        Point screenLocation,
        out string diagnostic)
    {
        diagnostic = string.Empty;
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
        {
            diagnostic = "The layered desktop window is unavailable.";
            return false;
        }

        if (bitmap.Width <= 0 || bitmap.Height <= 0)
        {
            diagnostic = "The layered desktop bitmap is empty.";
            return false;
        }

        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            diagnostic = $"GetDC failed error={Marshal.GetLastWin32Error()}";
            return false;
        }

        try
        {
            lock (SurfaceLock)
            {
                if (!Surfaces.TryGetValue(hwnd, out var surface) ||
                    !surface.Matches(bitmap.Width, bitmap.Height))
                {
                    surface?.Dispose();
                    surface = new PresentationSurface();
                    if (!surface.TryInitialize(bitmap.Width, bitmap.Height, screenDc, out diagnostic))
                    {
                        surface.Dispose();
                        Surfaces.Remove(hwnd);
                        return false;
                    }
                    Surfaces[hwnd] = surface;
                }

                return surface.Present(hwnd, bitmap, screenLocation, screenDc, out diagnostic);
            }
        }
        catch (Exception exception)
        {
            diagnostic = exception.Message;
            return false;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>
    /// Updates only a physical-pixel rectangle of an already presented layered
    /// surface. The persistent DIB keeps the pixels outside the dirty area, so
    /// animation frames avoid another full-monitor bitmap copy and upload.
    /// </summary>
    public static bool TryPresentPartial(
        IntPtr hwnd,
        Bitmap bitmap,
        Point screenLocation,
        Rectangle dirtyPixels,
        out string diagnostic)
    {
        diagnostic = string.Empty;
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
        {
            diagnostic = "The layered desktop window is unavailable.";
            return false;
        }
        if (bitmap.Width <= 0 || bitmap.Height <= 0)
        {
            diagnostic = "The layered desktop bitmap is empty.";
            return false;
        }

        var bitmapBounds = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var clippedDirtyPixels = Rectangle.Intersect(bitmapBounds, dirtyPixels);
        if (clippedDirtyPixels.Width <= 0 || clippedDirtyPixels.Height <= 0)
        {
            diagnostic = "The layered desktop dirty rectangle is empty.";
            return true;
        }
        if (clippedDirtyPixels == bitmapBounds)
        {
            return TryPresent(hwnd, bitmap, screenLocation, out diagnostic);
        }

        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            diagnostic = $"GetDC failed error={Marshal.GetLastWin32Error()}";
            return false;
        }

        try
        {
            lock (SurfaceLock)
            {
                if (!Surfaces.TryGetValue(hwnd, out var surface) ||
                    !surface.Matches(bitmap.Width, bitmap.Height) ||
                    !surface.HasPresented)
                {
                    diagnostic = "The layered desktop surface requires an initial full presentation.";
                    return false;
                }

                return surface.PresentPartial(
                    hwnd,
                    bitmap,
                    screenLocation,
                    clippedDirtyPixels,
                    screenDc,
                    out diagnostic);
            }
        }
        catch (Exception exception)
        {
            diagnostic = exception.Message;
            return false;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>
    /// Releases the persistent DIB used by a surface. Call this when the
    /// associated window is disposed so a restart does not retain GDI memory.
    /// </summary>
    public static void Release(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        lock (SurfaceLock)
        {
            if (Surfaces.Remove(hwnd, out var surface))
            {
                surface.Dispose();
            }
        }
    }

    private sealed class PresentationSurface : IDisposable
    {
        private IntPtr _memoryDc;
        private IntPtr _bitmapHandle;
        private IntPtr _previousBitmap;
        private IntPtr _bits;
        private IntPtr _destinationPointer;
        private IntPtr _sizePointer;
        private IntPtr _sourcePointer;
        private IntPtr _blendPointer;
        private IntPtr _dirtyPointer;
        private int _width;
        private int _height;
        private bool _hasPresented;

        internal bool Matches(int width, int height) => _width == width && _height == height;
        internal bool HasPresented => _hasPresented;

        internal bool TryInitialize(
            int width,
            int height,
            IntPtr screenDc,
            out string diagnostic)
        {
            diagnostic = string.Empty;
            _memoryDc = CreateCompatibleDC(screenDc);
            if (_memoryDc == IntPtr.Zero)
            {
                diagnostic = $"CreateCompatibleDC failed error={Marshal.GetLastWin32Error()}";
                return false;
            }

            var bitmapInfo = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = width,
                    // A negative height requests a top-down DIB, matching
                    // the row order returned by GDI+ LockBits.
                    Height = -height,
                    Planes = 1,
                    BitCount = 32,
                    Compression = BiRgb,
                    SizeImage = checked((uint)(width * height * 4))
                }
            };
            _bitmapHandle = CreateDIBSection(
                screenDc,
                ref bitmapInfo,
                DibRgbColors,
                out _bits,
                IntPtr.Zero,
                0);
            if (_bitmapHandle == IntPtr.Zero || _bits == IntPtr.Zero)
            {
                diagnostic = $"CreateDIBSection failed error={Marshal.GetLastWin32Error()}";
                return false;
            }

            _previousBitmap = SelectObject(_memoryDc, _bitmapHandle);
            if (_previousBitmap == IntPtr.Zero)
            {
                diagnostic = $"SelectObject failed error={Marshal.GetLastWin32Error()}";
                return false;
            }

            _width = width;
            _height = height;
            _destinationPointer = Marshal.AllocHGlobal(Marshal.SizeOf<NativePoint>());
            _sizePointer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeSize>());
            _sourcePointer = Marshal.AllocHGlobal(Marshal.SizeOf<NativePoint>());
            _blendPointer = Marshal.AllocHGlobal(Marshal.SizeOf<BlendFunction>());
            _dirtyPointer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeRect>());
            Marshal.StructureToPtr(new NativeSize(width, height), _sizePointer, false);
            Marshal.StructureToPtr(new NativePoint(0, 0), _sourcePointer, false);
            Marshal.StructureToPtr(
                new BlendFunction(AcSrcOver, 0, byte.MaxValue, AcSrcAlpha),
                _blendPointer,
                false);
            return true;
        }

        internal bool Present(
            IntPtr hwnd,
            Bitmap sourceBitmap,
            Point screenLocation,
            IntPtr screenDc,
            out string diagnostic)
        {
            diagnostic = string.Empty;
            if (!CopyBitmap(sourceBitmap, out diagnostic))
            {
                return false;
            }

            var destination = new NativePoint(screenLocation.X, screenLocation.Y);
            var source = new NativePoint(0, 0);
            var size = new NativeSize(_width, _height);
            var blend = new BlendFunction(AcSrcOver, 0, byte.MaxValue, AcSrcAlpha);
            if (!UpdateLayeredWindow(
                    hwnd,
                    screenDc,
                    ref destination,
                    ref size,
                    _memoryDc,
                    ref source,
                    0,
                    ref blend,
                    UlwAlpha))
            {
                diagnostic = $"UpdateLayeredWindow failed error={Marshal.GetLastWin32Error()}";
                return false;
            }

            _hasPresented = true;
            diagnostic = $"layered bitmap={_width}x{_height}";
            return true;
        }

        internal bool PresentPartial(
            IntPtr hwnd,
            Bitmap sourceBitmap,
            Point screenLocation,
            Rectangle dirtyPixels,
            IntPtr screenDc,
            out string diagnostic)
        {
            diagnostic = string.Empty;
            if (!CopyBitmap(sourceBitmap, dirtyPixels, out diagnostic))
            {
                return false;
            }

            Marshal.StructureToPtr(
                new NativePoint(screenLocation.X, screenLocation.Y),
                _destinationPointer,
                false);
            Marshal.StructureToPtr(
                new NativeRect(
                    dirtyPixels.Left,
                    dirtyPixels.Top,
                    dirtyPixels.Right,
                    dirtyPixels.Bottom),
                _dirtyPointer,
                false);
            var update = new UpdateLayeredWindowInfo
            {
                Size = (uint)Marshal.SizeOf<UpdateLayeredWindowInfo>(),
                DestinationDeviceContext = screenDc,
                DestinationPoint = _destinationPointer,
                WindowSize = _sizePointer,
                SourceDeviceContext = _memoryDc,
                SourcePoint = _sourcePointer,
                Blend = _blendPointer,
                Flags = UlwAlpha,
                DirtyRectangle = _dirtyPointer
            };
            if (!UpdateLayeredWindowIndirect(hwnd, ref update))
            {
                diagnostic = $"UpdateLayeredWindowIndirect failed error={Marshal.GetLastWin32Error()}";
                return false;
            }

            _hasPresented = true;
            diagnostic = $"layered bitmap={_width}x{_height} dirty={dirtyPixels}";
            return true;
        }

        private bool CopyBitmap(Bitmap sourceBitmap, out string diagnostic)
        {
            return CopyBitmap(
                sourceBitmap,
                new Rectangle(0, 0, _width, _height),
                out diagnostic);
        }

        private bool CopyBitmap(
            Bitmap sourceBitmap,
            Rectangle copyBounds,
            out string diagnostic)
        {
            diagnostic = string.Empty;
            BitmapData? data = null;
            try
            {
                data = sourceBitmap.LockBits(
                    copyBounds,
                    ImageLockMode.ReadOnly,
                    PixelFormat.Format32bppPArgb);
                var sourceStride = Math.Abs(data.Stride);
                var rowBytes = checked(copyBounds.Width * 4);
                var totalBytes = checked((nuint)(rowBytes * copyBounds.Height));

                // Format32bppPArgb bitmaps created by the runtime are normally
                // tightly packed. Copy the whole block in one native call;
                // the old row-by-row loop paid for one P/Invoke transition per
                // scanline on every drag frame.
                if (copyBounds.X == 0 && copyBounds.Y == 0 &&
                    copyBounds.Width == _width && copyBounds.Height == _height &&
                    data.Stride == rowBytes)
                {
                    CopyMemory(_bits, data.Scan0, totalBytes);
                    return true;
                }

                for (var row = 0; row < copyBounds.Height; row++)
                {
                    var sourceRow = data.Stride >= 0
                        ? IntPtr.Add(data.Scan0, row * sourceStride)
                        : IntPtr.Add(data.Scan0, (copyBounds.Height - row - 1) * sourceStride);
                    var destinationOffset = checked(
                        ((copyBounds.Top + row) * _width + copyBounds.Left) * 4);
                    CopyMemory(IntPtr.Add(_bits, destinationOffset), sourceRow, (nuint)rowBytes);
                }
                return true;
            }
            catch (Exception exception)
            {
                diagnostic = $"LockBits failed: {exception.Message}";
                return false;
            }
            finally
            {
                if (data is not null)
                {
                    sourceBitmap.UnlockBits(data);
                }
            }
        }

        public void Dispose()
        {
            if (_previousBitmap != IntPtr.Zero && _memoryDc != IntPtr.Zero)
            {
                SelectObject(_memoryDc, _previousBitmap);
                _previousBitmap = IntPtr.Zero;
            }
            if (_bitmapHandle != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(_bitmapHandle);
                _bitmapHandle = IntPtr.Zero;
            }
            if (_memoryDc != IntPtr.Zero)
            {
                DeleteDC(_memoryDc);
                _memoryDc = IntPtr.Zero;
            }
            _bits = IntPtr.Zero;
            Free(ref _destinationPointer);
            Free(ref _sizePointer);
            Free(ref _sourcePointer);
            Free(ref _blendPointer);
            Free(ref _dirtyPointer);
            _width = 0;
            _height = 0;
            _hasPresented = false;
        }

        private static void Free(ref IntPtr pointer)
        {
            if (pointer == IntPtr.Zero)
            {
                return;
            }
            Marshal.FreeHGlobal(pointer);
            pointer = IntPtr.Zero;
        }
    }

    /// <summary>
    /// Returns a compact alpha summary for diagnostics.  A layered surface
    /// can report a successful UpdateLayeredWindow call while its source
    /// bitmap is fully transparent, which makes the child appear missing.
    /// Keep this calculation here so both desktop replacement surfaces use
    /// the exact same presentation path.
    /// </summary>
    private static string DescribeAlpha(Bitmap bitmap)
    {
        BitmapData? data = null;
        try
        {
            data = bitmap.LockBits(
                new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                ImageLockMode.ReadOnly,
                PixelFormat.Format32bppPArgb);
            var stride = Math.Abs(data.Stride);
            var bytes = new byte[stride * bitmap.Height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            var nonZero = 0;
            var maxAlpha = 0;
            for (var offset = 3; offset < bytes.Length; offset += 4)
            {
                var alpha = bytes[offset];
                if (alpha != 0)
                {
                    nonZero++;
                    if (alpha > maxAlpha)
                    {
                        maxAlpha = alpha;
                    }
                }
            }
            return $"alphaPixels={nonZero}; alphaMax={maxAlpha}";
        }
        catch (Exception exception)
        {
            return $"alpha=diagnostic-failed:{exception.GetType().Name}";
        }
        finally
        {
            if (data is not null)
            {
                bitmap.UnlockBits(data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint(int x, int y)
    {
        internal int X = x;
        internal int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize(int width, int height)
    {
        internal int Width = width;
        internal int Height = height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlendFunction(byte blendOp, byte blendFlags, byte sourceConstantAlpha, byte alphaFormat)
    {
        internal byte BlendOp = blendOp;
        internal byte BlendFlags = blendFlags;
        internal byte SourceConstantAlpha = sourceConstantAlpha;
        internal byte AlphaFormat = alphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect(int left, int top, int right, int bottom)
    {
        internal int Left = left;
        internal int Top = top;
        internal int Right = right;
        internal int Bottom = bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UpdateLayeredWindowInfo
    {
        internal uint Size;
        internal IntPtr DestinationDeviceContext;
        internal IntPtr DestinationPoint;
        internal IntPtr WindowSize;
        internal IntPtr SourceDeviceContext;
        internal IntPtr SourcePoint;
        internal uint ColorKey;
        internal IntPtr Blend;
        internal uint Flags;
        internal IntPtr DirtyRectangle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        internal BitmapInfoHeader Header;
        internal uint Color1;
        internal uint Color2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        internal uint Size;
        internal int Width;
        internal int Height;
        internal ushort Planes;
        internal ushort BitCount;
        internal uint Compression;
        internal uint SizeImage;
        internal int XPelsPerMeter;
        internal int YPelsPerMeter;
        internal uint ClrUsed;
        internal uint ClrImportant;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr deviceContext);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateLayeredWindow(
        IntPtr hwnd,
        IntPtr destinationDeviceContext,
        ref NativePoint destinationPoint,
        ref NativeSize size,
        IntPtr sourceDeviceContext,
        ref NativePoint sourcePoint,
        int colorKey,
        ref BlendFunction blend,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateLayeredWindowIndirect(
        IntPtr hwnd,
        ref UpdateLayeredWindowInfo updateInfo);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(
        IntPtr deviceContext,
        ref BitmapInfo bitmapInfo,
        uint usage,
        out IntPtr bits,
        IntPtr section,
        uint offset);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr graphicsObject);

    [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
    private static extern void CopyMemory(IntPtr destination, IntPtr source, UIntPtr length);
}
