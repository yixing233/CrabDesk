using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;
using WinRT.Interop;

namespace CrabDesk.WinUI.Windows;

public sealed partial class WeatherDetailsWindow
{
    private readonly Storyboard _entrance = new();
    private readonly TranslateTransform _entranceOffset = new();
    private bool _animateEntrance;

    private void InitializeEntrance()
    {
        _animateEntrance = _runtime.State.Settings.Appearance.AnimationEnabled && new UISettings().AnimationsEnabled;
        _root.Opacity = _animateEntrance ? 0 : 1;
        _entranceOffset.Y = _animateEntrance ? 8 : 0;
        _root.RenderTransform = _entranceOffset;
        // Keep the native acrylic surface hidden until XAML has laid out and
        // submitted its first frame at the final size.
        var cloak = 1;
        _ = DwmSetWindowAttribute(WindowNative.GetWindowHandle(this), 13, ref cloak, sizeof(int));
        _root.Loaded += PrepareEntrance;
        _entrance.Completed += (_, _) =>
        {
            _root.Opacity = 1;
            _entranceOffset.Y = 0;
            _entrance.Stop();
        };
        Closed += (_, _) =>
        {
            _root.Loaded -= PrepareEntrance;
            CompositionTarget.Rendered -= ShowFirstFrame;
            _entrance.Stop();
        };
    }

    private void PrepareEntrance(object sender, RoutedEventArgs args)
    {
        _root.Loaded -= PrepareEntrance;
        _root.UpdateLayout();
        if (_locationsOnly) ResizeLocations();
        UpdateWindowShape();
        CompositionTarget.Rendered += ShowFirstFrame;
    }

    private void ShowFirstFrame(object? sender, RenderedEventArgs args)
    {
        CompositionTarget.Rendered -= ShowFirstFrame;
        if (_locationsClosed) return;
        if (_animateEntrance)
        {
            var fade = new DoubleAnimation { From = 0, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(180)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            Storyboard.SetTarget(fade, _root);
            Storyboard.SetTargetProperty(fade, "Opacity");
            var slide = new DoubleAnimation { From = 8, To = 0, Duration = new Duration(TimeSpan.FromMilliseconds(220)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            Storyboard.SetTarget(slide, _entranceOffset);
            Storyboard.SetTargetProperty(slide, "Y");
            _entrance.Children.Add(fade);
            _entrance.Children.Add(slide);
            _entrance.Begin();
        }
        var cloak = 0;
        _ = DwmSetWindowAttribute(WindowNative.GetWindowHandle(this), 13, ref cloak, sizeof(int));
    }
}
