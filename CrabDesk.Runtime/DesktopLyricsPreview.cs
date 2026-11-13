using System.Drawing;
using System.Drawing.Imaging;
using CrabDesk.Core;

namespace CrabDesk.Runtime;

public static class DesktopLyricsPreview
{
    public static byte[] Render(DesktopWidget widget, Color ink, Color accent, double width, double height)
    {
        using var bitmap = new Bitmap(Math.Max(1, (int)width), Math.Max(1, (int)height), PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(bitmap);
        using var visual = new DesktopLyricsVisual();
        var now = DateTimeOffset.Now;
        var snapshot = widget.Lyrics ?? Sample(now);
        if (widget.LyricsLightText is { } light) ink = light ? Color.White : Color.FromArgb(31, 35, 41);
        // The preview card reproduces the desktop header, including the player
        // brand logo drawn when a known player session is active.
        MediaPlayerIcons.Draw(graphics, snapshot.Media?.SourceAppId, new(12, 10, 16, 16), Color.FromArgb(165, ink));
        visual.Draw(graphics, snapshot, new(0, 0, bitmap.Width, bitmap.Height), ink, accent,
            widget.LyricsLineSpacing, widget.LyricsFontSize, widget.LyricsOffsetMilliseconds,
            widget.LyricsWordHighlight, false, 1, now, widget.LyricsFontFamily, widget.LyricsFontWeight, widget.LyricsFontStyle);
        using var output = new MemoryStream();
        bitmap.Save(output, ImageFormat.Png);
        return output.ToArray();
    }

    private static WidgetLyricsSnapshot Sample(DateTimeOffset now)
    {
        string[] text = ["把风声写进今天", "让旋律慢慢靠近", "这一刻跟着光前行", "听见心里的回声", "下一句还有新的风景"];
        var lines = text.Select((line, index) => new WidgetLyricLine(line, index * 5000, (index + 1) * 5000,
            line.Select((character, word) => new WidgetLyricWord(character.ToString(), index * 5000 + word * 5000d / line.Length,
                index * 5000 + (word + 1) * 5000d / line.Length)).ToArray())).ToArray();
        var media = new ControlCenterMedia("桌面歌词预览", "", "", false, false, false, false)
        { Position = TimeSpan.FromMilliseconds(12200), Duration = TimeSpan.FromSeconds(25), CapturedAt = now };
        return new(media, new(lines, "预览"));
    }
}
