using System.Drawing;

namespace CrabDesk.Runtime;

internal static class IconImageLayout
{
    internal static RectangleF Contain(Image image, RectangleF bounds)
    {
        try
        {
            if (image.Width <= 0 || image.Height <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
            {
                return RectangleF.Empty;
            }

            var scale = Math.Min(bounds.Width / image.Width, bounds.Height / image.Height);
            var width = image.Width * scale;
            var height = image.Height * scale;
            return new RectangleF(
                bounds.X + (bounds.Width - width) / 2f,
                bounds.Y + (bounds.Height - height) / 2f,
                width,
                height);
        }
        catch (ArgumentException)
        {
            return RectangleF.Empty;
        }
        catch (ObjectDisposedException)
        {
            return RectangleF.Empty;
        }
    }
}
