using System.Globalization;
using PlattaPlayer.Codecs.Abstractions;
using PlattaPlayer.Codecs.Spc.Emulation;

namespace PlattaPlayer.Codecs.Spc;

/// <summary>
/// SNES music (.spc): plays the snapshot on a cycle-accurate S-SMP + S-DSP emulation and reads/writes its
/// ID666 and xid6 tags. ID666 has no album or genre as such; the game title stands for the album, the
/// publisher for the album artist, the copyright year for the year, and the official soundtrack's disc and
/// track numbers for disc and track.
/// </summary>
public sealed class SpcCodecPlugin : ICodecPlugin
{
    /// <summary>Played when the file has no length tag, as most SPC players do.</summary>
    public const double DefaultPlaySeconds = 180;

    /// <summary>Faded over when the file gives no fade length.</summary>
    public const double DefaultFadeSeconds = 10;

    // Format-specific tag keys.
    public const string SoundtrackKey = "ost";
    public const string DumperKey = "dumper";
    public const string DumpDateKey = "dumpdate";
    public const string LengthKey = "length";
    public const string FadeKey = "fade";

    private static readonly CodecTagField[] Fields =
    [
        new(CodecTagKeys.Title, "Song title", MaxLength: 255),
        new(CodecTagKeys.Artist, "Artist", MaxLength: 255),
        new(CodecTagKeys.Album, "Game", MaxLength: 255),
        new(CodecTagKeys.AlbumArtist, "Publisher", MaxLength: 255,
            Hint: "Used as the album artist, so a game's songs stay one album when their composers differ."),
        new(CodecTagKeys.Year, "Copyright year", CodecTagFieldKind.Number),
        new(CodecTagKeys.Track, "Soundtrack track", CodecTagFieldKind.Number),
        new(CodecTagKeys.Disc, "Soundtrack disc", CodecTagFieldKind.Number),
        new(CodecTagKeys.Comment, "Comments", MaxLength: 255),
        new(SoundtrackKey, "Soundtrack title", MaxLength: 255, Hint: "The official soundtrack album the song appears on."),
        new(DumperKey, "Dumped by", MaxLength: 255),
        new(DumpDateKey, "Dump date", Hint: "As YYYY-MM-DD."),
        new(LengthKey, "Length", CodecTagFieldKind.Duration,
            Hint: $"Play time before the fade, as m:ss. Blank plays {DefaultPlaySeconds / 60:0} minutes."),
        new(FadeKey, "Fade", CodecTagFieldKind.Duration, Hint: $"Fade-out length in seconds. Blank fades for {DefaultFadeSeconds:0} seconds."),
    ];

    public string Id => "spc";
    public string DisplayName => "SNES SPC (SPC700 emulation)";
    public IReadOnlyCollection<string> Extensions { get; } = [".spc"];
    public IReadOnlyList<CodecTagField> TagFields => Fields;

    public CodecFileInfo? ReadInfo(string path)
    {
        if (SpcFile.TryLoad(path) is not { } file) return null;
        var tag = Id666Tag.Read(file.Data);
        var (play, fade) = PlayTime(tag);
        return new CodecFileInfo
        {
            Tags = ToTags(tag),
            Duration = TimeSpan.FromSeconds(play + fade),
            SampleRate = Apu.SampleRate,
            BitsPerSample = 16,
        };
    }

    public ICodecDecoder Open(string path)
    {
        var file = SpcFile.TryLoad(path) ?? throw new InvalidDataException($"{Path.GetFileName(path)} is not an SPC file.");
        var (play, fade) = PlayTime(Id666Tag.Read(file.Data));
        return new SpcDecoder(file, play, fade);
    }

    public CodecTags ReadTags(string path) =>
        SpcFile.TryLoad(path) is { } file ? ToTags(Id666Tag.Read(file.Data)) : new CodecTags();

    public void WriteTags(string path, CodecTags tags)
    {
        var bytes = File.ReadAllBytes(path);
        if (SpcFile.TryParse(bytes) is null) throw new InvalidDataException($"{Path.GetFileName(path)} is not an SPC file.");

        var tag = Id666Tag.Read(bytes);
        tag.Song = tags[CodecTagKeys.Title];
        tag.Artist = tags[CodecTagKeys.Artist];
        tag.Game = tags[CodecTagKeys.Album];
        tag.Publisher = tags[CodecTagKeys.AlbumArtist];
        tag.CopyrightYear = tags.GetNumber(CodecTagKeys.Year);
        tag.Comment = tags[CodecTagKeys.Comment];
        tag.OstTitle = tags[SoundtrackKey];
        tag.Dumper = tags[DumperKey];

        var track = tags.GetNumber(CodecTagKeys.Track);
        if (track != tag.OstTrack) tag.OstTrackSuffix = null;
        tag.OstTrack = track;
        tag.OstDisc = tags.GetNumber(CodecTagKeys.Disc);

        var date = tags[DumpDateKey];
        if (date != FormatDate(tag))
        {
            tag.DumpDate = null;
            tag.DumpDateText = null;
            if (date is not null)
            {
                if (DateOnly.TryParseExact(date, ["yyyy-MM-dd", "yyyy/MM/dd", "MM/dd/yyyy", "yyyyMMdd"],
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                    tag.DumpDate = parsed;
                else
                    tag.DumpDateText = date;
            }
        }

        // The timing is only rewritten when it was edited, so untouched xid6 intro/loop/end values survive.
        var length = tags[LengthKey];
        if (length != FormatSeconds(tag.PlaySeconds))
        {
            tag.IntroTicks = tag.LoopTicks = tag.EndTicks = tag.LoopCount = null;
            tag.LengthSeconds = ParseSeconds(length) is > 0 and var seconds ? seconds : null;
        }

        var fade = tags[FadeKey];
        if (fade != FormatSeconds(tag.FadeSeconds, alwaysSeconds: true))
            tag.FadeSeconds = ParseSeconds(fade) is >= 0 and var seconds ? seconds : null;

        // Write beside the file, then swap it in, so a failure can't leave a half-written SPC.
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, tag.WriteTo(bytes));
        File.Move(temp, path, overwrite: true);
    }

    private static (double Play, double Fade) PlayTime(Id666Tag tag) =>
        (tag.PlaySeconds ?? DefaultPlaySeconds, tag.FadeSeconds ?? DefaultFadeSeconds);

    private static CodecTags ToTags(Id666Tag tag)
    {
        var tags = new CodecTags
        {
            [CodecTagKeys.Title] = tag.Song,
            [CodecTagKeys.Artist] = tag.Artist,
            [CodecTagKeys.Album] = tag.Game,
            [CodecTagKeys.AlbumArtist] = tag.Publisher,
            [CodecTagKeys.Year] = tag.CopyrightYear?.ToString(CultureInfo.InvariantCulture),
            [CodecTagKeys.Track] = tag.OstTrack?.ToString(CultureInfo.InvariantCulture),
            [CodecTagKeys.Disc] = tag.OstDisc?.ToString(CultureInfo.InvariantCulture),
            [CodecTagKeys.Comment] = tag.Comment,
            [SoundtrackKey] = tag.OstTitle,
            [DumperKey] = tag.Dumper,
            [DumpDateKey] = FormatDate(tag),
            [LengthKey] = FormatSeconds(tag.PlaySeconds),
            [FadeKey] = FormatSeconds(tag.FadeSeconds, alwaysSeconds: true),
        };
        return tags;
    }

    private static string? FormatDate(Id666Tag tag) =>
        tag.DumpDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? tag.DumpDateText;

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

    /// <summary>Parses seconds ("155", "10.5") or m:ss / h:mm:ss with optional fractions.</summary>
    internal static double? ParseSeconds(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var total = 0.0;
        foreach (var part in text.Trim().Split(':'))
        {
            if (!double.TryParse(part, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n)) return null;
            total = total * 60 + n;
        }
        return total;
    }
}
