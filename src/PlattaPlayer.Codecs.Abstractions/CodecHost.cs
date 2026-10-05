namespace PlattaPlayer.Codecs.Abstractions;

/// <summary>What the host offers a codec plugin; handed to <see cref="ICodecPlugin.Initialize"/>.</summary>
public interface ICodecHost
{
    /// <summary>A private, writable folder for this plugin's own data (created on demand).</summary>
    string DataDirectory { get; }

    /// <summary>
    /// A drop-in folder the user manages, shared across the app and named by convention (e.g. "Roms" for
    /// firmware dumps, "SoundFonts"). Created on demand; <paramref name="name"/> is a plain folder name.
    /// </summary>
    string UserFolder(string name);

    /// <summary>The value the user picked for one of the plugin's <see cref="ICodecSettings.Settings"/>, or
    /// null when they haven't picked one.</summary>
    string? GetSetting(string key);
}

/// <summary>
/// Optional: a codec plugin that offers choices in the app's Settings (e.g. which device plays MIDI). The
/// host shows each setting, stores the user's pick and hands it back through <see cref="ICodecHost.GetSetting"/>.
/// </summary>
public interface ICodecSettings
{
    IReadOnlyList<CodecSetting> Settings { get; }
}

/// <summary>A setting offered in the app's Settings: one value out of a list that may change over time.</summary>
public sealed class CodecSetting
{
    /// <summary>Key under which the host stores the value (see <see cref="ICodecHost.GetSetting"/>).</summary>
    public required string Key { get; init; }

    public required string Label { get; init; }

    /// <summary>An explanation shown under the setting.</summary>
    public string? Description { get; init; }

    /// <summary>The values to choose from, read again whenever the user asks for a rescan.</summary>
    public required Func<IReadOnlyList<CodecChoice>> GetChoices { get; init; }

    /// <summary>The value in effect: the stored one if it's still offered, else the plugin's default. Null
    /// when there's nothing to choose.</summary>
    public required Func<string?> GetEffective { get; init; }

    /// <summary>Folders the user fills for this setting (e.g. ROM dumps), offered as "open folder" buttons.</summary>
    public IReadOnlyList<CodecFolder> Folders { get; init; } = [];
}

/// <summary>A folder the user is invited to open, with what it's for.</summary>
public sealed record CodecFolder(string Label, string Path);

/// <summary>
/// Optional: a codec plugin whose files can each hold several songs (e.g. a Game Boy .gbs holds a game's
/// whole soundtrack). The library lists every song of such a file as a track of its own, and the host plays,
/// reads and tags a song through the members here, passing the <see cref="CodecSubsong.Number"/> the plugin
/// reported for it.
/// <para>The single-song members of <see cref="ICodecPlugin"/> still work on such a file and mean its first
/// listed song; the host uses them only where a file is handled as a whole.</para>
/// </summary>
public interface ICodecSubsongs
{
    /// <summary>The songs the file offers, in album order (a song may be left out, e.g. a sound effect the
    /// rip's playlist skips). Null when the file is not a valid file of this format.</summary>
    IReadOnlyList<CodecSubsong>? ReadSubsongs(string path);

    /// <summary>Opens one song for playback, as <see cref="ICodecPlugin.Open"/>.</summary>
    ICodecDecoder Open(string path, int subsong);

    /// <summary>Reads one song's editable tags, keyed as in <see cref="ICodecPlugin.TagFields"/>.</summary>
    CodecTags ReadTags(string path, int subsong);

    /// <summary>Writes one song's tags, as <see cref="ICodecPlugin.WriteTags"/>. Fields the format stores
    /// once per file (e.g. the game) change for every song of the file.</summary>
    void WriteTags(string path, int subsong, CodecTags tags);
}

/// <summary>One song of a multi-song file.</summary>
/// <param name="Number">Identifies the song within its file (the plugin's own numbering, 0 or more); stable
/// across scans, since the library stores it.</param>
/// <param name="Info">What the library needs to know about the song, as <see cref="ICodecPlugin.ReadInfo"/>.</param>
public sealed record CodecSubsong(int Number, CodecFileInfo Info);

/// <summary>
/// Optional: a codec plugin whose format lacks album fields of its own, keeping album-level metadata (album,
/// album artist, genre, cover and the track order) in an album file shared by the album's files (e.g. an M3U
/// beside MIDI files). The Tag Editor then edits album fields there, and orders tracks by it.
/// <para><see cref="ICodecPlugin.ReadInfo"/> already folds the album file into what it reports.</para>
/// </summary>
public interface ICodecAlbumFiles
{
    /// <summary>The album fields (<see cref="CodecTagKeys.Album"/>, <see cref="CodecTagKeys.AlbumArtist"/>,
    /// <see cref="CodecTagKeys.Genre"/>) kept in the album file; any other is the file's own tag. All three by
    /// default. The cover and the track order are always the album file's.</summary>
    IReadOnlyCollection<string> AlbumFileKeys => [CodecTagKeys.Album, CodecTagKeys.AlbumArtist, CodecTagKeys.Genre];

    /// <summary>What the album file says about the file, or null when no album file lists it.</summary>
    CodecAlbumEntry? FindAlbumEntry(string path);

    /// <summary>
    /// Applies album-level changes to the album files describing the given files, creating or extending one
    /// where needed. A file's own copy of a changed field is cleared, since it would override the album file.
    /// Returns the album files written.
    /// </summary>
    IReadOnlyList<string> WriteAlbum(IReadOnlyCollection<string> paths, CodecAlbumChanges changes);

    /// <summary>
    /// Orders the files in their album files as given and returns each file's resulting track number (keyed
    /// by full path). With <paramref name="save"/> false nothing is written: only the numbers are computed.
    /// A song of a multi-song file (<see cref="ICodecSubsongs"/>) is given, and keyed, as
    /// <c>&lt;full path&gt;::&lt;song number&gt;</c>.
    /// </summary>
    IReadOnlyDictionary<string, int> WriteTrackOrder(IReadOnlyList<string> pathsInOrder, bool save);
}

/// <summary>What an album file says about one of its files. Any field but the path and track number may be
/// null.</summary>
/// <param name="AlbumFilePath">The album file listing the file.</param>
/// <param name="CoverPath">The album's cover image, resolved to a full path.</param>
/// <param name="Title">The entry's own title, if the album file gives one.</param>
/// <param name="Artist">The entry's own artist, if the album file gives one.</param>
/// <param name="TrackNo">The file's 1-based position in the album.</param>
/// <param name="Disc">The entry's disc, if the album file gives one.</param>
public sealed record CodecAlbumEntry(
    string AlbumFilePath,
    string? Album,
    string? AlbumArtist,
    string? Genre,
    string? CoverPath,
    string? Title,
    string? Artist,
    int TrackNo,
    int? Disc = null);

/// <summary>
/// Album-level edits for <see cref="ICodecAlbumFiles.WriteAlbum"/>. Each <c>SetX</c> flag marks a field as
/// changed; a changed field with a null/blank value is removed.
/// </summary>
public sealed class CodecAlbumChanges
{
    public bool SetAlbum { get; init; }
    public string? Album { get; init; }

    public bool SetAlbumArtist { get; init; }
    public string? AlbumArtist { get; init; }

    public bool SetGenre { get; init; }
    public string? Genre { get; init; }

    /// <summary>An image to install as the album cover; null leaves the cover alone.</summary>
    public string? CoverSourcePath { get; init; }

    public bool IsEmpty => !SetAlbum && !SetAlbumArtist && !SetGenre && CoverSourcePath is null;
}
