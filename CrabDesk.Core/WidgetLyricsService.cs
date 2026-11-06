using System.Globalization;
using System.Text;

namespace CrabDesk.Core;

public sealed class WidgetLyricsService : IWidgetLyricsProvider, IDisposable
{
    private readonly IWidgetLyricsProvider[] providers;
    private readonly TimeSpan providerTimeout;
    private readonly int cacheCapacity;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<string, WidgetLyricsDocument> cache = new(StringComparer.Ordinal);
    private readonly LinkedList<string> cacheOrder = new();
    private readonly object gate = new();
    private bool disposed;

    public WidgetLyricsService(IEnumerable<IWidgetLyricsProvider>? providers = null,
        TimeSpan? providerTimeout = null, int cacheCapacity = 64)
    {
        this.providers = providers?.ToArray() ?? WidgetLyricsOnlineProviders.Create();
        this.providerTimeout = providerTimeout ?? TimeSpan.FromSeconds(8);
        if (this.providerTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(providerTimeout));
        this.cacheCapacity = Math.Clamp(cacheCapacity, 1, 256);
    }

    public async Task<WidgetLyricsDocument?> FetchAsync(ControlCenterMedia media, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(media.Title) || string.IsNullOrWhiteSpace(media.Artist)) return null;
        var key = string.Join('\n', NormalizeTrackText(media.Title), NormalizeTrackText(media.Artist),
            NormalizeTrackText(media.Album), Math.Round(media.Duration.TotalSeconds).ToString(CultureInfo.InvariantCulture));
        lock (gate)
        {
            if (cache.TryGetValue(key, out var cached))
            {
                cacheOrder.Remove(key);
                cacheOrder.AddLast(key);
                return cached;
            }
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        WidgetLyricsDocument? fallback = null;
        foreach (var provider in providers)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
            timeout.CancelAfter(providerTimeout);
            try
            {
                var document = WidgetLyricsParser.Normalize(
                    await provider.FetchAsync(media, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false),
                    media.Duration.TotalMilliseconds);
                cancellation.Token.ThrowIfCancellationRequested();
                if (document is null) continue;
                if (document.HasWordTiming || document.IsInstrumental)
                {
                    AddCache(key, document);
                    return document;
                }
                fallback ??= document;
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                // One unavailable catalog must not suppress the remaining sources.
            }
        }
        cancellation.Token.ThrowIfCancellationRequested();
        if (fallback is not null) AddCache(key, fallback);
        return fallback;
    }

    /// <summary>Requires a matching title and artist; known durations must agree within a small tolerance.</summary>
    public static bool IsTrackMatch(ControlCenterMedia media, string title, IEnumerable<string> artists,
        double? durationMilliseconds)
    {
        if (NormalizeTrackText(media.Title) != NormalizeTrackText(title)) return false;
        var expectedArtist = NormalizeTrackText(media.Artist);
        var candidates = artists.Where(artist => !string.IsNullOrWhiteSpace(artist)).ToArray();
        if (expectedArtist.Length == 0 || candidates.Length == 0) return false;
        var artistMatch = candidates.Any(artist => NormalizeTrackText(artist) == expectedArtist) ||
            NormalizeTrackText(string.Concat(candidates)) == expectedArtist;
        if (!artistMatch) return false;
        if (media.Duration.TotalMilliseconds > 0 && durationMilliseconds is > 0)
        {
            var tolerance = Math.Clamp(media.Duration.TotalMilliseconds * .02, 2000, 5000);
            if (!double.IsFinite(durationMilliseconds.Value) ||
                Math.Abs(media.Duration.TotalMilliseconds - durationMilliseconds.Value) > tolerance) return false;
        }
        return true;
    }

    internal static string NormalizeTrackText(string value) => string.Concat(value.Normalize(NormalizationForm.FormKC)
        .EnumerateRunes().Where(Rune.IsLetterOrDigit).Select(rune => Rune.ToUpperInvariant(rune).ToString()));

    private void AddCache(string key, WidgetLyricsDocument document)
    {
        lock (gate)
        {
            if (disposed) return;
            cache[key] = document;
            cacheOrder.Remove(key);
            cacheOrder.AddLast(key);
            while (cache.Count > cacheCapacity)
            {
                cache.Remove(cacheOrder.First!.Value);
                cacheOrder.RemoveFirst();
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            cache.Clear();
            cacheOrder.Clear();
        }
        lifetime.Cancel();
        lifetime.Dispose();
    }
}
