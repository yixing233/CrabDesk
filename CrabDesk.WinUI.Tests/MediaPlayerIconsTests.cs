using System.Drawing;
using System.Drawing.Imaging;
using CrabDesk.Runtime;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class MediaPlayerIconsTests
{
    [Theory]
    [InlineData(@"D:\Program Files\Tencent\QQMusic\QQMusic.exe", "qqmusic")]
    [InlineData("CLOUDMUSIC.EXE", "netease")]
    [InlineData("KuGou.exe", "kugou")]
    [InlineData("KwMusic.exe", "kuwo")]
    [InlineData("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", "spotify")]
    [InlineData("AppleInc.AppleMusicWin_nzyj5cx40ttqa!App", "applemusic")]
    [InlineData("chrome.exe", null)]
    [InlineData("QQMusicHelper.exe", null)]
    [InlineData(null, null)]
    public void RecognizesExecutablePathsAndPackagedPlayerIdentities(string? source, string? expected) =>
        Assert.Equal(expected, MediaPlayerIcons.Resolve(source));

    [Fact]
    public void BuiltInPlayerIconsRenderInTheLyricsHeaderAtTheirActualSize()
    {
        string[] sources = ["QQMusic.exe", "cloudmusic.exe", "KuGou.exe", "KwMusic.exe", "Spotify.exe", "AppleMusic.exe"];
        using var preview = new Bitmap(480, 100, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(preview))
        {
            graphics.Clear(Color.FromArgb(38, 50, 73));
            for (var index = 0; index < sources.Length; index++)
            {
                using var icon = new Bitmap(16, 16, PixelFormat.Format32bppPArgb);
                using (var iconGraphics = Graphics.FromImage(icon))
                    MediaPlayerIcons.Draw(iconGraphics, sources[index], new(0, 0, 16, 16), Color.White);
                var painted = 0;
                for (var y = 0; y < 16; y++)
                for (var x = 0; x < 16; x++)
                    if (icon.GetPixel(x, y).A > 0) painted++;
                Assert.True(painted > 50);
                MediaPlayerIcons.Draw(graphics, sources[index], new(index * 80 + 8, 8, 48, 48), Color.White);
                graphics.DrawImage(icon, index * 80 + 24, 72);
            }
        }
        var directory = Environment.GetEnvironmentVariable("CRABDESK_LYRICS_VISUAL_OUTPUT") ?? Path.GetTempPath();
        Directory.CreateDirectory(directory);
        preview.Save(Path.Combine(directory, "lyrics-player-icons.png"));
    }
}
