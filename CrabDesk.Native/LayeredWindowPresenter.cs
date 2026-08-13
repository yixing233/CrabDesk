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
    private const byte AcSrcOver = 0;
    private const byte AcSrcAlpha = 1;

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

        IntPtr memoryDc = IntPtr.Zero;
        IntPtr bitmapHandle = IntPtr.Zero;
        IntPtr previousBitmap = IntPtr.Zero;
        try
        {
            memoryDc = CreateCompatibleDC(screenDc);
            if (memoryDc == IntPtr.Zero)
            {
                diagnostic = $"CreateCompatibleDC failed error={Marshal.GetLastWin32Error()}";
                return false;
            }

            bitmapHandle = bitmap.GetHbitmap(Color.FromArgb(0, 0, 0, 0));
            if (bitmapHandle == IntPtr.Zero)
            {
                diagnostic = $"GetHbitmap failed error={Marshal.GetLastWin32Error()}";
                return false;
            }

            previousBitmap = SelectObject(memoryDc, bitmapHandle);
            if (previousBitmap == IntPtr.Zero)
            {
                diagnostic = $"SelectObject failed error={Marshal.GetLastWin32Error()}";
                return false;
            }

            var destination = new NativePoint(screenLocation.X, screenLocation.Y);
            var source = new NativePoint(0, 0);
            var size = new NativeSize(bitmap.Width, bitmap.Height);
            var blend = new BlendFunction(AcSrcOver, 0, byte.MaxValue, AcSrcAlpha);
            if (!UpdateLayeredWindow(
                    hwnd,
                    screenDc,
                    ref destination,
                    ref size,
                    memoryDc,
                    ref source,
                    0,
                    ref blend,
                    UlwAlpha))
            {
                diagnostic = $"UpdateLayeredWindow failed error={Marshal.GetLastWin32Error()}";
                return false;
            }

            diagnostic = $"layered bitmap={bitmap.Width}x{bitmap.Height}; {DescribeAlpha(bitmap)}";
            return true;
        }
        catch (ExternalException exception)
        {
            diagnostic = exception.Message;
            return false;
        }
        finally
        {
            if (previousBitmap != IntPtr.Zero && memoryDc != IntPtr.Zero)
            {
                SelectObject(memoryDc, previousBitmap);
            }
            if (bitmapHandle != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(bitmapHandle);
            }
            if (memoryDc != IntPtr.Zero)
            {
                DeleteDC(memoryDc);
            }
            ReleaseDC(IntPtr.Zero, screenDc);
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

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr graphicsObject);
}
