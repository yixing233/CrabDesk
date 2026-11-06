using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Lyricify.Lyrics.Helpers;
using Lyricify.Lyrics.Models;

namespace CrabDesk.Core;

public static partial class WidgetLyricsParser
{
    private const int MaximumInputLength = 2_000_000;

    public static WidgetLyricsDocument? Parse(string raw, string? format = null, string source = "本地歌词")
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > MaximumInputLength) return null;
        try
        {
            raw = raw.Trim().TrimStart('\uFEFF');
            var type = !string.IsNullOrWhiteSpace(format) &&
                TypeHelper.TryParseRawType(format.Trim().TrimStart('.'), out var specified)
                ? specified : TypeHelper.GetLyricsTypes(raw);

            if (type == LyricsRawTypes.QrcFull || raw.Contains("LyricContent=", StringComparison.Ordinal))
            {
                using var reader = XmlReader.Create(new StringReader(raw), new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null
                });
                var xml = XDocument.Load(reader);
                raw = xml.Descendants().Attributes("LyricContent").FirstOrDefault()?.Value ?? "";
                type = LyricsRawTypes.Qrc;
            }
            else if (type == LyricsRawTypes.YrcFull)
            {
                using var json = JsonDocument.Parse(raw);
                raw = json.RootElement.GetProperty("yrc").GetProperty("lyric").GetString() ?? "";
                type = LyricsRawTypes.Yrc;
            }

            // The library's LRC parser does not handle signed/global offsets or enhanced LRC.
            if (type == LyricsRawTypes.Lrc || (type == LyricsRawTypes.Unknown && LineTimestamp().IsMatch(raw)))
                return ParseLrc(raw, source);

            var parsed = ParseHelper.ParseLyrics(raw, type);
            if (parsed?.Lines is null) return null;
            var lines = parsed.Lines.Where(line => line.StartTime.HasValue).Select(line =>
            {
                var words = line is SyllableLineInfo syllables
                    ? syllables.Syllables.Select(word => new WidgetLyricWord(word.Text, word.StartTime, word.EndTime)).ToArray()
                    : null;
                return new WidgetLyricLine(line.Text, line.StartTime!.Value, line.EndTime ?? double.NaN, words);
            }).ToArray();
            return Normalize(new WidgetLyricsDocument(lines, source));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Imported files and remote lyrics are optional; malformed formats must not stop the widget.
            return null;
        }
    }

    internal static WidgetLyricsDocument? Normalize(WidgetLyricsDocument? document, double durationMilliseconds = 0)
    {
        if (document is null) return null;
        if (document.IsInstrumental) return document with { Lines = Array.Empty<WidgetLyricLine>() };
        var candidates = document.Lines
            .Where(line => double.IsFinite(line.StartMilliseconds))
            .Select(line => line with { StartMilliseconds = Math.Max(0, line.StartMilliseconds) })
            .OrderBy(line => line.StartMilliseconds).ToArray();
        var result = new List<WidgetLyricLine>();
        for (var index = 0; index < candidates.Length; index++)
        {
            var line = candidates[index];
            if (string.IsNullOrWhiteSpace(line.Text)) continue;
            var nextStart = candidates.Skip(index + 1)
                .FirstOrDefault(next => next.StartMilliseconds > line.StartMilliseconds)?.StartMilliseconds;
            var end = double.IsFinite(line.EndMilliseconds) && line.EndMilliseconds > line.StartMilliseconds
                ? line.EndMilliseconds : nextStart ?? line.StartMilliseconds + 6000;
            if (durationMilliseconds > line.StartMilliseconds) end = Math.Min(end, durationMilliseconds);

            IReadOnlyList<WidgetLyricWord>? words = null;
            if (line.Words is { Count: > 0 } supplied &&
                supplied.All(word => double.IsFinite(word.StartMilliseconds) && double.IsFinite(word.EndMilliseconds) &&
                    word.EndMilliseconds >= word.StartMilliseconds) &&
                string.Concat(supplied.Select(word => word.Text)).Trim() == line.Text.Trim() &&
                supplied.Zip(supplied.Skip(1)).All(pair => pair.First.StartMilliseconds <= pair.Second.StartMilliseconds))
            {
                var normalized = supplied.Select(word => word with
                {
                    StartMilliseconds = Math.Max(0, word.StartMilliseconds),
                    EndMilliseconds = Math.Max(0, word.EndMilliseconds)
                }).ToArray();
                normalized[0] = normalized[0] with { Text = normalized[0].Text.TrimStart() };
                normalized[^1] = normalized[^1] with { Text = normalized[^1].Text.TrimEnd() };
                words = Array.AsReadOnly(normalized);
                end = Math.Max(end, normalized[^1].EndMilliseconds);
            }
            result.Add(new WidgetLyricLine(line.Text.Trim(), line.StartMilliseconds, end, words));
        }
        return result.Count == 0 ? null : document with { Lines = result.AsReadOnly() };
    }

    private static WidgetLyricsDocument? ParseLrc(string raw, string source)
    {
        var offsetMatch = Offset().Matches(raw).LastOrDefault();
        var offset = offsetMatch is not null &&
            double.TryParse(offsetMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) &&
            double.IsFinite(value) ? Math.Clamp(value, -86_400_000, 86_400_000) : 0;
        var lines = new List<WidgetLyricLine>();
        foreach (var rawLine in raw.Split('\n'))
        {
            var remaining = rawLine.Trim();
            var timestamps = new List<double>();
            while (LineTimestamp().Match(remaining) is { Success: true, Index: 0 } match)
            {
                if (TryTimestamp(match.Groups[1].Value, out var timestamp)) timestamps.Add(timestamp);
                remaining = remaining[match.Length..];
            }
            if (timestamps.Count == 0) continue;
            var text = WordTimestamp().Replace(remaining, "").Trim();
            var marks = WordTimestamp().Matches(remaining);
            foreach (var timestamp in timestamps)
            {
                List<WidgetLyricWord>? words = null;
                // A closing timestamp is necessary to retain a real end time for the last word.
                if (marks.Count > 1 && string.IsNullOrWhiteSpace(remaining[..marks[0].Index]) &&
                    string.IsNullOrWhiteSpace(remaining[(marks[^1].Index + marks[^1].Length)..]))
                {
                    words = new();
                    for (var i = 0; i + 1 < marks.Count; i++)
                    {
                        if (!TryTimestamp(marks[i].Groups[1].Value, out var start) ||
                            !TryTimestamp(marks[i + 1].Groups[1].Value, out var end)) continue;
                        var word = remaining[(marks[i].Index + marks[i].Length)..marks[i + 1].Index];
                        words.Add(new WidgetLyricWord(word, start + timestamp - timestamps[0] - offset,
                            end + timestamp - timestamps[0] - offset));
                    }
                }
                lines.Add(new WidgetLyricLine(text, timestamp - offset,
                    words is { Count: > 0 } ? words[^1].EndMilliseconds : double.NaN, words));
            }
        }
        return Normalize(new WidgetLyricsDocument(lines, source));
    }

    private static bool TryTimestamp(string text, out double milliseconds)
    {
        var parts = text.Split(':');
        milliseconds = 0;
        if (parts.Length != 2 || !double.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) ||
            !double.TryParse(parts[1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds) ||
            seconds >= 60) return false;
        milliseconds = (minutes * 60 + seconds) * 1000;
        return double.IsFinite(milliseconds) && milliseconds <= 86_400_000;
    }

    [GeneratedRegex(@"\[(\d{1,6}:\d{2}(?:\.\d{1,3})?)\]", RegexOptions.CultureInvariant)]
    private static partial Regex LineTimestamp();
    [GeneratedRegex(@"<(\d{1,6}:\d{2}(?:\.\d{1,3})?)>", RegexOptions.CultureInvariant)]
    private static partial Regex WordTimestamp();
    [GeneratedRegex(@"\[offset:\s*([+-]?\d+)\s*\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Offset();
}
