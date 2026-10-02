using PlattaPlayer.Codecs.Abstractions;
using PlattaPlayer.Codecs.Usf.Emulation;
using PlattaPlayer.Codecs.Usf.Psf;

namespace PlattaPlayer.Codecs.Usf;

/// <summary>
/// Nintendo 64 music (.usf, .miniusf): plays the rip on lazyusf2, an emulated N64 (R4300 CPU, RSP and audio
/// interface), and reads/writes its PSF tags. A miniUSF needs the .usflib it names beside it; libraries are
/// not offered as songs themselves. The RSP, which mixes the audio, runs low-level by default (it executes
/// the game's own microcode); the Settings can switch to lazyusf2's high-level audio, which is faster but
/// reimplements each known microcode and so can differ from the hardware.
/// </summary>
public sealed class UsfCodecPlugin : ICodecPlugin, ICodecSettings
{
    /// <summary>Played when the file has no length tag, as most PSF players do.</summary>
    public const double DefaultPlaySeconds = 180;

    /// <summary>Faded over when the file gives no fade length.</summary>
    public const double DefaultFadeSeconds = 10;

    // Format-specific tag keys.
    public const string CopyrightKey = "copyright";
    public const string RipperKey = "ripper";
    public const string LengthKey = "length";
    public const string FadeKey = "fade";

    public const string RspSetting = "rsp";
    private const string RspLowLevel = "lle";
    private const string RspHighLevel = "hle";

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
        new(CopyrightKey, "Copyright", Hint: "E.g. \"1997 Nintendo\"."),
        new(RipperKey, "Ripped by"),
        new(LengthKey, "Length", CodecTagFieldKind.Duration,
            Hint: $"Play time before the fade, as m:ss. Blank plays {DefaultPlaySeconds / 60:0} minutes."),
        new(FadeKey, "Fade", CodecTagFieldKind.Duration, Hint: $"Fade-out length in seconds. Blank fades for {DefaultFadeSeconds:0} seconds."),
    ];

    private static readonly CodecChoice[] RspChoices =
    [
        new(RspLowLevel, "Low-level RSP (accurate)"),
        new(RspHighLevel, "High-level audio (faster)"),
    ];

    private ICodecHost? _host;
    private IReadOnlyList<CodecSetting>? _settings;

    public string Id => "usf";
    public string DisplayName => "Nintendo 64 USF (lazyusf2 emulation)";
    public string FormatName => "USF";
    public IReadOnlyCollection<string> Extensions { get; } = [".usf", ".miniusf"];
    public IReadOnlyList<CodecTagField> TagFields => Fields;

    public IReadOnlyList<CodecSetting> Settings => _settings ??=
    [
        new CodecSetting
        {
            Key = RspSetting,
            Label = "N64 audio (USF)",
            Description = "How the N64's RSP, which mixes the game's audio, is emulated. Low-level runs the game's own "
                + "microcode and matches the hardware; high-level audio is faster but may sound different in some games.",
            GetChoices = () => RspChoices,
            GetEffective = () => UseHle ? RspHighLevel : RspLowLevel,
        },
    ];

    private bool UseHle => _host?.GetSetting(RspSetting) == RspHighLevel;

    public void Initialize(ICodecHost host) => _host = host;

    public CodecFileInfo? ReadInfo(string path)
    {
        // A playable song carries data or names a library holding it (a bare tag-only PSF plays nothing).
        if (PsfFile.TryReadTags(path) is not var (reserved, tags) || (reserved == 0 && tags["_lib"] is null)) return null;
        var (play, fade) = PlayTime(tags);
        return new CodecFileInfo
        {
            Tags = UsfTags.ToCodecTags(tags),
            Duration = TimeSpan.FromSeconds(play + fade),
            BitsPerSample = 16,
        };
    }

    public ICodecDecoder Open(string path)
    {
        var file = PsfFile.TryLoad(path) ?? throw new InvalidDataException($"{Path.GetFileName(path)} is not a USF file.");
        if (!LazyUsf2Native.IsAvailable)
            throw new DllNotFoundException("lazyusf2.dll is missing from the USF plugin's folder (build it with native/lazyusf2/build.ps1).");

        var (play, fade) = PlayTime(file.Tags);
        return new UsfDecoder(UsfSet.Load(path), UseHle, play, fade, UsfTags.VolumeOf(file.Tags));
    }

    public CodecTags ReadTags(string path) =>
        PsfFile.TryReadTags(path) is var (_, tags) ? UsfTags.ToCodecTags(tags) : new CodecTags();

    public void WriteTags(string path, CodecTags tags)
    {
        var file = PsfFile.TryLoad(path) ?? throw new InvalidDataException($"{Path.GetFileName(path)} is not a USF file.");
        UsfTags.Apply(file.Tags, tags);

        // Write beside the file, then swap it in, so a failure can't leave a half-written USF.
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, file.WithTags(file.Tags));
        File.Move(temp, path, overwrite: true);
    }

    private static (double Play, double Fade) PlayTime(PsfTags tags) =>
        (UsfTags.ParseSeconds(tags[UsfTags.Length]) is > 0 and var play ? play : DefaultPlaySeconds,
         UsfTags.ParseSeconds(tags[UsfTags.Fade]) is >= 0 and var fade ? fade : DefaultFadeSeconds);
}
