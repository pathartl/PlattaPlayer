using System.Text.RegularExpressions;
using PlattaPlayer.Codecs.Abstractions;

namespace PlattaPlayer.Codecs.Vgm;

/// <summary>
/// GD3 fields to and from the host's tags. The English track name, game and author are the title, album and
/// artist (the Japanese ones stand in where the English are blank, in the library only), the notes the
/// comment, and the year is the release date's (edited in place, keeping the rest of the date). Authors are listed in GD3 as "A, B" (or "A &amp; B"),
/// and in the host's multi-value form "A; B".
/// </summary>
internal static partial class VgmTagMap
{
    // Format-specific tag keys.
    public const string TitleJapaneseKey = "title_jp";
    public const string GameJapaneseKey = "game_jp";
    public const string AuthorJapaneseKey = "artist_jp";
    public const string SystemKey = "system";
    public const string SystemJapaneseKey = "system_jp";
    public const string ReleaseDateKey = "date";
    public const string RipperKey = "ripper";

    // Each editable key and its GD3 field.
    private static readonly (string Key, int Field)[] Map =
    [
        (CodecTagKeys.Title, Gd3Tag.TrackEnglish),
        (TitleJapaneseKey, Gd3Tag.TrackJapanese),
        (CodecTagKeys.Album, Gd3Tag.GameEnglish),
        (GameJapaneseKey, Gd3Tag.GameJapanese),
        (SystemKey, Gd3Tag.SystemEnglish),
        (SystemJapaneseKey, Gd3Tag.SystemJapanese),
        (CodecTagKeys.Artist, Gd3Tag.AuthorEnglish),
        (AuthorJapaneseKey, Gd3Tag.AuthorJapanese),
        (ReleaseDateKey, Gd3Tag.ReleaseDate),
        (RipperKey, Gd3Tag.Ripper),
        (CodecTagKeys.Comment, Gd3Tag.Notes),
    ];

    /// <summary>The game's name, English first.</summary>
    public static string? Game(Gd3Tag? gd3) => Either(gd3, Gd3Tag.GameEnglish, Gd3Tag.GameJapanese);

    /// <summary>The song's authors as GD3 lists them, English first.</summary>
    public static string? Author(Gd3Tag? gd3) => Either(gd3, Gd3Tag.AuthorEnglish, Gd3Tag.AuthorJapanese);

    /// <summary>The tags the Tag Editor edits: the GD3 fields as they are, and the release date's year.</summary>
    public static CodecTags ToCodecTags(Gd3Tag? gd3)
    {
        var tags = new CodecTags();
        if (gd3 is null) return tags;
        foreach (var (key, field) in Map)
            tags[key] = key == CodecTagKeys.Artist ? Authors(gd3[field]) : gd3[field];
        tags[CodecTagKeys.Year] = Year(gd3[Gd3Tag.ReleaseDate]);
        return tags;
    }

    /// <summary>The year in a release date ("1991", "1991/12/20", "Dec 1991"…), or null.</summary>
    public static string? Year(string? date) => date is not null && YearPattern().Match(date) is { Success: true } m ? m.Value : null;

    /// <summary>
    /// Writes into <paramref name="gd3"/> each field whose value in <paramref name="tags"/> differs from
    /// <paramref name="current"/> (what <see cref="ToCodecTags"/> read), so untouched fields keep their exact
    /// text. Returns whether anything changed.
    /// </summary>
    public static bool Apply(Gd3Tag gd3, CodecTags tags, CodecTags current)
    {
        var changed = false;
        foreach (var (key, field) in Map)
        {
            if (string.Equals(tags[key], current[key], StringComparison.Ordinal)) continue;
            var value = tags[key] ?? "";
            gd3[field] = key == CodecTagKeys.Artist ? string.Join(", ", VgmFolder.SplitAuthors(value)) : value;
            changed = true;
        }

        // After the date, so a year edited along with it lands in the new date.
        if (!string.Equals(tags[CodecTagKeys.Year], current[CodecTagKeys.Year], StringComparison.Ordinal))
        {
            gd3[Gd3Tag.ReleaseDate] = WithYear(gd3[Gd3Tag.ReleaseDate], tags[CodecTagKeys.Year]);
            changed = true;
        }
        return changed;
    }

    /// <summary>The date with its year replaced ("1991/06/23" → "1992/06/23"), the year alone when the date is
    /// blank, or added after a date without one. Clearing the year clears the date.</summary>
    public static string WithYear(string date, string? year)
    {
        if (string.IsNullOrWhiteSpace(year)) return "";
        year = year.Trim();
        if (string.IsNullOrWhiteSpace(date)) return year;
        return YearPattern().Match(date) is { Success: true } m
            ? date[..m.Index] + year + date[(m.Index + m.Length)..]
            : $"{date.Trim()} {year}";
    }

    /// <summary>"A; B" from "A, B".</summary>
    public static string? Authors(string? authors) =>
        VgmFolder.SplitAuthors(authors) is { Count: > 0 } list ? string.Join("; ", list) : null;

    private static string? Either(Gd3Tag? gd3, int english, int japanese) =>
        gd3 is null ? null
        : gd3[english].Length > 0 ? gd3[english]
        : gd3[japanese].Length > 0 ? gd3[japanese]
        : null;

    [GeneratedRegex(@"\b(19|20)\d\d\b")]
    private static partial Regex YearPattern();
}
