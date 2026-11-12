using CrabDesk.Core;
using Xunit;

namespace CrabDesk.Tests;

public sealed class WidgetControlCenterTests
{
    [Theory]
    [InlineData(0)] [InlineData(8)] [InlineData(20)]
    public void CompactMediaLayoutKeepsArtworkTextProgressAndButtonsSeparate(double corners)
    {
        var body = ControlCenterPresentation.Body(372, 292, corners);
        var panel = ControlCenterPresentation.MediaPanel(ControlCenterPresentation.MediaArea(body));
        var regions = new[] { ControlCenterPresentation.MediaArtwork(panel), ControlCenterPresentation.MediaTitle(panel),
            ControlCenterPresentation.MediaArtist(panel), ControlCenterPresentation.MediaProgressHitArea(panel), ControlCenterPresentation.MediaTime(panel) }
            .Concat(ControlCenterPresentation.MediaButtonRects(panel)).ToArray();
        foreach (var region in regions)
        {
            Assert.True(region.X >= panel.X && region.Y >= panel.Y);
            Assert.True(region.X + region.Width <= panel.X + panel.Width + .01 && region.Y + region.Height <= panel.Y + panel.Height + .01);
        }
        for (var i = 0; i < regions.Length; i++)
            for (var j = i + 1; j < regions.Length; j++)
            {
                var a = regions[i]; var b = regions[j];
                Assert.True(a.X + a.Width <= b.X || b.X + b.Width <= a.X || a.Y + a.Height <= b.Y || b.Y + b.Height <= a.Y,
                    $"Overlapping media regions: {a} and {b}");
            }
        var microphone = ControlCenterPresentation.MicrophoneButton(body);
        Assert.Equal(ControlCenterPresentation.ControlCenterTarget.Microphone,
            ControlCenterPresentation.TargetAt(body, microphone.X + microphone.Width / 2, microphone.Y + microphone.Height / 2));
        var track = ControlCenterPresentation.MediaProgress(panel);
        Assert.Equal(ControlCenterPresentation.ControlCenterTarget.MediaProgress,
            ControlCenterPresentation.TargetAt(body, track.X + track.Width / 2, track.Y + 2));
    }

    [Theory]
    [InlineData(-10, 20)] [InlineData(50, 50)] [InlineData(150, 80)]
    public void SeekingRespectsThePlayersReportedRange(double seconds, double expected)
    {
        var media = new ControlCenterMedia("Track", "Artist", "", false, true, true, true)
        { Duration = TimeSpan.FromSeconds(100), SeekMinimum = TimeSpan.FromSeconds(20), SeekMaximum = TimeSpan.FromSeconds(80) };
        Assert.Equal(TimeSpan.FromSeconds(expected), ControlCenterPresentation.ClampMediaPosition(media, TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void PlaybackRateIsAppliedBetweenTimelineUpdates()
    {
        var captured = DateTimeOffset.Now;
        var media = new ControlCenterMedia("Track", "", "", true, true, true, true)
        { Duration = TimeSpan.FromMinutes(2), Position = TimeSpan.FromSeconds(10), CapturedAt = captured, PlaybackRate = 2 };
        Assert.Equal(TimeSpan.FromSeconds(16), media.PositionNow(captured.AddSeconds(3)));
    }

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
        var body = ControlCenterPresentation.Body(width, height, 8);

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

    // The row opens a list rather than cycling. With more than two devices,
    // cycling makes the user click through them guessing at the order and offers
    // no way back except around again; the list shows every device at once and
    // marks the one currently in use.
    [Fact]
    public void OutputRowListsEveryDeviceAndMarksTheCurrentOne()
    {
        var snapshot = new ControlCenterSnapshot
        {
            OutputDeviceId = "b",
            OutputDeviceName = "扬声器",
            OutputDevices =
            [
                new("a", "耳机", false),
                new("b", "扬声器", true),
                new("c", "显示器", false)
            ]
        };

        var devices = snapshot.OutputDevices;

        Assert.Equal(3, devices.Count);
        // Exactly one entry is marked, and it is the one the card is showing.
        Assert.Single(devices.Where(device => string.Equals(device.Id, snapshot.OutputDeviceId, StringComparison.OrdinalIgnoreCase)));
        Assert.Equal("扬声器", devices.Single(device => device.Id == snapshot.OutputDeviceId).Name);
    }


    // Hover and click must agree: both go through this one hit test, so a
    // highlight can never appear on a control that a click would ignore.
    [Fact]
    public void HitTestFindsEachControlAtItsOwnCentre()
    {
        var body = ControlCenterPresentation.Body(
            ControlCenterPresentation.CardWidth, ControlCenterPresentation.CardHeight, 8);

        for (var index = 0; index < 3; index++)
        {
            var toggle = ControlCenterPresentation.Toggle(body, index);
            var expected = index switch
            {
                0 => ControlCenterPresentation.ControlCenterTarget.ToggleWiFi,
                1 => ControlCenterPresentation.ControlCenterTarget.ToggleBluetooth,
                _ => ControlCenterPresentation.ControlCenterTarget.ToggleNightLight
            };
            Assert.Equal(expected, ControlCenterPresentation.TargetAt(body,
                toggle.X + toggle.Width / 2, toggle.Y + toggle.Height / 2));
        }

        var sliders = ControlCenterPresentation.SliderArea(body);
        var half = (sliders.Width - 14) / 2;
        Assert.Equal(ControlCenterPresentation.ControlCenterTarget.VolumeSlider,
            ControlCenterPresentation.TargetAt(body, sliders.X + half / 2, sliders.Y + 10));
        Assert.Equal(ControlCenterPresentation.ControlCenterTarget.BrightnessSlider,
            ControlCenterPresentation.TargetAt(body, sliders.X + half + 14 + half / 2, sliders.Y + 10));

        var media = ControlCenterPresentation.MediaArea(body);
        var output = ControlCenterPresentation.OutputRow(media);
        Assert.Equal(ControlCenterPresentation.ControlCenterTarget.OutputRow,
            ControlCenterPresentation.TargetAt(body, output.X + output.Width / 2, output.Y + output.Height / 2));

        var buttons = ControlCenterPresentation.MediaButtonRects(ControlCenterPresentation.MediaPanel(media));
        var expectedMedia = new[]
        {
            ControlCenterPresentation.ControlCenterTarget.MediaPrevious,
            ControlCenterPresentation.ControlCenterTarget.MediaPlayPause,
            ControlCenterPresentation.ControlCenterTarget.MediaNext
        };
        for (var index = 0; index < buttons.Length; index++)
        {
            Assert.Equal(expectedMedia[index], ControlCenterPresentation.TargetAt(body,
                buttons[index].X + buttons[index].Width / 2, buttons[index].Y + buttons[index].Height / 2));
        }
    }

    [Fact]
    public void MuteButtonAndVolumeTrackHaveSeparateHitTargets()
    {
        var body = ControlCenterPresentation.Body(
            ControlCenterPresentation.CardWidth, ControlCenterPresentation.CardHeight, 8);
        var button = ControlCenterPresentation.VolumeMuteButton(body);
        var sliders = ControlCenterPresentation.SliderArea(body);

        Assert.Equal(ControlCenterPresentation.ControlCenterTarget.VolumeMute,
            ControlCenterPresentation.TargetAt(body, button.X + button.Width / 2, button.Y + button.Height / 2));
        Assert.True(button.Y + button.Height < sliders.Y + 24);
        Assert.Equal(ControlCenterPresentation.ControlCenterTarget.VolumeSlider,
            ControlCenterPresentation.TargetAt(body, button.X + button.Width / 2, sliders.Y + 26));
    }

    [Fact]
    public void HitTestReportsNothingOutsideAControl()
    {
        var body = ControlCenterPresentation.Body(
            ControlCenterPresentation.CardWidth, ControlCenterPresentation.CardHeight, 8);

        // The gap between the sliders and the output row belongs to no control.
        var sliders = ControlCenterPresentation.SliderArea(body);
        Assert.Equal(ControlCenterPresentation.ControlCenterTarget.None,
            ControlCenterPresentation.TargetAt(body, sliders.X + 20, sliders.Y + sliders.Height - 2));
    }

    // The output list is anchored under its row. MonitorRectangle already returns
    // screen DIPs, so adding the card origin again doubled it and threw the menu
    // to the far side of the desktop - the bug this pins down.
    [Fact]
    public void OutputRowAnchorSitsAtTheRowsBottomEdgeInScreenCoordinates()
    {
        var body = ControlCenterPresentation.Body(
            ControlCenterPresentation.CardWidth, ControlCenterPresentation.CardHeight, 8);
        var row = ControlCenterPresentation.OutputRow(ControlCenterPresentation.MediaArea(body));
        var cardX = 80d;
        var cardY = 60d;

        // What MonitorRectangle produces: the card origin plus the local rect.
        var rowLeft = cardX + row.X;
        var rowBottom = cardY + row.Y + row.Height;

        Assert.InRange(rowLeft, cardX, cardX + ControlCenterPresentation.CardWidth);
        // Below the row's top and at most the card's bottom edge.
        Assert.True(rowBottom > cardY + row.Y);
        Assert.True(rowBottom <= cardY + ControlCenterPresentation.CardHeight);
    }
}
