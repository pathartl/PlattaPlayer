namespace PlattaPlayer.Codecs.Abstractions;

/// <summary>
/// A drop-in decoder for a file format BASS can't play itself. A codec plugin is discovered from the plugins
/// folder like a visualizer plugin, and the host uses it in three places: the library scanner reads the
/// file's tags and length (<see cref="ReadInfo"/>), the playback engine pulls PCM from it
/// (<see cref="Open"/>), and the Tag Editor edits its tags (<see cref="TagFields"/>, <see cref="ReadTags"/>,
/// <see cref="WriteTags"/>).
/// <para>Implementations must have a public parameterless constructor and be safe to call from any thread;
/// each <see cref="ICodecDecoder"/> is used by one thread at a time.</para>
/// <para>Optional capabilities are separate interfaces the plugin may also implement:
/// <see cref="ICodecSettings"/> (choices offered in the app's Settings) and <see cref="ICodecAlbumFiles"/>
/// (album fields kept in a shared album file rather than in each file).</para>
/// </summary>
public interface ICodecPlugin
{
    /// <summary>Stable identifier, e.g. "spc".</summary>
    string Id { get; }

    /// <summary>Human-readable name, e.g. "SNES SPC700".</summary>
    string DisplayName { get; }

    /// <summary>Short format name shown in the library and the Tag Editor, e.g. "SPC" or "MIDI". Defaults
    /// to the first extension, upper-cased.</summary>
    string FormatName => Extensions.First().TrimStart('.').ToUpperInvariant();

    /// <summary>File extensions handled, with the leading dot (e.g. ".spc"). Matched case-insensitively.</summary>
    IReadOnlyCollection<string> Extensions { get; }

    /// <summary>Called once by the host after loading the plugin, before any other call that needs the host
    /// (opening files, settings). A plugin that needs nothing from the host can ignore it.</summary>
    void Initialize(ICodecHost host) { }

    /// <summary>Reads what the library needs from a file: its tags, play length and audio properties.
    /// Returns null when the file is not a valid file of this format.</summary>
    CodecFileInfo? ReadInfo(string path);

    /// <summary>Opens a file for playback. Throws when it can't be played. May block for a while (e.g. to
    /// boot an emulated device); the host calls it off the UI thread.</summary>
    ICodecDecoder Open(string path);

    /// <summary>The cover image for a file when the format has its own way of naming one (a tag, a sidecar),
    /// or null to let the host use the folder's image (cover.jpg, folder.jpg…).</summary>
    string? FindCover(string path) => null;

    /// <summary>The tags the Tag Editor offers for this format, in display order. Fields keyed by a
    /// <see cref="CodecTagKeys"/> constant fill the editor's standard fields (title, artist, album…); the
    /// rest are shown as format-specific fields. Empty when the format's tags are read-only.</summary>
    IReadOnlyList<CodecTagField> TagFields { get; }

    /// <summary>Reads the file's editable tags, keyed as in <see cref="TagFields"/>.</summary>
    CodecTags ReadTags(string path);

    /// <summary>Writes the tags into the file in place. A key that is absent (or blank) clears that field;
    /// keys that aren't in <see cref="TagFields"/> are ignored.</summary>
    void WriteTags(string path, CodecTags tags);
}

/// <summary>
/// PCM produced by a codec: interleaved 32-bit float frames at <see cref="SampleRate"/>. Not thread-safe;
/// the host serialises calls. A decoder that plays on an output of its own instead implements
/// <see cref="ICodecDirectOutput"/>.
/// </summary>
public interface ICodecDecoder : IDisposable
{
    /// <summary>The PCM rate (0 for an <see cref="ICodecDirectOutput"/>).</summary>
    int SampleRate { get; }

    /// <summary>The PCM channel count (0 for an <see cref="ICodecDirectOutput"/>).</summary>
    int Channels { get; }

    /// <summary>The play length, including any fade-out the codec applies.</summary>
    TimeSpan Duration { get; }

    /// <summary>The position of the next frame <see cref="Read"/> will return.</summary>
    TimeSpan Position { get; }

    /// <summary>What produces the audio, for display (e.g. "SPC700 + S-DSP (emulated)").</summary>
    string Description { get; }

    /// <summary>Identifies what the sound depends on besides the file (e.g. the SoundFont a MIDI file is
    /// rendered with), so the host's caches of decoded data can tell renderings apart. Null when the file
    /// alone decides the sound.</summary>
    string? Variant => null;

    /// <summary>Whether the host may open a second decoder of the same file while this one plays, to draw
    /// its seek-bar waveform. False when decoding is costly or holds an exclusive resource (e.g. an emulated
    /// hardware device).</summary>
    bool AllowsBackgroundDecode => true;

    /// <summary>Fills <paramref name="buffer"/> with interleaved frames and returns how many frames were
    /// written; fewer than requested (down to 0) means the end was reached.</summary>
    int Read(Span<float> buffer);

    /// <summary>Moves to <paramref name="position"/> (clamped to the length). May be slow for formats that
    /// have to be emulated up to the target.</summary>
    void Seek(TimeSpan position);
}

/// <summary>
/// A decoder that plays the file on an output of its own (e.g. a hardware MIDI port) instead of handing PCM
/// to the host. The host drives its transport and never calls <see cref="ICodecDecoder.Read"/>; since no
/// audio passes through the host, there is nothing to visualize. <see cref="ICodecDecoder.Position"/> is
/// what is being heard.
/// </summary>
public interface ICodecDirectOutput : ICodecDecoder
{
    /// <summary>Output volume, 0–1.</summary>
    double Volume { set; }

    /// <summary>Raised (on any thread) when playback reaches the end.</summary>
    event EventHandler? Ended;

    void Play();

    void Pause();
}

/// <summary>What a codec reports about a file for the library.</summary>
public sealed class CodecFileInfo
{
    public required CodecTags Tags { get; init; }

    /// <summary>The play length, including any fade-out.</summary>
    public TimeSpan Duration { get; init; }

    public int? SampleRate { get; init; }
    public int? BitsPerSample { get; init; }
    public int? Bitrate { get; init; }
}

/// <summary>The keys of the tags every music library understands. A codec maps its own fields onto these
/// where they mean the same thing (e.g. an SPC's game title is the album).</summary>
public static class CodecTagKeys
{
    public const string Title = "title";
    public const string Artist = "artist";
    public const string Album = "album";
    public const string AlbumArtist = "albumartist";
    public const string Genre = "genre";
    /// <summary>A year, as digits.</summary>
    public const string Year = "year";
    /// <summary>A positive track number, as digits.</summary>
    public const string Track = "track";
    /// <summary>A positive disc number, as digits.</summary>
    public const string Disc = "disc";
    public const string Comment = "comment";

    /// <summary>True for the keys above.</summary>
    public static bool IsStandard(string key) => key is Title or Artist or Album or AlbumArtist or Genre
        or Year or Track or Disc or Comment;
}

/// <summary>How a tag field is edited.</summary>
public enum CodecTagFieldKind
{
    Text,
    /// <summary>A whole number.</summary>
    Number,
    /// <summary>A time span, written as seconds or m:ss (fractions allowed).</summary>
    Duration,
    /// <summary>One of the values <see cref="CodecTagField.Choices"/> lists.</summary>
    Choice,
}

/// <summary>Describes one editable tag field.</summary>
/// <param name="Key">The key in <see cref="CodecTags"/>.</param>
/// <param name="Label">The field label shown in the Tag Editor.</param>
/// <param name="Kind">How the value is entered.</param>
/// <param name="MaxLength">The longest value the format stores in full, or null for no limit.</param>
/// <param name="Hint">An optional explanation shown under the field.</param>
/// <param name="Choices">For <see cref="CodecTagFieldKind.Choice"/>: the values offered, read again each time
/// the editor shows the field (so newly installed options appear). The value "" stands for "not set".</param>
public sealed record CodecTagField(
    string Key, string Label, CodecTagFieldKind Kind = CodecTagFieldKind.Text, int? MaxLength = null, string? Hint = null,
    Func<IReadOnlyList<CodecChoice>>? Choices = null);

/// <summary>A value of a choice (a tag field or a setting) and the label shown for it.</summary>
public sealed record CodecChoice(string Value, string Label);

/// <summary>A codec file's tags as key/value text. Keys are case-insensitive; blank values are not stored.</summary>
public sealed class CodecTags
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<string> Keys => _values.Keys;

    /// <summary>The value for <paramref name="key"/>, or null when it isn't set.</summary>
    public string? this[string key]
    {
        get => _values.TryGetValue(key, out var v) ? v : null;
        set
        {
            if (string.IsNullOrWhiteSpace(value)) _values.Remove(key);
            else _values[key] = value.Trim();
        }
    }

    /// <summary>The value as a positive whole number, or null.</summary>
    public int? GetNumber(string key) =>
        int.TryParse(this[key], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n) && n > 0
            ? n
            : null;

    public CodecTags Clone()
    {
        var copy = new CodecTags();
        foreach (var (k, v) in _values) copy._values[k] = v;
        return copy;
    }

    /// <summary>True when both hold the same keys with the same values.</summary>
    public bool ContentEquals(CodecTags other) =>
        _values.Count == other._values.Count
        && _values.All(kv => other._values.TryGetValue(kv.Key, out var v) && string.Equals(v, kv.Value, StringComparison.Ordinal));
}
