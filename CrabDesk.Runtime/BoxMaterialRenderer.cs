using System.Drawing;
using System.Drawing.Drawing2D;
using CrabDesk.Core;

namespace CrabDesk.Runtime;

internal readonly record struct BoxMaterialPaint(
    Graphics Graphics,
    GraphicsPath Path,
    RectangleF Bounds,
    Color FillColor,
    bool IsDark,
    bool HasBackdrop);

internal interface IBoxMaterialRenderer
{
    void Fill(in BoxMaterialPaint paint);
}

internal static class BoxMaterialRenderer
{
    private static readonly IBoxMaterialRenderer Solid = new SolidBoxMaterialRenderer();
    private static readonly IBoxMaterialRenderer Acrylic = new AcrylicPreviewBoxMaterialRenderer();

    internal static IBoxMaterialRenderer Get(BoxMaterialKind material) => material switch
    {
        BoxMaterialKind.AcrylicPreview => Acrylic,
        _ => Solid
    };

    private sealed class SolidBoxMaterialRenderer : IBoxMaterialRenderer
    {
        public void Fill(in BoxMaterialPaint paint)
        {
            using var fill = new SolidBrush(paint.FillColor);
            paint.Graphics.FillPath(fill, paint.Path);
        }
    }

    // Stage one keeps the proven desktop HWND/input adapter and moves material
    // painting behind an interface. The layered tint/highlight/noise treatment
    // is the fallback used until the Composition backdrop host is available.
    private sealed class AcrylicPreviewBoxMaterialRenderer : IBoxMaterialRenderer
    {
        public void Fill(in BoxMaterialPaint paint)
        {
            var alpha = paint.HasBackdrop
                ? Math.Clamp(paint.FillColor.A / 3, 48, 96)
                : paint.FillColor.A;
            var top = Color.FromArgb(alpha, ShiftLightness(paint.FillColor, paint.IsDark ? 16 : 6));
            var bottom = Color.FromArgb(alpha, ShiftLightness(paint.FillColor, paint.IsDark ? -7 : -3));
            using (var tint = new LinearGradientBrush(
                       paint.Bounds,
                       top,
                       bottom,
                       LinearGradientMode.Vertical))
            {
                paint.Graphics.FillPath(tint, paint.Path);
            }

            using (var luminosity = new SolidBrush(Color.FromArgb(
                       paint.HasBackdrop ? 18 : paint.IsDark ? 14 : 22,
                       paint.IsDark ? Color.White : Color.Black)))
            {
                paint.Graphics.FillPath(luminosity, paint.Path);
            }

            using var noise = new HatchBrush(
                HatchStyle.Percent05,
                Color.FromArgb(paint.IsDark ? 10 : 7, Color.White),
                Color.Transparent);
            paint.Graphics.FillPath(noise, paint.Path);
        }

        private static Color ShiftLightness(Color color, int amount) => Color.FromArgb(
            color.A,
            Math.Clamp(color.R + amount, 0, 255),
            Math.Clamp(color.G + amount, 0, 255),
            Math.Clamp(color.B + amount, 0, 255));
    }
}
