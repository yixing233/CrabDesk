using CrabDesk.Core;
using CrabDesk.Runtime;
using CrabDesk.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CrabDesk.WinUI.Controls;

public sealed partial class WidgetPreview
{
    private readonly FileShelfPreviewIconCache<ImageSource> _fileShelfIcons = new(64);
    private CancellationTokenSource? _fileShelfIconLifetime;
    private DesktopWidget? _fileShelfIconWidget;
    private bool _fileShelfIconCatalog;
    private int _fileShelfIconGeneration;

    private void BeginFileShelfIconRender(DesktopWidget widget)
    {
        if (!ReferenceEquals(widget, _fileShelfIconWidget) || IsCatalogPreview != _fileShelfIconCatalog)
        {
            ResetFileShelfIcons();
            _fileShelfIconWidget = widget;
            _fileShelfIconCatalog = IsCatalogPreview;
        }
        _fileShelfIconGeneration++;
        if (widget.Kind == DesktopWidgetKind.FileShelf && !IsCatalogPreview && IsLoaded)
            _fileShelfIconLifetime ??= new CancellationTokenSource();
    }

    private void ResetFileShelfIcons()
    {
        _fileShelfIconGeneration++;
        _fileShelfIconLifetime?.Cancel();
        _fileShelfIconLifetime?.Dispose();
        _fileShelfIconLifetime = null;
        _fileShelfIconWidget = null;
        _fileShelfIcons.Clear();
    }

    private FrameworkElement CreateFileShelfIcon(DesktopWidget widget, WidgetFileShelfItem item)
    {
        // Catalog paths are illustrative, and never reach a Shell extension.
        if (IsCatalogPreview) return new WidgetColorIcon { AssetName = FileShelfPreviewIcons.GetSampleAssetName(item.Name) };

        var fallback = new WidgetColorIcon { AssetName = "file-generic" };
        var slot = new Grid { Opacity = item.IsAvailable is false ? .45 : 1 };
        slot.Children.Add(fallback);
        if (_fileShelfIconLifetime is null) return slot;

        string path;
        try
        {
            path = FileShelfPreviewIcons.ResolveShellPath(widget, item, false,
                (owner, file) => App.GetService<CrabDeskRuntime>().GetFileShelfPath(owner, file));
        }
        catch (IOException) { return slot; }
        catch (ArgumentException) { return slot; }
        if (string.IsNullOrWhiteSpace(path)) return slot;

        var token = _fileShelfIconLifetime.Token;
        Task<ImageSource?> load;
        if (item.IsAvailable is false)
        {
            if (!_fileShelfIcons.TryGet(path, out load)) return slot;
        }
        else load = _fileShelfIcons.GetOrLoad(path,
            (name, cancellation) => App.GetService<DesktopItemIconSourceFactory>().LoadAsync(name, cancellation), token);
        var image = new Image { Stretch = Stretch.Uniform };
        slot.Children.Add(image);
        if (load.IsCompletedSuccessfully)
        {
            image.Source = load.Result;
            if (image.Source is not null) fallback.Visibility = Visibility.Collapsed;
        }
        else _ = ApplyFileShelfIconAsync(widget, _fileShelfIconGeneration, load, image, fallback, token);
        return slot;
    }

    private async Task ApplyFileShelfIconAsync(DesktopWidget widget, int generation, Task<ImageSource?> load,
        Image image, FrameworkElement fallback, CancellationToken cancellationToken)
    {
        try
        {
            var source = await load.ConfigureAwait(false);
            if (source is null || cancellationToken.IsCancellationRequested) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (cancellationToken.IsCancellationRequested || !IsLoaded || IsCatalogPreview ||
                    !ReferenceEquals(Widget, widget) || generation != _fileShelfIconGeneration) return;
                image.Source = source;
                fallback.Visibility = Visibility.Collapsed;
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* Keep the file fallback if Shell or image decoding fails. */ }
    }
}

internal static class FileShelfPreviewIcons
{
    internal static string ResolveShellPath(DesktopWidget widget, WidgetFileShelfItem item, bool isCatalog,
        Func<DesktopWidget, WidgetFileShelfItem, string> resolveCopyPath) => isCatalog ? string.Empty :
        item.Mode == WidgetFileShelfMode.Copy ? resolveCopyPath(widget, item) : item.OriginalPath;

    internal static string GetSampleAssetName(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".pdf" => "file-pdf",
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".svg" => "file-image",
        ".zip" or ".7z" or ".rar" or ".tar" or ".gz" => "file-archive",
        _ => "file-generic"
    };
}

/// <summary>Reuses pending and completed work across preview refreshes without retaining an unbounded gallery.</summary>
internal sealed class FileShelfPreviewIconCache<T> where T : class
{
    private readonly int _capacity;
    private readonly Dictionary<string, Task<T?>> _loads = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _order = new();

    internal FileShelfPreviewIconCache(int capacity) => _capacity = capacity;

    internal bool TryGet(string path, out Task<T?> load) => _loads.TryGetValue(path, out load!);

    internal Task<T?> GetOrLoad(string path, Func<string, CancellationToken, Task<T?>> loader, CancellationToken cancellationToken)
    {
        if (_loads.TryGetValue(path, out var existing)) return existing;
        while (_loads.Count >= _capacity) _loads.Remove(_order.Dequeue());
        var load = loader(path, cancellationToken);
        _loads.Add(path, load);
        _order.Enqueue(path);
        return load;
    }

    internal void Clear()
    {
        _loads.Clear();
        _order.Clear();
    }
}
