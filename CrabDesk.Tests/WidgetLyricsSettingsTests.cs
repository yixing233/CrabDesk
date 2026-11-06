using System.Text.Json;
using CrabDesk.Core;

namespace CrabDesk.Tests;

public sealed class WidgetLyricsSettingsTests
{
    [Fact]
    public void PersistedLyricsKeepOptionsAndLocalAssociationWithoutPlaybackState()
    {
        var widget = new DesktopWidget
        {
            Kind = DesktopWidgetKind.Lyrics, Size = DesktopWidgetSize.Custom, Bounds = new(40, 60, 450, 310),
            LyricsLineCount = 7, LyricsFontSize = 28, LyricsOffsetMilliseconds = 750,
            LyricsFontFamily = "Consolas", LyricsFontWeight = 700, LyricsFontStyle = WidgetLyricsFontStyle.Italic,
            LyricsLightText = true, LyricsWordHighlight = false, LyricsTransparentBackground = true,
            LyricsLocalText = "[00:00.00]A line\n[00:05.00]Another line", LyricsLocalFormat = "lrc", LyricsLocalTrackKey = "Track\nArtist\nAlbum",
            Lyrics = new(new ControlCenterMedia("Private playback title", "", "", true, false, false, false), null)
        };
        var json = JsonSerializer.Serialize(widget);
        Assert.DoesNotContain("Private playback title", json);
        var restored = JsonSerializer.Deserialize<DesktopWidget>(json)!;
        var state = new CrabDeskState(); state.Widgets.Add(restored); DesktopWidgets.Normalize(state);
        Assert.Equal((450d, 310d), DesktopWidgets.Dimensions(restored));
        Assert.Equal(7, restored.LyricsLineCount); Assert.Equal(28, restored.LyricsFontSize);
        Assert.Equal("Consolas", restored.LyricsFontFamily); Assert.Equal(700, restored.LyricsFontWeight);
        Assert.Equal(WidgetLyricsFontStyle.Italic, restored.LyricsFontStyle);
        Assert.Equal(750, restored.LyricsOffsetMilliseconds); Assert.True(restored.LyricsLightText);
        Assert.False(restored.LyricsWordHighlight); Assert.Equal(widget.LyricsLocalText, restored.LyricsLocalText);
        Assert.True(restored.LyricsTransparentBackground);
        Assert.False(DesktopWidgets.HasBackground(restored));
        Assert.True(DesktopWidgets.HasBackground(new DesktopWidget { Kind = DesktopWidgetKind.Note, LyricsTransparentBackground = true }));
        Assert.Equal(widget.LyricsLocalTrackKey, restored.LyricsLocalTrackKey); Assert.Null(restored.Lyrics);
    }

    [Fact]
    public void NewKindPreservesOldNumericKindsAndNormalizesInvalidDisplayOptions()
    {
        Assert.Equal(8, (int)DesktopWidgetKind.ControlCenter);
        Assert.Equal(9, (int)DesktopWidgetKind.Lyrics);
        var old = JsonSerializer.Deserialize<DesktopWidget>("{\"Kind\":8}")!;
        Assert.Equal(DesktopWidgetKind.ControlCenter, old.Kind);
        var legacy = JsonSerializer.Deserialize<DesktopWidget>("{\"Kind\":9}")!;
        Assert.False(legacy.LyricsTransparentBackground);
        Assert.Equal(BoxAppearance.DefaultFontFamily, legacy.LyricsFontFamily);
        Assert.Equal(600, legacy.LyricsFontWeight); Assert.Equal(WidgetLyricsFontStyle.Normal, legacy.LyricsFontStyle);
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Lyrics, LyricsLineCount = 99,
            LyricsFontSize = double.NaN, LyricsOffsetMilliseconds = int.MinValue,
            LyricsFontFamily = "  ", LyricsFontWeight = -1, LyricsFontStyle = (WidgetLyricsFontStyle)99 };
        var state = new CrabDeskState(); state.Widgets.Add(widget); DesktopWidgets.Normalize(state);
        Assert.Equal(9, widget.LyricsLineCount); Assert.Equal(24, widget.LyricsFontSize);
        Assert.Equal(-10000, widget.LyricsOffsetMilliseconds);
        Assert.Equal(BoxAppearance.DefaultFontFamily, widget.LyricsFontFamily);
        Assert.Equal(600, widget.LyricsFontWeight); Assert.Equal(WidgetLyricsFontStyle.Normal, widget.LyricsFontStyle);
        Assert.Equal(DesktopWidgetSize.Panel, widget.Size);
        Assert.True(DesktopWidgets.SupportsFreeResize(widget.Kind));
        Assert.True(DesktopWidgets.RequiresMonitoring(widget.Kind));
    }

    [Theory]
    [InlineData(100, 100)]
    [InlineData(450, 500)]
    [InlineData(900, 900)]
    [InlineData(0, 600)]
    [InlineData(1000, 600)]
    public void FontWeightsNormalizeToTheAvailableChoices(int value, int expected) =>
        Assert.Equal(expected, WidgetLyricsSettings.NormalizeFontWeight(value));
}
