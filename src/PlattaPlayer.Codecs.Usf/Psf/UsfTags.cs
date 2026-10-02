using System.Globalization;
using System.Text.RegularExpressions;
using PlattaPlayer.Codecs.Abstractions;

namespace PlattaPlayer.Codecs.Usf.Psf;

/// <summary>
/// The PSF tag names USF rips use, and how they map onto the library's fields. PSF has no album as such: the
/// game is the album. It has no album artist or publisher either, so unless a player has written an
/// "album artist" tag, the company in the copyright line ("1997 Nintendo") stands in for it, which keeps a
/// game's songs one album when their composers differ. The year falls back to the copyright line's year too.
/// </summary>
internal static partial class UsfTags
{
    public const string Title = "title";
    public const string Artist = "artist";
    public const string Game = "game";
    public const string Year = "year";
    public const string Genre = "genre";
    public const string Comment = "comment";
    public const string Copyright = "copyright";
    public const string UsfBy = "usfby";
    public const string PsfBy = "psfby";
    public const string Length = "length";
    public const string Fade = "fade";
    public const string Volume = "volume";
    // Not in the PSF spec, but written by foobar2000 and others.
    public const string Track = "track";
    public const string Disc = "disc";
    public const string AlbumArtist = "album artist";
    public const string AlbumArtistAlt = "albumartist";

    public const string EnableCompare = "_enablecompare";
    public const string EnableFifoFull = "_enablefifofull";

    /// <summary>The library's view of a file's tags (keys as in <see cref="UsfCodecPlugin"/>'s fields).</summary>
    public static CodecTags ToCodecTags(PsfTags psf) => new()
    {
        [CodecTagKeys.Title] = psf[Title],
        [CodecTagKeys.Artist] = psf[Artist],
        [CodecTagKeys.Album] = psf[Game],
        [CodecTagKeys.AlbumArtist] = psf[AlbumArtist] ?? psf[AlbumArtistAlt] ?? CopyrightHolder(psf[Copyright]),
        [CodecTagKeys.Year] = LeadingYear(psf[Year]) ?? LeadingYear(CopyrightYear(psf[Copyright])),
        [CodecTagKeys.Genre] = psf[Genre],
        [CodecTagKeys.Track] = LeadingNumber(psf[Track]),
        [CodecTagKeys.Disc] = LeadingNumber(psf[Disc]),
        [CodecTagKeys.Comment] = psf[Comment],
        [UsfCodecPlugin.CopyrightKey] = psf[Copyright],
        [UsfCodecPlugin.RipperKey] = psf[UsfBy] ?? psf[PsfBy],
        [UsfCodecPlugin.LengthKey] = FormatSeconds(ParseSeconds(psf[Length])),
        [UsfCodecPlugin.FadeKey] = FormatSeconds(ParseSeconds(psf[Fade]), alwaysSeconds: true),
    };

    /// <summary>
    /// Applies the edited <paramref name="tags"/> to <paramref name="psf"/>. Only fields whose value differs
    /// from what <see cref="ToCodecTags"/> shows for the file are written, so a field the user left alone
    /// keeps its original text (a "1997-08-21" year, a "3/12" track, a derived album artist that was never a
    /// tag, an "m:ss.fff" length).
    /// </summary>
    public static void Apply(PsfTags psf, CodecTags tags)
    {
        var current = ToCodecTags(psf);
        bool Changed(string key) => !string.Equals(current[key], tags[key], StringComparison.Ordinal);

        if (Changed(CodecTagKeys.Title)) psf.Set(Title, tags[CodecTagKeys.Title]);
        if (Changed(CodecTagKeys.Artist)) psf.Set(Artist, tags[CodecTagKeys.Artist]);
        if (Changed(CodecTagKeys.Album)) psf.Set(Game, tags[CodecTagKeys.Album]);
        if (Changed(CodecTagKeys.AlbumArtist))
        {
            psf.Remove(AlbumArtistAlt);
            psf.Set(AlbumArtist, tags[CodecTagKeys.AlbumArtist]);
        }
        if (Changed(CodecTagKeys.Year)) psf.Set(Year, tags[CodecTagKeys.Year]);
        if (Changed(CodecTagKeys.Genre)) psf.Set(Genre, tags[CodecTagKeys.Genre]);
        if (Changed(CodecTagKeys.Track)) psf.Set(Track, tags[CodecTagKeys.Track]);
        if (Changed(CodecTagKeys.Disc)) psf.Set(Disc, tags[CodecTagKeys.Disc]);
        if (Changed(CodecTagKeys.Comment)) psf.Set(Comment, tags[CodecTagKeys.Comment]);
        if (Changed(UsfCodecPlugin.CopyrightKey)) psf.Set(Copyright, tags[UsfCodecPlugin.CopyrightKey]);
        if (Changed(UsfCodecPlugin.RipperKey))
        {
            psf.Remove(PsfBy);
            psf.Set(UsfBy, tags[UsfCodecPlugin.RipperKey]);
        }
        if (Changed(UsfCodecPlugin.LengthKey))
            psf.Set(Length, FormatSeconds(ParseSeconds(tags[UsfCodecPlugin.LengthKey]) is > 0 and var s ? s : null));
        if (Changed(UsfCodecPlugin.FadeKey))
            psf.Set(Fade, FormatSeconds(ParseSeconds(tags[UsfCodecPlugin.FadeKey]) is >= 0 and var s ? s : null, alwaysSeconds: true));

        // Everything is written back as UTF-8.
        psf.Set(PsfTags.Utf8Tag, "1");
    }

    /// <summary>The "volume" tag: a gain to apply, 1 when absent or invalid.</summary>
    public static double VolumeOf(PsfTags psf) =>
        double.TryParse(psf[Volume]?.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
        && v > 0 && double.IsFinite(v)
            ? v
            : 1;

    /// <summary>"Nintendo" from "1997 Nintendo", "(C) 1996-1997 Rare / Nintendo", "©1998 Konami".</summary>
    internal static string? CopyrightHolder(string? copyright)
    {
        if (string.IsNullOrWhiteSpace(copyright)) return null;
        var holder = CopyrightPrefix().Replace(copyright.Trim(), "").Trim(' ', ',', '.', '-');
        return holder.Length > 0 ? holder : null;
    }

    private static string? CopyrightYear(string? copyright) =>
        copyright is null ? null : YearPattern().Match(copyright) is { Success: true } m ? m.Value : null;

    private static string? LeadingYear(string? text) =>
        text is not null && YearPattern().Match(text) is { Success: true, Index: var i } m && text[..i].Trim().Length == 0
            ? m.Value
            : null;

    // "3/12" → "3"; blank or zero → null.
    private static string? LeadingNumber(string? text)
    {
        if (text is null) return null;
        var digits = new string(text.Trim().TakeWhile(char.IsAsciiDigit).ToArray());
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0
            ? n.ToString(CultureInfo.InvariantCulture)
            : null;
    }

    /// <summary>"2:35" (or "2:35.5"); with <paramref name="alwaysSeconds"/>, "10" / "10.5".</summary>
    internal static string? FormatSeconds(double? value, bool alwaysSeconds = false)
    {
        if (value is not { } seconds || seconds < 0) return null;
        seconds = Math.Round(seconds, 3);
        if (alwaysSeconds) return seconds.ToString("0.###", CultureInfo.InvariantCulture);
        var minutes = (int)(seconds / 60);
        var rest = seconds - minutes * 60;
        return $"{minutes}:{rest.ToString("00.###", CultureInfo.InvariantCulture)}";
    }

    /// <summary>Parses PSF times: seconds ("155", "10.5", "10,5") or m:ss / h:mm:ss with optional fractions.</summary>
    internal static double? ParseSeconds(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var total = 0.0;
        foreach (var part in text.Trim().Replace(',', '.').Split(':'))
        {
            if (!double.TryParse(part, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n)) return null;
            total = total * 60 + n;
        }
        return total;
    }

    [GeneratedRegex(@"\b(19|20)\d\d\b")]
    private static partial Regex YearPattern();

    // Leading "(C)", "©", "Copyright", "(P)" and years / year ranges, in any mix.
    [GeneratedRegex(@"^(?:\s*(?:\(c\)|\(p\)|©|℗|copyright\b|(?:19|20)\d\d(?:\s*[-–/,]\s*(?:19|20)?\d\d)?)[\s,.]*)+", RegexOptions.IgnoreCase)]
    private static partial Regex CopyrightPrefix();
}
