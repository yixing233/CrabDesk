using CrabDesk.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CrabDesk.WinUI.Views;

public sealed partial class WidgetsPage
{
    private void LoadLyricsSettings(DesktopWidget widget)
    {
        LyricsEditor.Visibility = Show(widget.Kind == DesktopWidgetKind.Lyrics);
        if (widget.Kind != DesktopWidgetKind.Lyrics) return;
        var fonts = App.GetService<Services.IFontCatalogService>().FontFamilies.Append(widget.LyricsFontFamily)
            .Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        LyricsFontFamily.ItemsSource = fonts;
        LyricsFontFamily.SelectedItem = fonts.First(name => string.Equals(name, widget.LyricsFontFamily, StringComparison.CurrentCultureIgnoreCase));
        LyricsFontStyle.SelectedIndex = (int)widget.LyricsFontStyle;
        LyricsFontWeight.ItemsSource = Enumerable.Range(1, 9).Select(value => WidgetLyricsSettings.FontWeightLabel(value * 100)).ToArray();
        LyricsFontWeight.SelectedIndex = widget.LyricsFontWeight / 100 - 1;
        LyricsLines.ItemsSource = Enumerable.Range(1, 9).Select(value => $"{value} 行").ToArray();
        LyricsLines.SelectedIndex = widget.LyricsLineCount - 1;
        LyricsFont.Value = widget.LyricsFontSize;
        LyricsWords.IsOn = widget.LyricsWordHighlight;
        LyricsOffset.Value = widget.LyricsOffsetMilliseconds;
        LyricsInk.SelectedIndex = widget.LyricsLightText switch { true => 1, false => 2, _ => 0 };
        LyricsTransparentBackground.IsOn = widget.LyricsTransparentBackground;
        UpdateLyricsStatus();
    }

    private void UpdateLyricsStatus()
    {
        if (_selected is not { Kind: DesktopWidgetKind.Lyrics } widget) return;
        var snapshot = widget.Lyrics;
        LyricsSourceStatus.Text = snapshot?.Document is { } lyrics
            ? "歌词来源：" + lyrics.Source + (lyrics.HasWordTiming ? " · 逐字" : " · 逐行")
            : snapshot?.IsLoading == true ? "正在匹配歌词…" : snapshot?.Media is null ? "播放音乐后自动匹配歌词。" : "暂无匹配歌词，可导入本地文件。";
    }

    private void SaveLyricsOptions()
    {
        if (_loading || _selected is not { Kind: DesktopWidgetKind.Lyrics } widget || LyricsLines.SelectedIndex < 0 ||
            !double.IsFinite(LyricsFont.Value) || !double.IsFinite(LyricsOffset.Value)) return;
        _saving = true;
        try { _runtime.SetLyricsOptions(widget, LyricsLines.SelectedIndex + 1, LyricsFont.Value, (int)Math.Round(LyricsOffset.Value), LyricsWords.IsOn); }
        finally { _saving = false; }
        RefreshGallery(changedWidget: widget);
    }

    private void LyricsLines_SelectionChanged(object sender, SelectionChangedEventArgs args) => SaveLyricsOptions();
    private void LyricsNumber_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => SaveLyricsOptions();
    private void LyricsWords_Toggled(object sender, RoutedEventArgs args) => SaveLyricsOptions();
    private void LyricsTypography_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_loading || _selected is not { Kind: DesktopWidgetKind.Lyrics } widget ||
            LyricsFontFamily.SelectedItem is not string family || LyricsFontStyle.SelectedIndex < 0 || LyricsFontWeight.SelectedIndex < 0) return;
        _saving = true;
        try { _runtime.SetLyricsTypography(widget, family, (LyricsFontWeight.SelectedIndex + 1) * 100,
            (WidgetLyricsFontStyle)LyricsFontStyle.SelectedIndex); }
        finally { _saving = false; }
        RefreshGallery(changedWidget: widget);
    }
    private void LyricsTransparentBackground_Toggled(object sender, RoutedEventArgs args)
    {
        if (_loading || _selected is not { Kind: DesktopWidgetKind.Lyrics } widget) return;
        _saving = true;
        try { _runtime.SetLyricsTransparentBackground(widget, LyricsTransparentBackground.IsOn); }
        finally { _saving = false; }
        RefreshGallery(changedWidget: widget);
    }
    private void LyricsInk_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_loading || _selected is not { Kind: DesktopWidgetKind.Lyrics } widget || LyricsInk.SelectedIndex < 0) return;
        _saving = true;
        try { _runtime.SetLyricsLightText(widget, LyricsInk.SelectedIndex switch { 1 => true, 2 => false, _ => null }); }
        finally { _saving = false; }
        RefreshGallery(changedWidget: widget);
    }

    private void LyricsReload_Click(object sender, RoutedEventArgs args)
    {
        if (_selected is not { Kind: DesktopWidgetKind.Lyrics } widget) return;
        _runtime.UseAutomaticLyrics(widget);
        UpdateLyricsStatus();
    }

    private async void LyricsImport_Click(object sender, RoutedEventArgs args)
    {
        if (_selected is not { Kind: DesktopWidgetKind.Lyrics } widget) return;
        try
        {
            var window = App.GetService<MainWindow>();
            await _runtime.ImportLyricsAsync(widget, WinRT.Interop.WindowNative.GetWindowHandle(window));
            UpdateLyricsStatus();
        }
        catch (Exception error) { Error("导入歌词失败：" + error.Message); }
    }
}
