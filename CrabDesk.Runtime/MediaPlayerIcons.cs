using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace CrabDesk.Runtime;

internal static class MediaPlayerIcons
{
    private static readonly Lazy<Dictionary<string, Bitmap>> Icons = new(LoadIcons);

    internal static string? Resolve(string? sourceAppId)
    {
        if (string.IsNullOrWhiteSpace(sourceAppId)) return null;
        var application = sourceAppId.Trim().Replace('\\', '/');
        application = application[(application.LastIndexOf('/') + 1)..];
        // Packaged players report PackageFamilyName!Application rather than an executable.
        application = application.Split(new[] { '!', '_' })[0].ToLowerInvariant();
        return application switch
        {
            "qqmusic" or "qqmusic.exe" or "tencent.qqmusic" => "qqmusic",
            "cloudmusic" or "cloudmusic.exe" or "netease.cloudmusic" or "neteasecloudmusic" => "netease",
            "kugou" or "kugou.exe" or "kugou.kugoumusic" => "kugou",
            "kwmusic" or "kwmusic.exe" or "kuwomusic" or "kuwomusic.exe" or "kuwo.kuwomusic" => "kuwo",
            "spotify" or "spotify.exe" or "spotifyab.spotifymusic" => "spotify",
            "applemusic" or "applemusic.exe" or "appleinc.applemusicwin" => "applemusic",
            _ => null
        };
    }

    internal static void Draw(Graphics graphics, string? sourceAppId, RectangleF bounds, Color fallbackColor)
    {
        var player = Resolve(sourceAppId);
        if (player is null)
        {
            WidgetColorIcons.Draw(graphics, "content-music", bounds);
            return;
        }
        var state = graphics.Save();
        try
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(Icons.Value[player], bounds);
        }
        finally { graphics.Restore(state); }
    }

    private static Dictionary<string, Bitmap> LoadIcons()
    {
        var icons = new Dictionary<string, Bitmap>(StringComparer.Ordinal);
        foreach (var player in new[] { "qqmusic", "netease", "kugou", "kuwo", "spotify", "applemusic" })
        {
            using var stream = typeof(MediaPlayerIcons).Assembly.GetManifestResourceStream($"CrabDesk.Runtime.Assets.MediaPlayers.{player}.png")
                ?? throw new InvalidOperationException($"Missing embedded media player icon: {player}");
            using var original = Image.FromStream(stream);
            icons.Add(player, new Bitmap(original));
        }
        return icons;
    }
}
