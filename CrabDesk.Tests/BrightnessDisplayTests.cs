using CrabDesk.Core;
using CrabDesk.Native;
using System.Text.Json;

namespace CrabDesk.Tests;

public sealed class BrightnessDisplayTests
{
    [Fact]
    public void DisplayRowFitsBetweenSlidersAndAudioOutput()
    {
        var body = WidgetMonitorPresentation.Body(372, 292, 20);
        var row = ControlCenterPresentation.BrightnessDisplayRow(body);
        var sliders = ControlCenterPresentation.SliderArea(body);
        var output = ControlCenterPresentation.OutputRow(ControlCenterPresentation.MediaArea(body));
        Assert.True(row.Y >= sliders.Y + sliders.Height);
        Assert.True(row.Y + row.Height < output.Y);
        Assert.Equal(ControlCenterPresentation.ControlCenterTarget.BrightnessDisplayRow,
            ControlCenterPresentation.TargetAt(body, row.X + 20, row.Y + 10));
    }

    [Theory]
    [InlineData("DISPLAY\\ABC\\1_0", "DISPLAY\\ABC\\1", true)]
    [InlineData("display\\abc\\1", "DISPLAY\\ABC\\1", true)]
    [InlineData("DISPLAY\\ABC\\10_0", "DISPLAY\\ABC\\1", false)]
    [InlineData("DISPLAY\\XYZ\\1_0", "DISPLAY\\ABC\\1", false)]
    public void WmiFallbackMatchesOnlyTheSelectedPanel(string instance, string selected, bool expected) =>
        Assert.Equal(expected, BrightnessControl.InstanceMatches(instance, selected));

    [Fact]
    public void DisplaySelectionSurvivesConfigurationSerialization()
    {
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.ControlCenter, ControlBrightnessDisplayId = @"\\.\DISPLAY2" };
        Assert.Equal(widget.ControlBrightnessDisplayId, JsonSerializer.Deserialize<DesktopWidget>(JsonSerializer.Serialize(widget))!.ControlBrightnessDisplayId);
    }
}
