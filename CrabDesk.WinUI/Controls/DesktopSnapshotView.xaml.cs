using CrabDesk.Core;
using CrabDesk.WinUI.Converters;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.IO;

namespace CrabDesk.WinUI.Controls;

public sealed partial class DesktopSnapshotView : UserControl
{
    public static readonly DependencyProperty SnapshotProperty = DependencyProperty.Register(
        nameof(Snapshot),
        typeof(LayoutBackupSnapshot),
        typeof(DesktopSnapshotView),
        new PropertyMetadata(null, OnSnapshotChanged));

    public DesktopSnapshotView()
    {
        InitializeComponent();
    }

    public LayoutBackupSnapshot? Snapshot
    {
        get => (LayoutBackupSnapshot?)GetValue(SnapshotProperty);
        set => SetValue(SnapshotProperty, value);
    }

    private static void OnSnapshotChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is DesktopSnapshotView view)
        {
            view.RenderSnapshot();
        }
    }

    private void SnapshotCanvas_OnSizeChanged(object sender, SizeChangedEventArgs e) => RenderSnapshot();

    private void RenderSnapshot()
    {
        SnapshotCanvas.Children.Clear();
        SnapshotWallpaper.Source = null;
        if (Snapshot is not { } snapshot ||
            SnapshotCanvas.ActualWidth <= 0 ||
            SnapshotCanvas.ActualHeight <= 0 ||
            snapshot.DesktopBounds.Width <= 0 ||
            snapshot.DesktopBounds.Height <= 0)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(snapshot.WallpaperPath) &&
            File.Exists(snapshot.WallpaperPath))
        {
            try
            {
                SnapshotWallpaper.Source = new BitmapImage(new Uri(snapshot.WallpaperPath, UriKind.Absolute));
            }
            catch
            {
                SnapshotWallpaper.Source = null;
            }
        }

        const double inset = 2;
        var availableWidth = Math.Max(0, SnapshotCanvas.ActualWidth - inset * 2);
        var availableHeight = Math.Max(0, SnapshotCanvas.ActualHeight - inset * 2 - 3);
        var scale = Math.Min(
            availableWidth / snapshot.DesktopBounds.Width,
            availableHeight / snapshot.DesktopBounds.Height);
        var renderedWidth = snapshot.DesktopBounds.Width * scale;
        var renderedHeight = snapshot.DesktopBounds.Height * scale;
        var offsetX = inset + (availableWidth - renderedWidth) / 2;
        var offsetY = inset + (availableHeight - renderedHeight) / 2;

        foreach (var position in snapshot.IconPositions ?? [])
        {
            var markerSize = Math.Clamp(5 * scale, 1.5, 5);
            var marker = new Border
            {
                Width = markerSize,
                Height = markerSize,
                Background = new SolidColorBrush(ColorHelper.FromArgb(210, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(220, 32, 38, 44)),
                BorderThickness = new Thickness(0.5),
                CornerRadius = new CornerRadius(markerSize / 2)
            };
            Canvas.SetLeft(marker, offsetX + (position.X - snapshot.DesktopBounds.X) * scale);
            Canvas.SetTop(marker, offsetY + (position.Y - snapshot.DesktopBounds.Y) * scale);
            SnapshotCanvas.Children.Add(marker);
        }

        foreach (var box in snapshot.Boxes)
        {
            var width = Math.Max(5, box.Bounds.Width * scale);
            var height = Math.Max(4, (box.IsCollapsed ? 42 : box.Bounds.Height) * scale);
            var boxView = new Border
            {
                Width = width,
                Height = height,
                Background = Brush(box.Background, 176),
                BorderBrush = Brush(box.Accent, 255),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(Math.Min(2.5, Math.Min(width, height) / 4))
            };
            if (width >= 28 && height >= 13)
            {
                boxView.Child = new TextBlock
                {
                    Text = box.Title,
                    FontSize = 6,
                    Margin = new Thickness(3, 1, 2, 1),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = new SolidColorBrush(Colors.White)
                };
            }

            Canvas.SetLeft(boxView, offsetX + (box.Bounds.X - snapshot.DesktopBounds.X) * scale);
            Canvas.SetTop(boxView, offsetY + (box.Bounds.Y - snapshot.DesktopBounds.Y) * scale);
            SnapshotCanvas.Children.Add(boxView);
        }
    }

    private static SolidColorBrush Brush(string value, byte maximumAlpha)
    {
        if (!HexColor.TryParse(value, out var color))
        {
            color = Colors.SlateGray;
        }
        return new SolidColorBrush(ColorHelper.FromArgb(
            Math.Min(color.A, maximumAlpha),
            color.R,
            color.G,
            color.B));
    }
}
