using PlattaPlayer.Codecs.Abstractions;
using PlattaPlayer.Codecs.Gsf.Emulation;
using PlattaPlayer.Codecs.Psf;

namespace PlattaPlayer.Codecs.Gsf;

/// <summary>
/// Game Boy Advance music (.gsf, .minigsf): plays the rip on mGBA, an emulated GBA (ARM7TDMI CPU, timers,
/// DMA and the sound unit) running the game's own sound code, and reads/writes its PSF tags. A miniGSF needs
/// the .gsflib it names beside it; libraries are not offered as songs themselves.
/// </summary>
public sealed class GsfCodecPlugin : ICodecPlugin
{
    /// <summary>Played when the file has no length tag, as most PSF players do.</summary>
    public const double DefaultPlaySeconds = 180;

    /// <summary>Faded over when the file gives no fade length.</summary>
    public const double DefaultFadeSeconds = 10;

    // Format-specific tag keys.
    public const string CopyrightKey = PsfTagMap.CopyrightKey;
    public const string RipperKey = PsfTagMap.RipperKey;
    public const string LengthKey = PsfTagMap.LengthKey;
    public const string FadeKey = PsfTagMap.FadeKey;

    /// <summary>The PSF tag naming who ripped a GSF.</summary>
    internal const string GsfBy = "gsfby";

    private static readonly CodecTagField[] Fields =
    [
        new(CodecTagKeys.Title, "Title"),
        new(CodecTagKeys.Artist, "Artist"),
        new(CodecTagKeys.Album, "Game"),
        new(CodecTagKeys.AlbumArtist, "Album artist",
            Hint: "Blank uses the company in the copyright line, so a game's songs stay one album when their composers differ."),
        new(CodecTagKeys.Year, "Year", CodecTagFieldKind.Number),
        new(CodecTagKeys.Genre, "Genre"),
        new(CodecTagKeys.Track, "Track", CodecTagFieldKind.Number),
        new(CodecTagKeys.Disc, "Disc", CodecTagFieldKind.Number),
        new(CodecTagKeys.Comment, "Comment"),
        new(CopyrightKey, "Copyright", Hint: "E.g. \"2002 Nintendo\"."),
        new(RipperKey, "Ripped by"),
        new(LengthKey, "Length", CodecTagFieldKind.Duration,
            Hint: $"Play time before the fade, as m:ss. Blank plays {DefaultPlaySeconds / 60:0} minutes."),
        new(FadeKey, "Fade", CodecTagFieldKind.Duration, Hint: $"Fade-out length in seconds. Blank fades for {DefaultFadeSeconds:0} seconds."),
    ];

    public string Id => "gsf";
    public string DisplayName => "Game Boy Advance GSF (mGBA emulation)";
    public string FormatName => "GSF";
    public IReadOnlyCollection<string> Extensions { get; } = [".gsf", ".minigsf"];
    public IReadOnlyList<CodecTagField> TagFields => Fields;

    public CodecFileInfo? ReadInfo(string path)
    {
        // A playable song carries data or names a library holding it (a bare tag-only PSF plays nothing).
        if (PsfFile.TryReadTags(path, PsfFile.GsfVersion) is not var (_, program, tags) || (program == 0 && tags["_lib"] is null))
            return null;
        var (play, fade) = PlayTime(tags);
        return new CodecFileInfo
        {
            Tags = PsfTagMap.ToCodecTags(tags, GsfBy),
            Duration = TimeSpan.FromSeconds(play + fade),
            BitsPerSample = 16,
        };
    }

    public ICodecDecoder Open(string path)
    {
        var file = PsfFile.TryLoad(path, PsfFile.GsfVersion) ?? throw new InvalidDataException($"{Path.GetFileName(path)} is not a GSF file.");
        if (!MgbaGsfNative.IsAvailable)
            throw new DllNotFoundException("mgbagsf.dll is missing from the GSF plugin's folder (build it with native/mgbagsf/build.ps1).");

        var (play, fade) = PlayTime(file.Tags);
        return new GsfDecoder(GsfImage.Load(path), play, fade, PsfTagMap.VolumeOf(file.Tags));
    }

    public CodecTags ReadTags(string path) =>
        PsfFile.TryReadTags(path, PsfFile.GsfVersion) is var (_, _, tags) ? PsfTagMap.ToCodecTags(tags, GsfBy) : new CodecTags();

    public void WriteTags(string path, CodecTags tags)
    {
        var file = PsfFile.TryLoad(path, PsfFile.GsfVersion) ?? throw new InvalidDataException($"{Path.GetFileName(path)} is not a GSF file.");
        PsfTagMap.Apply(file.Tags, tags, GsfBy);

        // Write beside the file, then swap it in, so a failure can't leave a half-written GSF.
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, file.WithTags(file.Tags));
        File.Move(temp, path, overwrite: true);
    }

    private static (double Play, double Fade) PlayTime(PsfTags tags) =>
        PsfTagMap.PlayTime(tags, DefaultPlaySeconds, DefaultFadeSeconds);
}
