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

    // The progress bar has to move on its own between polls, otherwise it jumps
    // once every couple of seconds while the music plays smoothly.
    [Fact]
    public void ProgressAdvancesWithTheClockWhilePlaying()
    {
        var captured = DateTimeOffset.Now;
        var media = new ControlCenterMedia("t", "a", "", true, true, true, true)
        {
            Duration = TimeSpan.FromSeconds(190), Position = TimeSpan.FromSeconds(30), CapturedAt = captured
        };

        Assert.Equal(TimeSpan.FromSeconds(30), media.PositionNow(captured));
        Assert.Equal(TimeSpan.FromSeconds(31.5), media.PositionNow(captured.AddSeconds(1.5)));
    }

    [Fact]
    public void ProgressStaysStillWhilePaused()
    {
        var captured = DateTimeOffset.Now;
        var media = new ControlCenterMedia("t", "a", "", false, true, true, true)
        {
            Duration = TimeSpan.FromSeconds(190), Position = TimeSpan.FromSeconds(30), CapturedAt = captured
        };

        // A paused player must not drift forward just because time passed.
        Assert.Equal(TimeSpan.FromSeconds(30), media.PositionNow(captured.AddSeconds(5)));
    }

    [Fact]
    public void ProgressNeverRunsPastTheEndOfTheTrack()
    {
        var captured = DateTimeOffset.Now;
        var media = new ControlCenterMedia("t", "a", "", true, true, true, true)
        {
            Duration = TimeSpan.FromSeconds(10), Position = TimeSpan.FromSeconds(9), CapturedAt = captured
        };

        Assert.Equal(TimeSpan.FromSeconds(10), media.PositionNow(captured.AddSeconds(60)));
    }

    [Fact]
    public void LiveStreamReportsNoProgress()
    {
        // A stream with no end time cannot show a bar; it must report nothing
        // rather than a percentage against zero.
        var media = new ControlCenterMedia("t", "a", "", true, true, true, true);
        Assert.Equal(TimeSpan.Zero, media.PositionNow(DateTimeOffset.Now));
        Assert.Equal("", ControlCenterPresentation.MediaTimeText(media, DateTimeOffset.Now));
    }

    [Fact]
    public void MediaTimeIsFormattedAsMinutesAndSeconds()
    {
        var captured = DateTimeOffset.Now;
        var media = new ControlCenterMedia("t", "a", "", false, true, true, true)
        {
            Duration = TimeSpan.FromSeconds(190.4), Position = TimeSpan.FromSeconds(37.9), CapturedAt = captured
        };

        Assert.Equal("0:37 / 3:10", ControlCenterPresentation.MediaTimeText(media, captured));
    }

    // The output row is a button that cycles endpoints, so it has to know which
    // one comes next and wrap rather than fall off the end.
    [Fact]
    public void OutputRowCyclesThroughEveryDeviceAndWraps()
    {
        var devices = new List<ControlCenterOutputDevice>
        {
            new("a", "耳机", true), new("b", "扬声器", false), new("c", "显示器", false)
        };
        var snapshot = new ControlCenterSnapshot { OutputDeviceId = "a", OutputDevices = devices };
        Assert.Equal("b", ControlCenterPresentation.NextOutputDevice(snapshot));

        snapshot = snapshot with { OutputDeviceId = "b" };
        Assert.Equal("c", ControlCenterPresentation.NextOutputDevice(snapshot));

        snapshot = snapshot with { OutputDeviceId = "c" };
        Assert.Equal("a", ControlCenterPresentation.NextOutputDevice(snapshot));
    }

    [Fact]
    public void OutputRowSaysNothingToDoWhenThereIsOnlyOneDevice()
    {
        var single = new ControlCenterSnapshot
        {
            OutputDeviceId = "a",
            OutputDevices = [new("a", "耳机", true)]
        };
        Assert.Null(ControlCenterPresentation.NextOutputDevice(single));
        Assert.Null(ControlCenterPresentation.NextOutputDevice(null));
    }
}
