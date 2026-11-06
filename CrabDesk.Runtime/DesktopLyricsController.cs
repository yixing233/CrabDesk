using System;
using System.Threading;
using System.Threading.Tasks;
using CrabDesk.Core;

namespace CrabDesk.Runtime;

/// <summary>Polls playback independently from a cancellable lyrics lookup.</summary>
internal sealed class DesktopLyricsController(
    Func<CancellationToken, Task<ControlCenterMedia?>> readMedia,
    IWidgetLyricsProvider provider) : IDisposable
{
    private readonly object _gate = new();
    private WidgetLyricsSnapshot _snapshot = WidgetLyricsSnapshot.Empty;
    private CancellationTokenSource? _mediaRequest, _lyricsRequest;
    private long _generation;
    private bool _forceLyrics, _disposed;

    public WidgetLyricsSnapshot Snapshot { get { lock (_gate) return _snapshot; } }
    public event Action? Changed;

    /// <summary>
    /// Completes after reading playback. Lyrics publish separately through Changed,
    /// which may run on any thread. Overlapping polls are skipped while explicit
    /// lyrics retries are retained for the active read.
    /// </summary>
    public async Task RefreshAsync(bool forceLyrics = false)
    {
        CancellationTokenSource request;
        long generation;
        lock (_gate)
        {
            if (_disposed) return;
            _forceLyrics |= forceLyrics;
            if (_mediaRequest is not null) return;
            request = new CancellationTokenSource();
            _mediaRequest = request;
            generation = _generation;
        }

        try
        {
            var media = await readMedia(request.Token).ConfigureAwait(false);
            PublishMedia(media, "", request, generation);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception)
        {
            PublishMedia(null, "无法读取播放信息，请稍后重试。", request, generation);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_mediaRequest, request)) _mediaRequest = null;
            }
            request.Dispose();
        }
    }

    private void PublishMedia(ControlCenterMedia? media, string error,
        CancellationTokenSource mediaRequest, long generation)
    {
        CancellationTokenSource? previous = null, next = null;
        lock (_gate)
        {
            if (_disposed || generation != _generation || mediaRequest.IsCancellationRequested) return;
            var trackChanged = !SameTrack(_snapshot.Media, media);
            var refreshLyrics = trackChanged || _forceLyrics;
            _forceLyrics = false;
            if (refreshLyrics)
            {
                previous = _lyricsRequest;
                _lyricsRequest = null;
                if (media is not null && !string.IsNullOrWhiteSpace(media.Title))
                    next = _lyricsRequest = new CancellationTokenSource();
                _snapshot = new(media, trackChanged ? null : _snapshot.Document, next is not null, error);
            }
            else
            {
                _snapshot = _snapshot with { Media = media };
                if (media is null) _snapshot = new(null, null, false, error);
            }
            // The native read has completed. Release its slot before notifications,
            // so a subscriber can immediately request a retry without losing it.
            _mediaRequest = null;
        }

        Cancel(previous);
        if (next is not null)
        {
            var lyricsRequest = next;
            _ = Task.Run(() => FetchLyricsAsync(media!, lyricsRequest));
        }
        NotifyChanged();
    }

    private async Task FetchLyricsAsync(ControlCenterMedia media, CancellationTokenSource request)
    {
        WidgetLyricsDocument? document = null;
        var error = "";
        try
        {
            request.Token.ThrowIfCancellationRequested();
            document = await provider.FetchAsync(media, request.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception) { error = "歌词获取失败，请稍后重试。"; }

        try
        {
            lock (_gate)
            {
                if (_disposed || !ReferenceEquals(_lyricsRequest, request) || request.IsCancellationRequested) return;
                _lyricsRequest = null;
                // Keep the newest playback position, pause state and seek from polls.
                _snapshot = _snapshot with { Document = document, IsLoading = false, Error = error };
            }
            NotifyChanged();
        }
        finally { request.Dispose(); }
    }

    private static bool SameTrack(ControlCenterMedia? previous, ControlCenterMedia? current) =>
        previous is null || current is null
            ? previous is null && current is null
            : string.Equals(previous.TrackId, current.TrackId, StringComparison.Ordinal) &&
              string.Equals(WidgetLyricsSettings.TrackKey(previous), WidgetLyricsSettings.TrackKey(current), StringComparison.Ordinal);

    /// <summary>Clears hidden widgets' state and cancels outstanding work.</summary>
    public void Reset()
    {
        CancellationTokenSource? media, lyrics;
        lock (_gate)
        {
            if (_disposed) return;
            _generation++;
            _forceLyrics = false;
            media = _mediaRequest;
            lyrics = _lyricsRequest;
            _lyricsRequest = null;
            _snapshot = WidgetLyricsSnapshot.Empty;
        }
        Cancel(media);
        Cancel(lyrics);
        NotifyChanged();
    }

    private void NotifyChanged()
    {
        // Share the disposal gate so no subscriber runs after Dispose returns.
        lock (_gate)
        {
            if (!_disposed) Changed?.Invoke();
        }
    }

    private static void Cancel(CancellationTokenSource? request)
    {
        if (request is null) return;
        try { request.Cancel(); }
        catch (ObjectDisposedException) { } // Its async operation already finished.
    }

    public void Dispose()
    {
        CancellationTokenSource? media, lyrics;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _generation++;
            media = _mediaRequest;
            lyrics = _lyricsRequest;
            _lyricsRequest = null;
            Changed = null;
        }
        Cancel(media);
        Cancel(lyrics);
    }
}
