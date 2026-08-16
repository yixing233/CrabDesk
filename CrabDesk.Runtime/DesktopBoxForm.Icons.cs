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
            if (_pendingIconLoads.Add(key))
            {
                _ = LoadIconBitmapAsync(key, _iconCacheVersion);
            }
            return nearest;
        }
        if (_iconLoadRetries.TryGetValue(key, out var retry) &&
            DateTimeOffset.UtcNow < retry.RetryAfter)
        {
            return null;
        }
        if (_pendingIconLoads.Add(key))
        {
            _ = LoadIconBitmapAsync(key, _iconCacheVersion);
        }
        return null;
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
                var source = _runtime.IconProvider.GetIcon(key.ParsingName, key.PixelSize);
                if (source is not null)
                {
                    bitmap = new Bitmap(source);
                }
                else if (!_iconLoadRetries.ContainsKey(key))
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
        // This is a full-surface layered window, so a completed icon load
        // always needs the same complete presentation regardless of how many
        // items use the bitmap.  Do not enumerate _items here: presenting a
        // layer rebuilds that list synchronously, which used to invalidate a
        // Where() enumerator between its first and second matching item.
        RequestVisualLayerRender();
    }

}

