using System.Drawing;
using CrabDesk.Core;
using Forms = System.Windows.Forms;

namespace CrabDesk.Runtime;

internal sealed partial class DesktopBoxForm
{
    private readonly Dictionary<Guid, DesktopLyricsVisual> _lyricsVisuals = [];
    private Forms.Timer? _lyricsAnimationTimer;

    private void DrawLyricsWidget(Graphics graphics, DesktopWidget widget, Color text, Color accent)
    {
        var bounds = WidgetRectangle(widget);
        if (widget.LyricsLightText is { } light) text = light ? Color.White : Color.FromArgb(31, 35, 41);
        var snapshot = widget.Lyrics ?? WidgetLyricsSnapshot.Empty;
        var media = snapshot.Media;
        var title = media is null ? widget.DisplayTitle : string.Join(" · ", new[] { media.Title, media.Artist }.Where(value => !string.IsNullOrWhiteSpace(value)));
        MediaPlayerIcons.Draw(graphics, media?.SourceAppId, new(bounds.X + 12, bounds.Y + 10, 16, 16), Color.FromArgb(165, text));
        WidgetText(graphics, title, new(bounds.X + 34, bounds.Y + 7, bounds.Width - 72, 22), Color.FromArgb(180, text), 11);
        if (!_lyricsVisuals.TryGetValue(widget.Id, out var visual)) _lyricsVisuals[widget.Id] = visual = new DesktopLyricsVisual();
        var area = new RectangleF(bounds.X + 12, bounds.Y + 35, Math.Max(1, bounds.Width - 24), Math.Max(1, bounds.Height - 47));
        visual.Draw(graphics, snapshot, area, text, accent, widget.LyricsLineCount, widget.LyricsFontSize,
            widget.LyricsOffsetMilliseconds, widget.LyricsWordHighlight, _runtime.State.Settings.Appearance.AnimationEnabled,
            _scale, DateTimeOffset.Now, widget.LyricsFontFamily, widget.LyricsFontWeight, widget.LyricsFontStyle);
        UpdateLyricsAnimationTimer();
    }

    private void UpdateLyricsAnimationTimer()
    {
        var active = !_resourcesDisposed && Visible && !_runtime.IsPaused &&
            DesktopWidgetItems.Any(widget => widget.Kind == DesktopWidgetKind.Lyrics &&
                _lyricsVisuals.TryGetValue(widget.Id, out var visual) && visual.NeedsAnimation);
        if (!active) { _lyricsAnimationTimer?.Stop(); return; }
        if (_lyricsAnimationTimer is null)
        {
            _lyricsAnimationTimer = new Forms.Timer { Interval = 33 };
            _lyricsAnimationTimer.Tick += (_, _) =>
            {
                UpdateLyricsAnimationTimer();
                if (_lyricsAnimationTimer.Enabled && !TryPresentLyricsFrame()) RequestVisualLayerRender();
            };
        }
        _lyricsAnimationTimer.Start();
    }

    private void RefreshLyricsVisuals()
    {
        var ids = DesktopWidgetItems.Where(widget => widget.Kind == DesktopWidgetKind.Lyrics).Select(widget => widget.Id).ToHashSet();
        foreach (var id in _lyricsVisuals.Keys.Where(id => !ids.Contains(id)).ToArray())
        { _lyricsVisuals[id].Dispose(); _lyricsVisuals.Remove(id); }
        UpdateLyricsAnimationTimer();
    }

    private void DisposeLyricsVisuals()
    {
        _lyricsAnimationTimer?.Dispose(); _lyricsAnimationTimer = null;
        foreach (var visual in _lyricsVisuals.Values) visual.Dispose();
        _lyricsVisuals.Clear();
    }

    private void AddLyricsContextMenu(FluentContextMenuStrip menu, DesktopWidget widget)
    {
        var lines = new FluentToolStripMenuItem("显示行数");
        for (var count = 1; count <= 9; count++)
        {
            var value = count;
            var item = new FluentToolStripMenuItem($"{count} 行") { Checked = widget.LyricsLineCount == count };
            item.Click += (_, _) => _runtime.SetLyricsOptions(widget, value, widget.LyricsFontSize, widget.LyricsOffsetMilliseconds, widget.LyricsWordHighlight);
            lines.DropDownItems.Add(item);
        }
        menu.Items.Add(lines);
        var font = new FluentToolStripMenuItem("歌词字号");
        foreach (var size in new[] { 18, 20, 24, 28, 32, 36 })
        {
            var item = new FluentToolStripMenuItem($"{size}") { Checked = widget.LyricsFontSize == size };
            item.Click += (_, _) => _runtime.SetLyricsOptions(widget, widget.LyricsLineCount, size, widget.LyricsOffsetMilliseconds, widget.LyricsWordHighlight);
            font.DropDownItems.Add(item);
        }
        menu.Items.Add(font);
        var familyMenu = new FluentToolStripMenuItem("歌词字体");
        foreach (var family in new[] { BoxAppearance.DefaultFontFamily, "Segoe UI", "Arial", "Consolas", widget.LyricsFontFamily }
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var item = new FluentToolStripMenuItem(family) { Checked = string.Equals(widget.LyricsFontFamily, family, StringComparison.OrdinalIgnoreCase) };
            item.Click += (_, _) => _runtime.SetLyricsTypography(widget, family, widget.LyricsFontWeight, widget.LyricsFontStyle);
            familyMenu.DropDownItems.Add(item);
        }
        familyMenu.DropDownItems.Add("选择其他字体…", null, (_, _) => _runtime.OpenWidgetEditor(widget.Id));
        menu.Items.Add(familyMenu);
        var weightMenu = new FluentToolStripMenuItem("字重");
        for (var value = 100; value <= 900; value += 100)
        {
            var weight = value;
            var item = new FluentToolStripMenuItem(WidgetLyricsSettings.FontWeightLabel(weight)) { Checked = widget.LyricsFontWeight == weight };
            item.Click += (_, _) => _runtime.SetLyricsTypography(widget, widget.LyricsFontFamily, weight, widget.LyricsFontStyle);
            weightMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(weightMenu);
        var italic = new FluentToolStripMenuItem("斜体") { Checked = widget.LyricsFontStyle == WidgetLyricsFontStyle.Italic };
        italic.Click += (_, _) => _runtime.SetLyricsTypography(widget, widget.LyricsFontFamily, widget.LyricsFontWeight,
            widget.LyricsFontStyle == WidgetLyricsFontStyle.Italic ? WidgetLyricsFontStyle.Normal : WidgetLyricsFontStyle.Italic);
        menu.Items.Add(italic);
        var ink = new FluentToolStripMenuItem("文字颜色");
        foreach (var (label, light) in new (string, bool?)[] { ("自动", null), ("浅色", true), ("深色", false) })
        {
            var item = new FluentToolStripMenuItem(label) { Checked = widget.LyricsLightText == light };
            item.Click += (_, _) => _runtime.SetLyricsLightText(widget, light);
            ink.DropDownItems.Add(item);
        }
        menu.Items.Add(ink);
        var transparent = new FluentToolStripMenuItem("透明背景") { Checked = widget.LyricsTransparentBackground };
        transparent.Click += (_, _) => _runtime.SetLyricsTransparentBackground(widget, !widget.LyricsTransparentBackground);
        menu.Items.Add(transparent);
        var highlight = new FluentToolStripMenuItem("逐字覆盖动画") { Checked = widget.LyricsWordHighlight };
        highlight.Click += (_, _) => _runtime.SetLyricsOptions(widget, widget.LyricsLineCount, widget.LyricsFontSize,
            widget.LyricsOffsetMilliseconds, !widget.LyricsWordHighlight);
        menu.Items.Add(highlight);
        var timing = new FluentToolStripMenuItem($"歌词校准（{widget.LyricsOffsetMilliseconds / 1000d:+0.00;-0.00;0.00} s）");
        timing.DropDownItems.Add("歌词提前 0.25 秒", null, (_, _) => SetLyricsOffset(widget, widget.LyricsOffsetMilliseconds + 250));
        timing.DropDownItems.Add("歌词延后 0.25 秒", null, (_, _) => SetLyricsOffset(widget, widget.LyricsOffsetMilliseconds - 250));
        timing.DropDownItems.Add("重置校准", null, (_, _) => SetLyricsOffset(widget, 0));
        menu.Items.Add(timing);
        menu.Items.Add("重新匹配歌词", null, (_, _) => _runtime.UseAutomaticLyrics(widget));
        menu.Items.Add("导入本地歌词…", null, async (_, _) =>
        {
            try { await _runtime.ImportLyricsAsync(widget, Handle); }
            catch (Exception error) { TryAction(() => throw new InvalidOperationException("导入歌词失败：" + error.Message)); }
        });
    }

    private void SetLyricsOffset(DesktopWidget widget, int offset) => _runtime.SetLyricsOptions(widget,
        widget.LyricsLineCount, widget.LyricsFontSize, offset, widget.LyricsWordHighlight);
}
