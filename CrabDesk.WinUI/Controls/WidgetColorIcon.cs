using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace CrabDesk.WinUI.Controls;

/// <summary>Displays a widget's SVG artwork in its original colors.</summary>
public sealed class WidgetColorIcon : UserControl
{
    public static readonly DependencyProperty AssetNameProperty = DependencyProperty.Register(
        nameof(AssetName), typeof(string), typeof(WidgetColorIcon),
        new PropertyMetadata(string.Empty, OnAssetNameChanged));

    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private SvgImageSource? _source;
    private XamlRoot? _observedRoot;

    public string AssetName
    {
        get => (string)GetValue(AssetNameProperty);
        set => SetValue(AssetNameProperty, value);
    }

    public WidgetColorIcon()
    {
        Content = _image;
        IsHitTestVisible = false;
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
        SizeChanged += (_, _) => UpdateRasterSize();
        Loaded += (_, _) =>
        {
            _observedRoot = XamlRoot;
            if (_observedRoot is not null) _observedRoot.Changed += OnRootChanged;
            UpdateRasterSize();
        };
        Unloaded += (_, _) =>
        {
            if (_observedRoot is not null) _observedRoot.Changed -= OnRootChanged;
            _observedRoot = null;
        };
    }

    private static void OnAssetNameChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var icon = (WidgetColorIcon)sender;
        var name = args.NewValue as string;
        icon._source = string.IsNullOrWhiteSpace(name) ? null : new SvgImageSource
        {
            UriSource = new Uri($"ms-appx:///Assets/WidgetIcons/{name}.svg"),
            RasterizePixelWidth = 128,
            RasterizePixelHeight = 128
        };
        icon._image.Source = icon._source;
        icon.UpdateRasterSize();
    }

    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateRasterSize();

    private void UpdateRasterSize()
    {
        if (_source is null || ActualWidth <= 0 || ActualHeight <= 0) return;
        var pixels = Math.Ceiling(Math.Max(ActualWidth, ActualHeight) * (XamlRoot?.RasterizationScale ?? 1));
        if (_source.RasterizePixelWidth == pixels && _source.RasterizePixelHeight == pixels) return;
        _source.RasterizePixelWidth = pixels;
        _source.RasterizePixelHeight = pixels;
    }
}
