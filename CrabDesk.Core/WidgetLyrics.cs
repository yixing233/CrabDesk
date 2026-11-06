namespace CrabDesk.Core;

public enum WidgetLyricsFontStyle { Normal, Italic }

public sealed record WidgetLyricWord(string Text, double StartMilliseconds, double EndMilliseconds);

public sealed record WidgetLyricLine(string Text, double StartMilliseconds, double EndMilliseconds,
    IReadOnlyList<WidgetLyricWord>? Words = null);

public sealed record WidgetLyricsDocument(IReadOnlyList<WidgetLyricLine> Lines, string Source, bool IsInstrumental = false)
{
    public bool HasWordTiming => Lines.Any(line => line.Words is { Count: > 0 });
}

public sealed record WidgetLyricsSnapshot(ControlCenterMedia? Media, WidgetLyricsDocument? Document,
    bool IsLoading = false, string Error = "")
{
    public static WidgetLyricsSnapshot Empty { get; } = new(null, null);
}

public interface IWidgetLyricsProvider
{
    Task<WidgetLyricsDocument?> FetchAsync(ControlCenterMedia media, CancellationToken token = default);
}

public static class WidgetLyricsSettings
{
    public static string NormalizeFontFamily(string? value) =>
        string.IsNullOrWhiteSpace(value) ? BoxAppearance.DefaultFontFamily : value.Trim();
    public static int NormalizeFontWeight(int value) => value is >= 100 and <= 900
        ? (int)Math.Round(value / 100d, MidpointRounding.AwayFromZero) * 100 : 600;
    public static WidgetLyricsFontStyle NormalizeFontStyle(WidgetLyricsFontStyle value) =>
        value is WidgetLyricsFontStyle.Normal or WidgetLyricsFontStyle.Italic ? value : WidgetLyricsFontStyle.Normal;
    public static string FontWeightLabel(int value) => NormalizeFontWeight(value) switch
    {
        100 => "纤细（100）", 200 => "特细（200）", 300 => "细体（300）", 400 => "常规（400）",
        500 => "中等（500）", 600 => "半粗（600）", 700 => "粗体（700）", 800 => "特粗（800）", _ => "黑体（900）"
    };
    public static int NormalizeLineCount(int value) => Math.Clamp(value, 1, 9);
    public static double NormalizeFontSize(double value) => double.IsFinite(value) ? Math.Clamp(value, 14, 48) : 24;
    public static int NormalizeOffset(int value) => Math.Clamp(value, -10000, 10000);
    public static string TrackKey(ControlCenterMedia media) => string.Join("\n", media.Title.Trim(), media.Artist.Trim(), media.Album.Trim());
}
