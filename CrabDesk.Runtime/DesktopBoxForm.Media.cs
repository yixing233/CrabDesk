using System.Drawing;
using System.Drawing.Drawing2D;
using CrabDesk.Core;

namespace CrabDesk.Runtime;

internal sealed partial class DesktopBoxForm
{
    private byte[]? _controlArtworkData;
    private Bitmap? _controlArtwork;
    private ControlCenterMedia? _controlSeekMedia;
    private Guid? _controlSeekWidget;
    private TimeSpan _controlSeekPosition;

    private static RectangleF MediaRectangle(LayoutRect rect) => new((float)rect.X, (float)rect.Y, (float)rect.Width, (float)rect.Height);

    private void DrawControlArtwork(Graphics graphics, byte[]? data, RectangleF bounds, Color fill, Color ink)
    {
        if (!ReferenceEquals(data, _controlArtworkData))
        {
            _controlArtwork?.Dispose();
            _controlArtwork = null;
            _controlArtworkData = data;
            if (data is { Length: > 0 })
            {
                try
                {
                    using var stream = new MemoryStream(data);
                    using var image = Image.FromStream(stream);
                    _controlArtwork = new Bitmap(image);
                }
                catch (Exception) { }
            }
        }
        using var clip = RoundedRectangle(bounds, 6);
        using var brush = new SolidBrush(fill);
        graphics.FillPath(brush, clip);
        if (_controlArtwork is not { } artwork)
        {
            LucideRuntimeIcons.Draw(graphics, LucideRuntimeIcon.Music, RectangleF.Inflate(bounds, -7, -7), ink, 24);
            return;
        }
        var state = graphics.Save();
        try
        {
            graphics.SetClip(clip, CombineMode.Intersect);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            var side = Math.Min(artwork.Width, artwork.Height);
            graphics.DrawImage(artwork, bounds, new RectangleF((artwork.Width - side) / 2f, (artwork.Height - side) / 2f, side, side), GraphicsUnit.Pixel);
        }
        finally { graphics.Restore(state); }
    }

    private void BeginControlSeek(DesktopWidget widget, PointF point)
    {
        _controlSeekMedia = widget.Controls?.Media is { CanSeek: true, Duration.TotalSeconds: > 0 } media && !string.IsNullOrEmpty(media.TrackId) ? media : null;
        _controlSeekWidget = widget.Id;
        UpdateControlSeek(widget, point);
    }

    private void UpdateControlSeek(DesktopWidget widget, PointF point)
    {
        if (_controlSeekWidget != widget.Id || _controlSeekMedia is not { } media) return;
        if (widget.Controls?.Media is not { CanSeek: true } current || current.TrackId != media.TrackId ||
            !_runtime.State.Widgets.Contains(widget) || _runtime.IsPaused)
        {
            _controlSeekMedia = null;
            return;
        }
        var track = MonitorRectangle(widget, ControlCenterPresentation.MediaProgress(
            ControlCenterPresentation.MediaPanel(ControlCenterPresentation.MediaArea(MonitorBody(widget)))));
        var fraction = track.Width <= 0 ? 0 : Math.Clamp((point.X - track.X) / track.Width, 0, 1);
        _controlSeekPosition = ControlCenterPresentation.ClampMediaPosition(media, TimeSpan.FromTicks((long)(media.Duration.Ticks * (double)fraction)));
        RequestVisualLayerRender();
    }

    private bool CompleteControlSeek(DesktopWidget widget, PointF point)
    {
        if (_controlPressedWidgetId != widget.Id || _controlPressedTarget != ControlCenterPresentation.ControlCenterTarget.MediaProgress) return false;
        UpdateControlSeek(widget, point);
        if (_controlSeekMedia is { } media && _controlSeekWidget == widget.Id)
            _ = _runtime.SeekControlMediaAsync(widget, media, _controlSeekPosition);
        return true;
    }
}
