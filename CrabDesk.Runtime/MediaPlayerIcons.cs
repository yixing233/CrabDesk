using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;

namespace CrabDesk.Runtime;

public static class MediaPlayerIcons
{
    private static readonly Lazy<Dictionary<string, Bitmap>> Icons = new(LoadIcons);
    private static readonly Lazy<Dictionary<string, byte[]>> IconPngBytes = new(LoadIconPngBytes);

    public static string? Resolve(string? sourceAppId)
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

    /// <summary>
    /// The PNG bytes for a player's brand logo, or null when the session is not
    /// a known player. WinUI previews decode the same asset the desktop runtime
    /// draws, so both sides show one source-of-truth logo.
    /// </summary>
    public static byte[]? TryGetLogoBytes(string? sourceAppId) =>
        Resolve(sourceAppId) is { } player && IconPngBytes.Value.TryGetValue(player, out var bytes) ? bytes : null;

    public static void Draw(Graphics graphics, string? sourceAppId, RectangleF bounds, Color fallbackColor)
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

    private static Dictionary<string, byte[]> LoadIconPngBytes()
    {
        var bytes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var player in new[] { "qqmusic", "netease", "kugou", "kuwo", "spotify", "applemusic" })
        {
            using var stream = typeof(MediaPlayerIcons).Assembly.GetManifestResourceStream($"CrabDesk.Runtime.Assets.MediaPlayers.{player}.png")
                ?? throw new InvalidOperationException($"Missing embedded media player icon: {player}");
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            bytes.Add(player, memory.ToArray());
        }
        return bytes;
    }
}
