using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using CrabDesk.Core;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace CrabDesk.Native;

public sealed partial class WidgetControlCenterMonitor
{
    private GlobalSystemMediaTransportControlsSessionManager? _mediaManager;
    private GlobalSystemMediaTransportControlsSession? _mediaSession;
    private long _mediaSessionVersion;
    private long _mediaReadVersion = -1;
    private MediaIdentity? _mediaIdentity;
    private string _mediaTrackId = "";
    private byte[]? _mediaArtwork;
    private sealed record MediaIdentity(string Application, string Title, string Artist, string Album, TimeSpan Start, TimeSpan Duration);

    /// <summary>Reads playback without querying audio, radios or display settings.</summary>
    public Task<ControlCenterMedia?> ReadCurrentMediaAsync(CancellationToken token = default) => ReadMediaAsync(token);

    private async Task<ControlCenterMedia?> ReadMediaAsync(CancellationToken token)
    {
        try
        {
            var manager = _mediaManager;
            if (manager is null)
            {
                var requested = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(token).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_disposed) return null;
                    if (_mediaManager is null)
                    {
                        _mediaManager = requested;
                        _mediaManager.CurrentSessionChanged += OnCurrentMediaSessionChanged;
                    }
                    manager = _mediaManager;
                }
            }
            var sessionVersion = Volatile.Read(ref _mediaSessionVersion);
            var session = manager.GetCurrentSession();
            if (session is null) { ClearMedia(); return null; }
            var info = session.GetPlaybackInfo();
            var properties = await session.TryGetMediaPropertiesAsync().AsTask(token).ConfigureAwait(false);
            var timeline = session.GetTimelineProperties();
            var duration = timeline.EndTime > timeline.StartTime ? timeline.EndTime - timeline.StartTime : TimeSpan.Zero;
            var identity = new MediaIdentity(session.SourceAppUserModelId, properties?.Title ?? "", properties?.Artist ?? "",
                properties?.AlbumTitle ?? "", timeline.StartTime, duration);
            // WinRT can return a new wrapper on every read. Session-change
            // notifications identify actual player changes; metadata identifies tracks.
            if (sessionVersion != _mediaReadVersion || identity != _mediaIdentity)
            {
                _mediaTrackId = Guid.NewGuid().ToString("N");
                _mediaArtwork = null;
            }
            _mediaSession = session;
            _mediaReadVersion = sessionVersion;
            _mediaIdentity = identity;
            if (_mediaArtwork is null && properties?.Thumbnail is { } thumbnail)
                _mediaArtwork = await ReadArtworkAsync(thumbnail, token).ConfigureAwait(false);
            if (sessionVersion != Volatile.Read(ref _mediaSessionVersion)) { ClearMedia(); return null; }
            var now = DateTimeOffset.Now;
            var captured = timeline.LastUpdatedTime;
            var minimum = TimeSpan.FromTicks(Math.Clamp((timeline.MinSeekTime - timeline.StartTime).Ticks, 0, duration.Ticks));
            var maximum = timeline.MaxSeekTime > timeline.MinSeekTime
                ? TimeSpan.FromTicks(Math.Clamp((timeline.MaxSeekTime - timeline.StartTime).Ticks, minimum.Ticks, duration.Ticks)) : duration;
            return new ControlCenterMedia(identity.Title, identity.Artist, identity.Album,
                info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                info?.Controls.IsPlayPauseToggleEnabled ?? false, info?.Controls.IsNextEnabled ?? false,
                info?.Controls.IsPreviousEnabled ?? false)
            {
                TrackId = _mediaTrackId, SourceAppId = identity.Application, Artwork = _mediaArtwork,
                Duration = duration,
                Position = TimeSpan.FromTicks(Math.Clamp((timeline.Position - timeline.StartTime).Ticks, 0, duration.Ticks)),
                CapturedAt = captured == default || captured > now ? now : captured,
                PlaybackRate = info?.PlaybackRate ?? 1,
                CanSeek = info?.Controls.IsPlaybackPositionEnabled is true && maximum > minimum,
                SeekMinimum = minimum, SeekMaximum = maximum
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { ClearMedia(); return null; }
    }

    public async Task<bool> SeekMediaAsync(ControlCenterMedia media, TimeSpan position, CancellationToken token = default)
    {
        if (!media.CanSeek || media.Duration <= TimeSpan.Zero || string.IsNullOrEmpty(media.TrackId)) return false;
        try
        {
            // Recheck both the track and native session after a drag. A newly
            // active player must not receive the previous player's seek.
            var current = await ReadMediaAsync(token).ConfigureAwait(false);
            if (current is not { CanSeek: true } || current.TrackId != media.TrackId || _mediaSession is null || _mediaIdentity is null ||
                _mediaReadVersion != Volatile.Read(ref _mediaSessionVersion)) return false;
            var target = _mediaIdentity.Start + ControlCenterPresentation.ClampMediaPosition(current, position);
            return await _mediaSession.TryChangePlaybackPositionAsync(target.Ticks).AsTask(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return false; }
    }

    private void ClearMedia()
    {
        _mediaSession = null;
        _mediaIdentity = null;
        _mediaTrackId = "";
        _mediaArtwork = null;
    }

    private void OnCurrentMediaSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args) =>
        Interlocked.Increment(ref _mediaSessionVersion);

    private static async Task<byte[]?> ReadArtworkAsync(IRandomAccessStreamReference reference, CancellationToken token)
    {
        try
        {
            using var stream = await reference.OpenReadAsync().AsTask(token).ConfigureAwait(false);
            if (stream.Size == 0 || stream.Size > 4 * 1024 * 1024) return null;
            using var input = stream.AsStreamForRead();
            using var memory = new MemoryStream();
            await input.CopyToAsync(memory, token).ConfigureAwait(false);
            memory.Position = 0;
            using var original = Image.FromStream(memory);
            var ratio = Math.Min(1d, 128d / Math.Max(original.Width, original.Height));
            using var small = new Bitmap(Math.Max(1, (int)(original.Width * ratio)), Math.Max(1, (int)(original.Height * ratio)));
            using (var graphics = Graphics.FromImage(small))
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(original, new Rectangle(0, 0, small.Width, small.Height));
            }
            using var output = new MemoryStream();
            small.Save(output, ImageFormat.Png);
            return output.ToArray();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return null; }
    }
}
