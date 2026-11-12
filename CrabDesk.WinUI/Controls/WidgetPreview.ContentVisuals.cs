using CrabDesk.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace CrabDesk.WinUI.Controls;

public sealed partial class WidgetPreview
{
    private SolidColorBrush ContentTone(string asset) => new(Parse(WidgetContentVisuals.Tone(asset, _text.Color.R > 128), _accent.Color));

    private static WidgetColorIcon ContentIcon(string asset, double size = 18, double opacity = 1)
        => new() { AssetName = asset, Width = size, Height = size, Opacity = opacity };

    private Border ContentWash(string asset, byte alpha = 18, double radius = 7)
        => new() { Background = new SolidColorBrush(ContentTone(asset).Color with { A = alpha }), CornerRadius = new CornerRadius(radius) };

    private static Color MixContent(Color from, Color to, double amount) => Color.FromArgb(255,
        (byte)Math.Round(from.R + (to.R - from.R) * amount),
        (byte)Math.Round(from.G + (to.G - from.G) * amount),
        (byte)Math.Round(from.B + (to.B - from.B) * amount));
}
