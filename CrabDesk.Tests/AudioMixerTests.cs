using CrabDesk.Core;

namespace CrabDesk.Tests;

public sealed class AudioMixerTests
{
    [Fact]
    public void SessionsFromTheSameApplicationShareOneVolumeRow()
    {
        var apps = AudioMixerPresentation.GroupApplications([
            new("browser", "Browser", "browser.exe", 30, true),
            new("BROWSER", "Browser", "browser.exe", 65, false),
            new("player", "Player", "player.exe", 80, true)
        ]);
        Assert.Equal(2, apps.Count);
        var browser = Assert.Single(apps, app => app.Name == "Browser");
        Assert.Equal(65, browser.VolumePercent);
        Assert.False(browser.Muted);
        Assert.True(Assert.Single(apps, app => app.Name == "Player").Muted);
    }

    [Fact]
    public void MixerEntryIsSeparateFromMuteAndTheVolumeTrack()
    {
        var body = WidgetMonitorPresentation.Body(372, 292, 8);
        var mixer = ControlCenterPresentation.VolumeMixerButton(body);
        var mute = ControlCenterPresentation.VolumeMuteButton(body);
        Assert.True(mixer.X >= mute.X + mute.Width);
        Assert.Equal(ControlCenterPresentation.ControlCenterTarget.VolumeMixer,
            ControlCenterPresentation.TargetAt(body, mixer.X + mixer.Width / 2, mixer.Y + mixer.Height / 2));
        Assert.Equal(ControlCenterPresentation.ControlCenterTarget.VolumeSlider,
            ControlCenterPresentation.TargetAt(body, mixer.X + mixer.Width / 2, ControlCenterPresentation.SliderArea(body).Y + 26));
    }
}
