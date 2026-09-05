using System.Collections.Specialized;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices.ComTypes;
using CrabDesk.Core;
using CrabDesk.Native;
using Forms = System.Windows.Forms;
using FormsIntegration = System.Windows.Forms.Integration;
using Wpf = System.Windows;
using WpfControls = System.Windows.Controls;
using WpfInput = System.Windows.Input;
using WpfMedia = System.Windows.Media;

namespace CrabDesk.Runtime;

internal sealed partial class DesktopBoxForm : Forms.Form
{

    private Bitmap? GetIconBitmap(DesktopItemRef item, float iconSize)
    {
        var key = CreateIconBitmapKey(item, iconSize);
        if (_iconCache.TryGetValue(key, out var bitmap))
        {
            return bitmap;
        }
        // A zoom notch changes the requested pixel size. Keep rendering from
        // the nearest cached size while the new size loads asynchronously;
        // DrawImage scales the bitmap to the current icon bounds.
        var nearest = _iconCache
            .Where(pair =>
                pair.Key.ParsingName == key.ParsingName &&
                pair.Key.ModifiedTicks == key.ModifiedTicks &&
                pair.Key.Length == key.Length)
            .OrderBy(pair => Math.Abs(pair.Key.PixelSize - key.PixelSize))
            .Select(pair => pair.Value)
            .FirstOrDefault();
        if (nearest is not null)
        {
            QueueIconBitmapLoad(key);
            return nearest;
        }
        if (_iconLoadRetries.TryGetValue(key, out var retry) &&
            DateTimeOffset.UtcNow < retry.RetryAfter)
        {
            return null;
        }
        QueueIconBitmapLoad(key);
        return null;
    }

    private void QueueIconBitmapLoad(IconBitmapKey key)
    {
        if (_iconCache.ContainsKey(key) ||
            (_iconLoadRetries.TryGetValue(key, out var retry) &&
             DateTimeOffset.UtcNow < retry.RetryAfter) ||
            !_pendingIconLoads.Add(key))
        {
            return;
        }

        _ = LoadIconBitmapAsync(key, _iconCacheVersion);
    }

    private void QueueBoxIconPreload()
    {
        if (_boxIconPreloadPending || _resourcesDisposed || IsDisposed || !IsHandleCreated)
        {
            return;
        }

        _boxIconPreloadPending = true;
        try
        {
            BeginInvoke((Action)PreloadBoxIcons);
        }
        catch (InvalidOperationException)
        {
            _boxIconPreloadPending = false;
        }
    }

    private void PreloadBoxIcons()
    {
        _boxIconPreloadPending = false;
        if (_resourcesDisposed || IsDisposed)
        {
            return;
        }

        var requiredKeys = DesktopBoxes
            .SelectMany(GetRequiredBoxIconBitmapKeys)
            .Distinct()
            .ToArray();
        _boxIconPreloadKeys.Clear();
        _boxIconPreloadKeys.UnionWith(requiredKeys.Where(key => !_iconCache.ContainsKey(key)));
        _boxIconPreloadStartedAt = _boxIconPreloadKeys.Count > 0
            ? DateTimeOffset.UtcNow
            : null;
        DiagnosticLog.Info(
            $"Box icon preload monitor={_monitor.Id} required={requiredKeys.Length} " +
            $"missing={_boxIconPreloadKeys.Count}");
        foreach (var key in requiredKeys)
        {
            QueueIconBitmapLoad(key);
        }
    }

    private IconBitmapKey[] GetRequiredBoxIconBitmapKeys(DesktopBox box)
    {
        var expandedGeometry = CreateBoxGeometry(
            box,
            (float)box.Bounds.Height,
            isCollapsed: false);
        return GetRenderedItemsForBox(expandedGeometry, expandedGeometry.Bounds)
            .Select(item => CreateIconBitmapKey(item.Item, (float)box.Appearance.IconSize))
            .Distinct()
            .ToArray();
    }

    private bool AreRequiredBoxIconsLoaded(DesktopBox box)
    {
        var requiredKeys = GetRequiredBoxIconBitmapKeys(box);
        var loadedIconCount = requiredKeys.Count(key =>
            _iconCache.TryGetValue(key, out var bitmap) && bitmap is not null);
        return ShouldCreateHeightAnimationVisualCache(requiredKeys.Length, loadedIconCount);
    }

    private async Task LoadIconBitmapAsync(IconBitmapKey key, int cacheVersion)
    {
        Bitmap? bitmap = null;
        var token = _iconLoadCancellation.Token;
        try
        {
            await _iconLoadGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                bitmap = await Task.Run(() =>
                {
                    var source = _runtime.IconProvider.GetIcon(key.ParsingName, key.PixelSize);
                    return source is null ? null : new Bitmap(source);
                }, token).ConfigureAwait(false);
                if (bitmap is null && !_iconLoadRetries.ContainsKey(key))
                {
                    DiagnosticLog.Info(
                        $"Icon load returned no image parsingName={key.ParsingName} pixelSize={key.PixelSize}");
                }
            }
            finally
            {
                _iconLoadGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            bitmap?.Dispose();
            return;
        }
        catch
        {
            bitmap?.Dispose();
            bitmap = null;
        }

        if (token.IsCancellationRequested || IsDisposed || !IsHandleCreated)
        {
            bitmap?.Dispose();
            return;
        }

        try
        {
            BeginInvoke((Action)(() =>
            {
                _pendingIconLoads.Remove(key);
                if (IsDisposed || cacheVersion != _iconCacheVersion)
                {
                    bitmap?.Dispose();
                    return;
                }
                if (_iconCache.ContainsKey(key))
                {
                    bitmap?.Dispose();
                    return;
                }
                if (bitmap is null)
                {
                    ScheduleIconLoadRetry(key);
                    return;
                }
                _iconLoadRetries.Remove(key);
                _iconCache[key] = bitmap;
                if (_boxIconPreloadKeys.Remove(key) && _boxIconPreloadKeys.Count == 0)
                {
                    var elapsed = _boxIconPreloadStartedAt is { } startedAt
                        ? (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds
                        : 0;
                    _boxIconPreloadStartedAt = null;
                    DiagnosticLog.Info(
                        $"Box icon preload ready monitor={_monitor.Id} elapsedMs={elapsed:0}");
                }
                TryCompleteHeightAnimationCacheRequest(key);
                InvalidateIcon(key);
            }));
        }
        catch (InvalidOperationException)
        {
            bitmap?.Dispose();
        }
    }

    private void ScheduleIconLoadRetry(IconBitmapKey key)
    {
        var attempt = _iconLoadRetries.GetValueOrDefault(key).Attempt + 1;
        var delay = TimeSpan.FromMilliseconds(Math.Min(30000, 500 * Math.Pow(2, Math.Min(attempt - 1, 6))));
        _iconLoadRetries[key] = new IconLoadRetry(attempt, DateTimeOffset.UtcNow + delay);
        _ = RetryIconLoadAsync(key, delay, _iconCacheVersion);
    }

    private async Task RetryIconLoadAsync(IconBitmapKey key, TimeSpan delay, int cacheVersion)
    {
        try
        {
            await Task.Delay(delay, _iconLoadCancellation.Token).ConfigureAwait(false);
            if (_iconLoadCancellation.IsCancellationRequested || IsDisposed || !IsHandleCreated)
            {
                return;
            }
            BeginInvoke((Action)(() =>
            {
                if (!IsDisposed && cacheVersion == _iconCacheVersion && !_iconCache.ContainsKey(key))
                {
                    if (_iconLoadRetries.TryGetValue(key, out var retry))
                    {
                        _iconLoadRetries[key] = retry with { RetryAfter = DateTimeOffset.MinValue };
                    }
                    InvalidateIcon(key);
                }
            }));
        }
        catch (OperationCanceledException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void PruneIconCache()
    {
        var activeKeys = DesktopBoxes
            .SelectMany(box => GetCachedItemsForBox(box.Id)
                .SelectMany(item => CreateNeighborIconBitmapKeys(item, (float)box.Appearance.IconSize)))
            .ToHashSet();
        foreach (var key in _iconCache.Keys.Where(key => !activeKeys.Contains(key)).ToArray())
        {
            _iconCache[key]?.Dispose();
            _iconCache.Remove(key);
        }
        foreach (var key in _iconLoadRetries.Keys.Where(key => !activeKeys.Contains(key)).ToArray())
        {
            _iconLoadRetries.Remove(key);
        }
    }

    private IEnumerable<IconBitmapKey> CreateNeighborIconBitmapKeys(
        DesktopItemRef item,
        float iconSize)
    {
        var center = QuantizeIconPixelSize((int)Math.Round(iconSize * _scale));
        foreach (var offset in new[] { -32, -16, 0, 16, 32 })
        {
            yield return new IconBitmapKey(
                item.ParsingName,
                Math.Clamp(center + offset, 16, 256),
                item.ModifiedAt?.UtcDateTime.Ticks ?? 0,
                0);
        }
    }

    private static int QuantizeIconPixelSize(int pixelSize)
    {
        pixelSize = Math.Clamp(pixelSize, 16, 256);
        return (int)(16 * Math.Round(pixelSize / 16.0, MidpointRounding.AwayFromZero));
    }

    private IconBitmapKey CreateIconBitmapKey(DesktopItemRef item, float iconSize)
    {
        return new IconBitmapKey(
            item.ParsingName,
            QuantizeIconPixelSize((int)Math.Round(iconSize * _scale)),
            item.ModifiedAt?.UtcDateTime.Ticks ?? 0,
            0);
    }

    private void InvalidateIcon(IconBitmapKey key)
    {
        var dirtyBounds = DesktopBoxes
            .Where(box =>
                ShouldPresentLoadedBoxIcon(
                    IsEffectivelyCollapsed(box),
                    _heightAnimations.ContainsKey(box.Id)) &&
                GetRequiredBoxIconBitmapKeys(box).Contains(key))
            .Select(box => new RectangleF(
                (float)box.Bounds.X,
                (float)box.Bounds.Y,
                (float)box.Bounds.Width,
                (float)box.Bounds.Height))
            .Aggregate((RectangleF?)null, (current, candidate) => current is { } existing
                ? RectangleF.Union(existing, candidate)
                : candidate);
        if (dirtyBounds is null)
        {
            return;
        }

        if (_isCompositedByIconSurface && _iconLayerPartialRenderRequest is not null)
        {
            _iconLayerPartialRenderRequest(dirtyBounds.Value);
            return;
        }

        RequestVisualLayerRender();
    }

    private void TryCompleteHeightAnimationCacheRequest(IconBitmapKey loadedKey)
    {
        foreach (var boxId in _heightAnimationCacheRequestBoxIds.ToArray())
        {
            var box = DesktopBoxes.FirstOrDefault(candidate => candidate.Id == boxId);
            if (box is null)
            {
                _heightAnimationCacheRequestBoxIds.Remove(boxId);
                continue;
            }
            if (!GetRequiredBoxIconBitmapKeys(box).Contains(loadedKey) ||
                !AreRequiredBoxIconsLoaded(box))
            {
                continue;
            }

            PrepareHeightAnimationVisualCache(box);
            if (_heightAnimationVisualCaches.ContainsKey(boxId))
            {
                _prewarmedHeightAnimationCacheBoxId = boxId;
            }
        }
    }

}

