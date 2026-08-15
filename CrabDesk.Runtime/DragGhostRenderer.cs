using System.Drawing;
using System.Drawing.Drawing2D;

namespace CrabDesk.Runtime;

/// <summary>
/// The single mouse-following drag ghost shared by every drag that needs a
/// custom image: external file drops and box-item drags. Painted in the
/// native Explorer style - a translucent icon with its label beneath, stacked
/// with a count badge when several items are dragged together - so every
/// drag looks exactly like the desktop icon drag itself. Drawn in DIP space
/// (callers apply the surface scale transform).
/// </summary>
internal static class DragGhostRenderer
{
    public static void Draw(
        Graphics graphics,
        PointF pointerDip,
        Bitmap? icon,
        string label,
        int count,
        Font font)
    {
        var iconSize = 32f;
        var stackCount = Math.Min(3, Math.Max(1, count));
        var stackOffset = (stackCount - 1) * 3f;
        var origin = new PointF(
            pointerDip.X + 10 - stackOffset * 0.5f,
            pointerDip.Y + 10 - stackOffset * 0.5f);

        // Stacked translucent icons, matching the native multi-item drag look.
        if (icon is not null)
        {
            for (var index = stackCount - 1; index >= 0; index--)
            {
                var offset = index * 3f;
                DrawWithAlpha(
                    graphics,
                    icon,
                    new RectangleF(origin.X + offset, origin.Y + offset, iconSize, iconSize),
                    0.86f);
            }
        }

        // A small count badge for multi-item drags, like the shell drag image.
        if (count > 1)
        {
            var badgeText = count.ToString();
            using var badgeFont = new Font("Segoe UI", 8f, FontStyle.Bold, GraphicsUnit.Point);
            var badgeWidth = Math.Max(16f, graphics.MeasureString(badgeText, badgeFont).Width + 7f);
            var badge = new RectangleF(
                origin.X + iconSize - badgeWidth * 0.35f,
                origin.Y - 6,
                badgeWidth,
                16f);
            using var badgePath = RoundedRectangle(badge, 8);
            using var badgeFill = new SolidBrush(Color.FromArgb(215, 40, 44, 52));
            using var badgeTextBrush = new SolidBrush(Color.White);
            using var badgeFormat = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            graphics.FillPath(badgeFill, badgePath);
            graphics.DrawString(badgeText, badgeFont, badgeTextBrush, badge, badgeFormat);
        }

        // The label beneath the icon, styled like the desktop icon labels.
        if (!string.IsNullOrEmpty(label))
        {
            var textBounds = new RectangleF(
                origin.X - 44,
                origin.Y + iconSize + 2,
                iconSize + 88,
                18f);
            using var shadow = new SolidBrush(Color.FromArgb(180, Color.Black));
            using var textBrush = new SolidBrush(Color.FromArgb(238, Color.White));
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.LineLimit
            };
            var shadowBounds = textBounds;
            shadowBounds.Offset(1, 1);
            graphics.DrawString(label, font, shadow, shadowBounds, format);
            graphics.DrawString(label, font, textBrush, textBounds, format);
        }
    }

    private static void DrawWithAlpha(
        Graphics graphics,
        Image image,
        RectangleF bounds,
        float alpha)
    {
        alpha = Math.Clamp(alpha, 0, 1);
        if (alpha >= 0.999f)
        {
            graphics.DrawImage(image, bounds);
            return;
        }

        using var attributes = new System.Drawing.Imaging.ImageAttributes();
        var matrix = new System.Drawing.Imaging.ColorMatrix { Matrix33 = alpha };
        attributes.SetColorMatrix(
            matrix,
            System.Drawing.Imaging.ColorMatrixFlag.Default,
            System.Drawing.Imaging.ColorAdjustType.Bitmap);
        graphics.DrawImage(
            image,
            Rectangle.Round(bounds),
            0,
            0,
            image.Width,
            image.Height,
            GraphicsUnit.Pixel,
            attributes);
    }

    private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
