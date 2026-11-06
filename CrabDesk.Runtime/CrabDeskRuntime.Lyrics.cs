using System.Diagnostics;
using CrabDesk.Core;
using CrabDesk.Native;
using Forms = System.Windows.Forms;

namespace CrabDesk.Runtime;

public sealed partial class CrabDeskRuntime
{
    private DesktopLyricsController? _lyricsController;
    private WidgetLyricsService? _lyricsProvider;
    private WidgetControlCenterMonitor? _lyricsMedia;
    private long _lyricsLastTick;
    private readonly Dictionary<Guid, (string Text, string Format, WidgetLyricsDocument? Document)> _localLyrics = [];

    public Task RefreshLyricsWidgetsAsync(bool force = false)
    {
        if (_disposed || IsPaused || !State.Widgets.Any(widget => widget.Kind == DesktopWidgetKind.Lyrics)) return Task.CompletedTask;
        var now = Stopwatch.GetTimestamp();
        if (!force && _lyricsLastTick != 0 && Stopwatch.GetElapsedTime(_lyricsLastTick, now).TotalMilliseconds < 700)
            return Task.CompletedTask;
        _lyricsLastTick = now;
        if (_lyricsController is null)
        {
            _lyricsMedia = new WidgetControlCenterMonitor();
            _lyricsProvider = new WidgetLyricsService();
            _lyricsController = new DesktopLyricsController(_lyricsMedia.ReadCurrentMediaAsync, _lyricsProvider);
            _lyricsController.Changed += OnLyricsChanged;
        }
        return _lyricsController.RefreshAsync(force);
    }

    private void OnLyricsChanged()
    {
        if (_disposed) return;
        _beginInvoke(() =>
        {
            if (_disposed || IsPaused || _lyricsController is null) return;
            var snapshot = _lyricsController.Snapshot;
            foreach (var widget in State.Widgets.Where(widget => widget.Kind == DesktopWidgetKind.Lyrics))
                widget.Lyrics = LyricsForWidget(widget, snapshot);
            NotifyMonitorFrame();
        });
    }

    private WidgetLyricsSnapshot LyricsForWidget(DesktopWidget widget, WidgetLyricsSnapshot snapshot)
    {
        if (string.IsNullOrEmpty(widget.LyricsLocalText) || snapshot.Media is not { } media ||
            widget.LyricsLocalTrackKey != WidgetLyricsSettings.TrackKey(media)) return snapshot;
        if (!_localLyrics.TryGetValue(widget.Id, out var cached) || cached.Text != widget.LyricsLocalText || cached.Format != widget.LyricsLocalFormat)
        {
            cached = (widget.LyricsLocalText, widget.LyricsLocalFormat,
                WidgetLyricsParser.Parse(widget.LyricsLocalText, widget.LyricsLocalFormat, "本地歌词"));
            _localLyrics[widget.Id] = cached;
        }
        return cached.Document is null ? snapshot with { Error = "本地歌词无法解析，请重新导入。" }
            : snapshot with { Document = cached.Document, IsLoading = false, Error = "" };
    }

    public void SetLyricsOptions(DesktopWidget widget, int lines, double fontSize, int offsetMilliseconds, bool wordHighlight)
    {
        if (_disposed || widget.Kind != DesktopWidgetKind.Lyrics || !State.Widgets.Contains(widget)) return;
        lines = WidgetLyricsSettings.NormalizeLineCount(lines);
        fontSize = WidgetLyricsSettings.NormalizeFontSize(fontSize);
        offsetMilliseconds = WidgetLyricsSettings.NormalizeOffset(offsetMilliseconds);
        if (widget.LyricsLineCount == lines && widget.LyricsFontSize == fontSize &&
            widget.LyricsOffsetMilliseconds == offsetMilliseconds && widget.LyricsWordHighlight == wordHighlight) return;
        widget.LyricsLineCount = lines;
        widget.LyricsFontSize = fontSize;
        widget.LyricsOffsetMilliseconds = offsetMilliseconds;
        widget.LyricsWordHighlight = wordHighlight;
        WidgetChanged(widget);
    }

    public void SetLyricsLightText(DesktopWidget widget, bool? light)
    {
        if (_disposed || widget.Kind != DesktopWidgetKind.Lyrics || !State.Widgets.Contains(widget) || widget.LyricsLightText == light) return;
        widget.LyricsLightText = light;
        WidgetChanged(widget);
    }

    public void SetLyricsTypography(DesktopWidget widget, string? fontFamily, int fontWeight, WidgetLyricsFontStyle fontStyle)
    {
        if (_disposed || widget.Kind != DesktopWidgetKind.Lyrics || !State.Widgets.Contains(widget)) return;
        fontFamily = WidgetLyricsSettings.NormalizeFontFamily(fontFamily);
        fontWeight = WidgetLyricsSettings.NormalizeFontWeight(fontWeight);
        fontStyle = WidgetLyricsSettings.NormalizeFontStyle(fontStyle);
        if (widget.LyricsFontFamily == fontFamily && widget.LyricsFontWeight == fontWeight && widget.LyricsFontStyle == fontStyle) return;
        widget.LyricsFontFamily = fontFamily;
        widget.LyricsFontWeight = fontWeight;
        widget.LyricsFontStyle = fontStyle;
        WidgetChanged(widget);
    }

    public void SetLyricsTransparentBackground(DesktopWidget widget, bool transparent)
    {
        if (_disposed || widget.Kind != DesktopWidgetKind.Lyrics || !State.Widgets.Contains(widget) ||
            widget.LyricsTransparentBackground == transparent) return;
        widget.LyricsTransparentBackground = transparent;
        WidgetChanged(widget);
    }

    public async Task ImportLyricsAsync(DesktopWidget widget, IntPtr owner)
    {
        if (_disposed || widget.Kind != DesktopWidgetKind.Lyrics || !State.Widgets.Contains(widget)) return;
        if (widget.Lyrics?.Media is not { } media)
            throw new InvalidOperationException("请先在播放器中播放歌曲，再导入对应的歌词。");
        using var picker = new Forms.OpenFileDialog
        {
            Title = "为「" + media.Title + "」导入歌词",
            Filter = "歌词文件|*.lrc;*.qrc;*.yrc;*.ttml;*.xml|所有文件|*.*",
            CheckFileExists = true, Multiselect = false
        };
        if (picker.ShowDialog(new LyricsDialogOwner(owner)) != Forms.DialogResult.OK) return;
        if (new FileInfo(picker.FileName).Length > 1024 * 1024)
            throw new InvalidOperationException("歌词文件过大，请选择小于 1 MB 的文件。");
        var raw = await File.ReadAllTextAsync(picker.FileName);
        var format = Path.GetExtension(picker.FileName).TrimStart('.');
        var document = await Task.Run(() => WidgetLyricsParser.Parse(raw, format, "本地歌词"));
        if (document is null || document.Lines.Count == 0)
            throw new InvalidOperationException("没有读到有效的时间轴，请使用 LRC、QRC、YRC 或 TTML 歌词。");
        if (_disposed || !State.Widgets.Contains(widget)) return;
        widget.LyricsLocalText = raw;
        widget.LyricsLocalFormat = format;
        widget.LyricsLocalTrackKey = WidgetLyricsSettings.TrackKey(media);
        _localLyrics[widget.Id] = (raw, format, document);
        if (widget.Lyrics is { } snapshot) widget.Lyrics = LyricsForWidget(widget, snapshot);
        WidgetChanged(widget);
    }

    public void UseAutomaticLyrics(DesktopWidget widget)
    {
        if (_disposed || widget.Kind != DesktopWidgetKind.Lyrics || !State.Widgets.Contains(widget)) return;
        widget.LyricsLocalText = widget.LyricsLocalFormat = widget.LyricsLocalTrackKey = "";
        _localLyrics.Remove(widget.Id);
        widget.Lyrics = _lyricsController?.Snapshot;
        WidgetChanged(widget);
        _ = RefreshLyricsWidgetsAsync(true);
    }

    private void PauseLyrics()
    {
        _lyricsLastTick = 0;
        _lyricsController?.Reset();
    }

    private void DisposeLyrics()
    {
        if (_lyricsController is { } controller) { controller.Changed -= OnLyricsChanged; controller.Dispose(); }
        _lyricsController = null;
        _lyricsProvider?.Dispose(); _lyricsProvider = null;
        _lyricsMedia?.Dispose(); _lyricsMedia = null;
        _localLyrics.Clear(); _lyricsLastTick = 0;
    }

    private sealed record LyricsDialogOwner(IntPtr Handle) : Forms.IWin32Window;
}
