using CrabDesk.Core;
using Xunit;

namespace CrabDesk.Tests;

public sealed class WidgetControlCenterTests
{
    // Every reading is nullable because the matching capability can be missing.
    // A control must therefore say "unavailable" rather than "off": a user who
    // reads "off" keeps clicking a switch the machine cannot move.
    [Fact]
    public void UnavailableControlsSaySoInsteadOfReportingOff()
    {
        var empty = ControlCenterSnapshot.Empty;

        Assert.Equal("不可用", ControlCenterPresentation.ToggleStateText(ControlCenterToggle.WiFi, empty));
        Assert.Equal("不可用", ControlCenterPresentation.ToggleStateText(ControlCenterToggle.Bluetooth, empty));
        Assert.Equal("不可用", ControlCenterPresentation.ToggleStateText(ControlCenterToggle.NightLight, empty));
        Assert.False(ControlCenterPresentation.ToggleAvailable(ControlCenterToggle.WiFi, empty));
        Assert.Equal("不可用", ControlCenterPresentation.VolumeText(empty));
        Assert.Equal("不可用", ControlCenterPresentation.BrightnessText(empty));
    }

    [Fact]
    public void TogglesReportTheirOwnState()
    {
        var snapshot = new ControlCenterSnapshot { WiFiEnabled = true, BluetoothEnabled = false, NightLightEnabled = true };

        Assert.Equal("已开启", ControlCenterPresentation.ToggleStateText(ControlCenterToggle.WiFi, snapshot));
        Assert.Equal("已关闭", ControlCenterPresentation.ToggleStateText(ControlCenterToggle.Bluetooth, snapshot));
        Assert.Equal("已开启", ControlCenterPresentation.ToggleStateText(ControlCenterToggle.NightLight, snapshot));
        Assert.True(ControlCenterPresentation.ToggleAvailable(ControlCenterToggle.Bluetooth, snapshot));
    }

    [Fact]
    public void MutedVolumeIsStatedRatherThanShowingTheLostLevel()
    {
        var muted = new ControlCenterSnapshot { VolumePercent = 42, VolumeMuted = true };

        Assert.Equal("已静音", ControlCenterPresentation.VolumeText(muted));
    }

    [Theory]
    [InlineData("Shape of You", "Ed Sheeran", "Shape of You · Ed Sheeran")]
    [InlineData("Shape of You", "", "Shape of You")]
    [InlineData("", "Ed Sheeran", "Ed Sheeran")]
    [InlineData("", "", "未知曲目")]
    public void MediaSummaryOmitsAnEmptyArtistInsteadOfShowingADanglingDot(
        string title, string artist, string expected)
    {
        var snapshot = new ControlCenterSnapshot { Media = new(title, artist, "", true, true, true, true) };

        Assert.Equal(expected, ControlCenterPresentation.MediaSummary(snapshot));
    }

    [Fact]
    public void NoMediaIsStatedPlainly()
    {
        Assert.Equal("没有正在播放的媒体", ControlCenterPresentation.MediaSummary(null));
        Assert.Equal("没有正在播放的媒体", ControlCenterPresentation.MediaSummary(new ControlCenterSnapshot()));
    }

    // The tiles sit on one row, the sliders below them and the media strip at the
    // bottom; overlap would hide a control behind another.
    [Fact]
    public void CardLayoutKeepsEveryControlInsideTheCardWithoutOverlap()
    {
        var (width, height) = DesktopWidgets.Dimensions(DesktopWidgetSize.Panel);
        var body = WidgetMonitorPresentation.Body(width, height, 8);

        Assert.Equal(ControlCenterPresentation.CardWidth, width);
        Assert.Equal(ControlCenterPresentation.CardHeight, height);

        var tiles = Enum.GetValues<ControlCenterToggle>()
            .Select(toggle => ControlCenterPresentation.Toggle(body, (int)toggle)).ToArray();
        // All switches across one row, left to right, no overlap.
        Assert.Equal(3, tiles.Length);
        for (var index = 1; index < tiles.Length; index++)
        {
            Assert.True(tiles[index].X >= tiles[index - 1].X + tiles[index - 1].Width);
        }
        Assert.All(tiles, tile => Assert.Equal(tiles[0].Y, tile.Y));
        Assert.True(tiles[^1].X + tiles[^1].Width <= body.X + body.Width + 0.01);

        var sliders = ControlCenterPresentation.SliderArea(body);
        Assert.True(sliders.Y >= tiles[0].Y + tiles[0].Height);

        var media = ControlCenterPresentation.MediaArea(body);
        Assert.True(media.Y >= sliders.Y + sliders.Height);
        Assert.True(media.Y + media.Height <= body.Y + body.Height + 0.01);
    }

    [Theory]
    [InlineData(-20, 0)]
    [InlineData(150, 100)]
    [InlineData(double.NaN, 0)]
    public void RequestedLevelsAreClampedToTheSliderRange(double requested, double expected)
    {
        // Slider input can overshoot at the edges, and the COM call would take a
        // negative scalar as garbage.
        Assert.Equal(expected, ControlCenterPresentation.ClampPercent(requested));
    }

    [Fact]
    public void PanelSizeIsOnlyOfferedToTheControlCentre()
    {
        Assert.Equal([DesktopWidgetSize.Panel], DesktopWidgets.SupportedSizes(DesktopWidgetKind.ControlCenter));
        Assert.DoesNotContain(DesktopWidgetSize.Panel, DesktopWidgets.SupportedSizes(DesktopWidgetKind.Performance));
    }

    // New sizes are appended so an existing widget's stored number keeps its
    // meaning; inserting one would silently resize saved cards.
    [Fact]
    public void AppendingThePanelSizeDoesNotRenumberExistingSizes()
    {
        Assert.Equal(0, (int)DesktopWidgetSize.Small);
        Assert.Equal(3, (int)DesktopWidgetSize.Large);
        Assert.Equal(6, (int)DesktopWidgetSize.Custom);
        Assert.Equal(7, (int)DesktopWidgetSize.Panel);
    }
}
