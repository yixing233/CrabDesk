using System.Drawing;
using System.Runtime.InteropServices;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace CrabDesk.Native;

/// <summary>
/// Supplies the shell drag loop with a compact bitmap that follows the pointer.
/// The shell owns the displayed copy, so the source bitmap can be disposed as
/// soon as this method returns.
/// </summary>
public static class DesktopDragImageHelper
{
    public static bool TryInitialize(
        ComDataObject? dataObject,
        Bitmap image,
        Point cursorOffset)
    {
        if (dataObject is null || image.Width <= 0 || image.Height <= 0)
        {
            return false;
        }

        IntPtr bitmapHandle = IntPtr.Zero;
        try
        {
            bitmapHandle = image.GetHbitmap();
            var dragImage = new ShellDragImage
            {
                Size = new NativeSize(image.Width, image.Height),
                CursorOffset = new NativePoint(
                    Math.Clamp(cursorOffset.X, 0, image.Width - 1),
                    Math.Clamp(cursorOffset.Y, 0, image.Height - 1)),
                BitmapHandle = bitmapHandle,
                ColorKey = unchecked((int)0xFFFFFFFF)
            };
            var helper = (IDragSourceHelper)new DragDropHelper();
            helper.InitializeFromBitmap(ref dragImage, dataObject);
            return true;
        }
        // A drag image is purely visual. Shell or COM failures must leave the
        // ordinary WinForms drag loop usable with its default feedback.
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (bitmapHandle != IntPtr.Zero)
            {
                DeleteObject(bitmapHandle);
            }
        }
    }

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr objectHandle);

    [ComImport]
    [Guid("DE5BF786-477A-11D2-839D-00C04FD918D0")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDragSourceHelper
    {
        void InitializeFromBitmap(
            ref ShellDragImage dragImage,
            [MarshalAs(UnmanagedType.Interface)] ComDataObject dataObject);

        void InitializeFromWindow(
            IntPtr windowHandle,
            ref NativePoint cursorOffset,
            [MarshalAs(UnmanagedType.Interface)] ComDataObject dataObject);
    }

    [ComImport]
    [Guid("4657278A-411B-11D2-839A-00C04FD918D0")]
    private class DragDropHelper
    {
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShellDragImage
    {
        public NativeSize Size;
        public NativePoint CursorOffset;
        public IntPtr BitmapHandle;
        public int ColorKey;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public NativeSize(int width, int height)
        {
            Width = width;
            Height = height;
        }

        public int Width;
        public int Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public NativePoint(int x, int y)
        {
            X = x;
            Y = y;
        }

        public int X;
        public int Y;
    }
}
