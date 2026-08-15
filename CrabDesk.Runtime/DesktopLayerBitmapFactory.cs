using System.Drawing;
using System.Drawing.Imaging;

namespace CrabDesk.Runtime;

/// <summary>
/// Creates backing bitmaps for desktop layers whose geometry is explicitly
/// transformed from DIPs to physical pixels by the renderer.
/// </summary>
internal static class DesktopLayerBitmapFactory
{
    private const float LogicalDpi = 96f;

    internal static Bitmap Create(int width, int height)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        // A bitmap created on a PerMonitorV2 thread inherits the monitor DPI.
        // Layer renderers already apply that DPI as a graphics transform, so
        // retain a logical canvas to keep point fonts from scaling twice.
        bitmap.SetResolution(LogicalDpi, LogicalDpi);
        return bitmap;
    }
}
