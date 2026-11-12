using System.Diagnostics;
using Wpf = System.Windows;
using WpfControls = System.Windows.Controls;
using Media = System.Windows.Media;
using Animation = System.Windows.Media.Animation;

namespace CrabDesk.Runtime;

internal enum PopupSkeletonLayout { Connections, NetworkAdapters, Display, Mixer, Microphone }

internal sealed class PopupLoadingPresenter : WpfControls.Grid, IDisposable
{
    private readonly WpfControls.Grid _content;
    private readonly WpfControls.Grid _skeleton;
    private readonly Media.TranslateTransform _offset = new();
    private readonly bool _animate;
    private long _loadedAt;
    private bool _disposed;

    internal bool IsLoading { get; private set; } = true;
    internal bool IsTransitioning { get; private set; }
    internal double ContentOpacity => _content.Opacity;
    internal bool SkeletonVisible => _skeleton.Visibility == Wpf.Visibility.Visible;

    internal PopupLoadingPresenter(Wpf.FrameworkElement content, bool dark, bool animationsEnabled, PopupSkeletonLayout layout)
    {
        _animate = animationsEnabled && Wpf.SystemParameters.ClientAreaAnimation;
        _content = new WpfControls.Grid { Visibility = Wpf.Visibility.Hidden, RenderTransform = _offset };
        _content.Children.Add(content);
        _skeleton = new WpfControls.Grid { ClipToBounds = true, IsHitTestVisible = false };
        _skeleton.Children.Add(BuildSkeleton(dark, layout));
        Children.Add(_content);
        Children.Add(_skeleton);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    internal void ShowContent()
    {
        if (_disposed || !IsLoading) return;
        IsLoading = false;
        _content.Visibility = Wpf.Visibility.Visible;
        // A cached or fast result should be part of the window's first frame,
        // without briefly flashing a placeholder or waiting for a minimum delay.
        if (!_animate || !IsLoaded || _loadedAt == 0 || Stopwatch.GetElapsedTime(_loadedAt).TotalMilliseconds < 120)
        {
            FinishTransition();
            return;
        }
        IsTransitioning = true;
        _content.IsHitTestVisible = false;
        _content.Opacity = 0;
        // Build newly populated row templates before starting the animation clock.
        _content.UpdateLayout();
        if (_disposed || !IsLoaded) { FinishTransition(); return; }
        var skeletonOpacity = _skeleton.Opacity;
        _skeleton.BeginAnimation(OpacityProperty, null);
        var fadeOut = new Animation.DoubleAnimation(skeletonOpacity, 0, TimeSpan.FromMilliseconds(160));
        _skeleton.BeginAnimation(OpacityProperty, fadeOut);
        var fadeIn = new Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240))
        {
            EasingFunction = new Animation.CubicEase { EasingMode = Animation.EasingMode.EaseOut }
        };
        fadeIn.Completed += (_, _) => { if (!_disposed) FinishTransition(); };
        _content.BeginAnimation(OpacityProperty, fadeIn);
        _offset.BeginAnimation(Media.TranslateTransform.YProperty,
            new Animation.DoubleAnimation(4, 0, TimeSpan.FromMilliseconds(240))
            {
                EasingFunction = new Animation.CubicEase { EasingMode = Animation.EasingMode.EaseOut }
            });
    }

    private void OnLoaded(object sender, Wpf.RoutedEventArgs args)
    {
        _loadedAt = Stopwatch.GetTimestamp();
        if (IsLoading && _animate)
            _skeleton.BeginAnimation(OpacityProperty, new Animation.DoubleAnimation(.55, .9, TimeSpan.FromMilliseconds(900))
            {
                AutoReverse = true, RepeatBehavior = Animation.RepeatBehavior.Forever
            });
    }

    private void OnUnloaded(object sender, Wpf.RoutedEventArgs args)
    {
        if (!IsLoading) FinishTransition();
        else _skeleton.BeginAnimation(OpacityProperty, null);
    }

    private void FinishTransition()
    {
        _skeleton.BeginAnimation(OpacityProperty, null);
        _skeleton.Visibility = Wpf.Visibility.Collapsed;
        _content.BeginAnimation(OpacityProperty, null);
        _content.Opacity = 1;
        _content.IsHitTestVisible = true;
        _offset.BeginAnimation(Media.TranslateTransform.YProperty, null);
        _offset.Y = 0;
        IsTransitioning = false;
    }

    private static WpfControls.StackPanel BuildSkeleton(bool dark, PopupSkeletonLayout layout)
    {
        var ink = new Media.SolidColorBrush(dark ? Media.Color.FromArgb(36, 255, 255, 255) : Media.Color.FromArgb(24, 31, 35, 41));
        var fill = new Media.SolidColorBrush(dark ? Media.Color.FromArgb(9, 255, 255, 255) : Media.Color.FromArgb(8, 31, 35, 41));
        ink.Freeze();
        fill.Freeze();
        var stack = new WpfControls.StackPanel { VerticalAlignment = Wpf.VerticalAlignment.Top };
        WpfControls.Border Bar(double width, double height = 10) => new()
        {
            Width = width, Height = height, MaxWidth = width, Background = ink,
            CornerRadius = new Wpf.CornerRadius(Math.Min(4, height / 2)),
            HorizontalAlignment = Wpf.HorizontalAlignment.Left, VerticalAlignment = Wpf.VerticalAlignment.Center
        };
        if (layout == PopupSkeletonLayout.Microphone)
        {
            var device = Bar(180, 16);
            device.Margin = new Wpf.Thickness(8, 7, 0, 18);
            stack.Children.Add(device);
            var label = Bar(72);
            label.Margin = new Wpf.Thickness(0, 0, 0, 10);
            stack.Children.Add(label);
            var slider = Bar(246, 4);
            slider.Margin = new Wpf.Thickness(0, 0, 0, 18);
            stack.Children.Add(slider);
            stack.Children.Add(Bar(62, 8));
            var meter = Bar(270, 4);
            meter.Margin = new Wpf.Thickness(0, 8, 0, 0);
            stack.Children.Add(meter);
            return stack;
        }
        var summary = new WpfControls.Grid
        {
            Height = layout == PopupSkeletonLayout.Connections ? 34 : 22,
            Margin = new Wpf.Thickness(0, 0, 0, layout == PopupSkeletonLayout.Connections ? 12 : 8)
        };
        summary.Children.Add(Bar(layout == PopupSkeletonLayout.Mixer ? 152 : 94, 11));
        if (layout == PopupSkeletonLayout.Connections)
        {
            var button = Bar(40, 20);
            button.CornerRadius = new Wpf.CornerRadius(10);
            button.HorizontalAlignment = Wpf.HorizontalAlignment.Right;
            button.Margin = new Wpf.Thickness(0, 0, 4, 0);
            summary.Children.Add(button);
        }
        stack.Children.Add(summary);
        var compact = layout is PopupSkeletonLayout.Display or PopupSkeletonLayout.Mixer;
        for (var index = 0; index < 4; index++)
        {
            var row = new WpfControls.Grid { Height = compact ? 42 : 44 };
            row.ColumnDefinitions.Add(new() { Width = new Wpf.GridLength(32) });
            row.ColumnDefinitions.Add(new() { Width = new Wpf.GridLength(1, Wpf.GridUnitType.Star) });
            row.ColumnDefinitions.Add(new() { Width = new Wpf.GridLength(48) });
            row.Children.Add(Bar(28, 28));
            var labels = new WpfControls.StackPanel { Margin = new Wpf.Thickness(10, 0, 8, 0), VerticalAlignment = Wpf.VerticalAlignment.Center };
            labels.Children.Add(Bar(index % 2 == 0 ? 130 : 104, 11));
            var detail = Bar(index % 2 == 0 ? 96 : 118, compact ? 5 : 8);
            detail.Margin = new Wpf.Thickness(0, 7, 0, 0);
            labels.Children.Add(detail);
            WpfControls.Grid.SetColumn(labels, 1);
            row.Children.Add(labels);
            var action = Bar(30, 10);
            action.HorizontalAlignment = Wpf.HorizontalAlignment.Right;
            WpfControls.Grid.SetColumn(action, 2);
            row.Children.Add(action);
            stack.Children.Add(new WpfControls.Border
            {
                Child = row, Background = compact ? Media.Brushes.Transparent : fill,
                CornerRadius = new Wpf.CornerRadius(8), Padding = new Wpf.Thickness(8, compact ? 0 : 7, 8, compact ? 0 : 7),
                Margin = new Wpf.Thickness(0, 0, 0, compact ? 4 : 8)
            });
        }
        return stack;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        FinishTransition();
    }
}
