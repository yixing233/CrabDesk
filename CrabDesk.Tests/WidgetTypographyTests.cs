using System.Text.Json;
using CrabDesk.Core;

namespace CrabDesk.Tests;

public sealed class WidgetTypographyTests
{
    [Fact]
    public void IndependentTypographySurvivesJsonReload()
    {
        var network = new DesktopWidget { Kind = DesktopWidgetKind.Network, FontFamily = "Segoe UI", FontScale = 1.25, FontWeight = 400, FontItalic = true };
        var weather = new DesktopWidget { Kind = DesktopWidgetKind.Weather };
        var state = JsonSerializer.Deserialize<CrabDeskState>(JsonSerializer.Serialize(new CrabDeskState { Widgets = [network, weather] }))!;
        DesktopWidgets.Normalize(state);
        var restored = state.Widgets[0];
        Assert.Equal("Segoe UI", restored.FontFamily);
        Assert.Equal(1.25, restored.FontScale);
        Assert.Equal(400, restored.FontWeight);
        Assert.True(restored.FontItalic);
        Assert.Equal(BoxAppearance.DefaultFontFamily, state.Widgets[1].FontFamily);
        Assert.Equal(1, state.Widgets[1].FontScale);
        Assert.Equal(0, state.Widgets[1].FontWeight);
        Assert.False(state.Widgets[1].FontItalic);
    }

    [Theory]
    [InlineData(double.NaN, 1)]
    [InlineData(.1, .75)]
    [InlineData(2, 1.5)]
    public void InvalidTypographyNormalizesWithoutChangingLyrics(double scale, double expected)
    {
        var widget = new DesktopWidget { FontFamily = " ", FontScale = scale, FontWeight = 999, LyricsFontFamily = "Segoe UI", LyricsFontSize = 28 };
        DesktopWidgets.Normalize(new() { Widgets = [widget] });
        Assert.Equal(BoxAppearance.DefaultFontFamily, widget.FontFamily);
        Assert.Equal(expected, widget.FontScale);
        Assert.Equal(0, widget.FontWeight);
        Assert.Equal("Segoe UI", widget.LyricsFontFamily);
        Assert.Equal(28, widget.LyricsFontSize);
    }
}
