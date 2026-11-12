using Microsoft.Graphics.Canvas.Effects;
using Windows.UI.Composition;

namespace CrabDesk.WinUI.Services;

internal static class AcrylicBlurEffects
{
    internal static CompositionEffectFactory CreateFactory(Compositor compositor)
    {
        using var blur = new GaussianBlurEffect
        {
            Name = "BackdropBlur", BlurAmount = 0, BorderMode = EffectBorderMode.Hard,
            Source = new CompositionEffectSourceParameter("Backdrop")
        };
        return compositor.CreateEffectFactory(blur, ["BackdropBlur.BlurAmount"]);
    }
}
