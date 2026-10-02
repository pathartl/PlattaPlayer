using System.Globalization;
using System.Text.RegularExpressions;
using PlattaPlayer.Core.Models;

namespace PlattaPlayer.Core.Parsing;

/// <summary>
/// Parses LRC text: <c>[mm:ss.xx]line</c>, several leading timestamps per line, the <c>[offset:±ms]</c> header,
/// and enhanced/word-level <c>&lt;mm:ss.xx&gt;</c> tags (stripped, the line keeps its leading timestamp).
/// Other <c>[tag:value]</c> headers are ignored.
/// </summary>
public static partial class LrcParser
{
    [GeneratedRegex(@"^\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]")]
    private static partial Regex TimeTag();

    [GeneratedRegex(@"^\[offset:\s*([+-]?\d+)\s*\]", RegexOptions.IgnoreCase)]
    private static partial Regex OffsetTag();

    [GeneratedRegex(@"<\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?>")]
    private static partial Regex WordTag();

    /// <summary>True when <paramref name="text"/> contains at least one LRC line timestamp.</summary>
    public static bool LooksLikeLrc(string text)
        => text.Split('\n').Any(l => TimeTag().IsMatch(l.TrimStart()));

    /// <summary>Returns the timed lines in time order, or an empty list when none were found.</summary>
    public static IReadOnlyList<LyricLine> Parse(string text)
    {
        var offset = TimeSpan.Zero;
        var lines = new List<LyricLine>();

        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (OffsetTag().Match(line) is { Success: true } o)
            {
                // A positive offset means the lyrics should appear sooner.
                offset = TimeSpan.FromMilliseconds(-int.Parse(o.Groups[1].Value, CultureInfo.InvariantCulture));
                continue;
            }

            var times = new List<TimeSpan>();
            while (TimeTag().Match(line) is { Success: true } m)
            {
                times.Add(ToTime(m));
                line = line[m.Length..];
            }
            if (times.Count == 0) continue;

            var lyric = WordTag().Replace(line, "").Trim();
            foreach (var t in times)
                lines.Add(new LyricLine(t, lyric));
        }

        return lines
            .Select(l => l with { Time = l.Time + offset < TimeSpan.Zero ? TimeSpan.Zero : l.Time + offset })
            .OrderBy(l => l.Time)
            .ToList();
    }

    private static TimeSpan ToTime(Match m)
    {
        var minutes = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var seconds = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        var fraction = m.Groups[3].Success ? m.Groups[3].Value : "0";
        // ".5" = 500 ms, ".05" = 50 ms, ".005" = 5 ms.
        var ms = int.Parse(fraction.PadRight(3, '0'), CultureInfo.InvariantCulture);
        return new TimeSpan(0, 0, minutes, seconds, ms);
    }
}
