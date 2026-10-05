using System.Globalization;
using System.Text.RegularExpressions;
using PlattaPlayer.Codecs.Abstractions;
using PlattaPlayer.Codecs.AlbumFiles;
using PlattaPlayer.Codecs.Gbs.Emulation;

namespace PlattaPlayer.Codecs.Gbs;

/// <summary>
/// Game Boy music (.gbs): a game's sound driver and its whole soundtrack, played on an emulated Game Boy Color
/// (ported from SameBoy). Each song of a file is a track of its own (<see cref="ICodecSubsongs"/>).
/// <para>
/// The GBS header names the game, the author and the copyright holder. Song titles and lengths come from the
/// NEZplug-style M3U playlists rips ship beside the file (see <see cref="NezEntry"/>): when one lists the file,
/// its entries are the songs, in its order (leaving out sound effects, say); otherwise every song is offered.
/// Song numbers (<see cref="CodecSubsong.Number"/>) are the GBS's own 0-based song indices.
/// </para>
/// <para>
/// Tags: the game is the album, the author the artist, the copyright year the year, and the song's position
/// in the playlist the track number. The game, author and copyright (with the year in it) are written into the
/// header, so every song of the file changes. The title, length and fade are the song's playlist entry, and
/// the playlist also keeps what GBS has no field for (<see cref="ICodecAlbumFiles"/>): the album artist
/// (<c>#EXTART:</c>, else the copyright holder, the copyright line less its year, else the author), the genre
/// (<c>#EXTGENRE:</c>), the cover (<c>#EXTIMG:</c>) and each song's disc (<c>#EXTDISC:</c> above its entry).
/// A typed track number moves the song's entry. Writing creates <c>&lt;file&gt;.m3u</c>, listing every song,
/// when no playlist lists the file.
/// </para>
/// </summary>
public sealed partial class GbsCodecPlugin : ICodecPlugin, ICodecSubsongs, ICodecAlbumFiles
{
    /// <summary>Played when the playlist gives no length (Game Music Emu's default for GBS).</summary>
    public const double DefaultPlaySeconds = 150;

    /// <summary>Faded over when the playlist gives no fade length.</summary>
    public const double DefaultFadeSeconds = 8;

    // Format-specific tag keys.
    public const string CopyrightKey = "copyright";
    public const string LengthKey = "length";
    public const string FadeKey = "fade";

    private static readonly CodecTagField[] Fields =
    [
        new(CodecTagKeys.Title, "Song title", Hint: "Stored in the M3U playlist beside the file."),
        new(CodecTagKeys.Artist, "Author", MaxLength: GbsHeader.TextLength, Hint: "Stored in the GBS file: changes every song of it."),
        new(CodecTagKeys.Album, "Game", MaxLength: GbsHeader.TextLength, Hint: "Stored in the GBS file: changes every song of it."),
        new(CodecTagKeys.AlbumArtist, "Album artist", Hint: "Stored in the M3U playlist. Blank uses the copyright holder."),
        new(CodecTagKeys.Genre, "Genre", Hint: "Stored in the M3U playlist."),
        new(CodecTagKeys.Year, "Year", CodecTagFieldKind.Number, Hint: "Stored in the copyright line: changes every song of the file."),
        new(CodecTagKeys.Track, "Track", CodecTagFieldKind.Number, Hint: "The song's position in the M3U playlist."),
        new(CodecTagKeys.Disc, "Disc", CodecTagFieldKind.Number, Hint: "Stored in the M3U playlist."),
        new(CopyrightKey, "Copyright", MaxLength: GbsHeader.TextLength,
            Hint: "E.g. \"1998 Nintendo\". The year, and the album artist unless one is set, are taken from it. Changes every song of the file."),
        new(LengthKey, "Length", CodecTagFieldKind.Duration,
            Hint: $"Play time before the fade, as m:ss. Blank plays {DefaultPlaySeconds / 60:0.#} minutes."),
        new(FadeKey, "Fade", CodecTagFieldKind.Duration, Hint: $"Fade-out length in seconds. Blank fades for {DefaultFadeSeconds:0} seconds."),
    ];

    private readonly NezPlaylistIndex _playlists = new();
    private readonly Lock _writeGate = new();

    public string Id => "gbs";
    public string DisplayName => "Game Boy GBS (SameBoy emulation)";
    public string FormatName => "GBS";
    public IReadOnlyCollection<string> Extensions { get; } = [".gbs"];
    public IReadOnlyList<CodecTagField> TagFields => Fields;

    // ----- ICodecPlugin: the file's first listed song -----

    public CodecFileInfo? ReadInfo(string path) => ReadSubsongs(path) is [var first, ..] ? first.Info : null;

    public ICodecDecoder Open(string path) => Open(path, FirstSong(path));

    public CodecTags ReadTags(string path) => ReadSubsongs(path) is [var first, ..] ? first.Info.Tags : new CodecTags();

    public void WriteTags(string path, CodecTags tags) => WriteTags(path, FirstSong(path), tags);

    private int FirstSong(string path) =>
        ReadSubsongs(path) is [var first, ..] ? first.Number : throw new InvalidDataException($"{Path.GetFileName(path)} is not a GBS file.");

    // ----- ICodecSubsongs -----

    public IReadOnlyList<CodecSubsong>? ReadSubsongs(string path)
    {
        if (ReadHeader(path) is not { } header) return null;
        var album = _playlists.FindAlbum(path);
        return Songs(path, header)
            .Select((song, i) => new CodecSubsong(song.Song, Info(ToTags(header, album, song.Song, song.Entry, i + 1), song.Entry)))
            .ToList();
    }

    public ICodecDecoder Open(string path, int subsong)
    {
        var bytes = File.ReadAllBytes(path);
        var image = GbsImage.TryCreate(bytes) ?? throw new InvalidDataException($"{Path.GetFileName(path)} is not a GBS file.");
        var header = GbsHeader.TryParse(bytes)!;
        var entry = Songs(path, header).FirstOrDefault(s => s.Song == subsong).Entry;
        var (play, fade) = PlayTime(entry);
        return new GbsDecoder(image, subsong, play, fade);
    }

    public CodecTags ReadTags(string path, int subsong)
    {
        if (ReadHeader(path) is not { } header) return new CodecTags();
        var songs = Songs(path, header);
        var position = songs.FindIndex(s => s.Song == subsong);
        var entry = position >= 0 ? songs[position].Entry : null;
        return ToTags(header, _playlists.FindAlbum(path), subsong, entry, position >= 0 ? position + 1 : subsong + 1);
    }

    public void WriteTags(string path, int subsong, CodecTags tags)
    {
        lock (_writeGate)
        {
            var current = ReadTags(path, subsong);
            bool Changed(string key) => !string.Equals(tags[key], current[key], StringComparison.Ordinal);

            // File fields: into the header (only the fields that changed, so untouched ones keep their bytes). The
            // year is the copyright's, edited after the copyright so a year changed along with it lands in it.
            if (Changed(CodecTagKeys.Album) || Changed(CodecTagKeys.Artist) || Changed(CopyrightKey) || Changed(CodecTagKeys.Year))
            {
                var bytes = File.ReadAllBytes(path);
                if (GbsHeader.TryParse(bytes) is null) throw new InvalidDataException($"{Path.GetFileName(path)} is not a GBS file.");
                if (Changed(CodecTagKeys.Album)) GbsHeader.WriteTitle(bytes, tags[CodecTagKeys.Album]);
                if (Changed(CodecTagKeys.Artist)) GbsHeader.WriteAuthor(bytes, tags[CodecTagKeys.Artist]);
                var copyright = Changed(CopyrightKey) ? tags[CopyrightKey] : current[CopyrightKey];
                if (Changed(CodecTagKeys.Year)) copyright = WithYear(copyright, tags[CodecTagKeys.Year]);
                if (Changed(CopyrightKey) || Changed(CodecTagKeys.Year)) GbsHeader.WriteCopyright(bytes, copyright);

                // Write beside the file, then swap it in, so a failure can't leave a half-written GBS.
                var temp = path + ".tmp";
                File.WriteAllBytes(temp, bytes);
                File.Move(temp, path, overwrite: true);
            }

            // Song fields: into the playlist.
            var title = Changed(CodecTagKeys.Title) ? tags[CodecTagKeys.Title] ?? "" : null;
            var time = Changed(LengthKey) ? NezTime.Parse(tags[LengthKey]) is > 0 and var t ? NezTime.Format(t) : "" : null;
            var fade = Changed(FadeKey) ? NezTime.Parse(tags[FadeKey]) is >= 0 and var f ? FormatFade(f) : "" : null;
            if (title is not null || time is not null || fade is not null)
            {
                var entry = EnsureListed(path, subsong);
                var document = NezDocument.Load(entry.PlaylistPath);
                document.Lines[entry.LineIndex] = entry.WithFields(title, time, fade);
                Save(document, entry.PlaylistPath);
            }

            if (Changed(CodecTagKeys.Disc))
            {
                var entry = EnsureListed(path, subsong);
                var document = NezDocument.Load(entry.PlaylistPath);
                M3uSidecarWriter.SetEntryDirective(document.Lines, entry.LineIndex, M3u.Disc, tags.GetNumber(CodecTagKeys.Disc)?.ToString(CultureInfo.InvariantCulture));
                Save(document, entry.PlaylistPath);
            }

            // A cleared track number is left as it is: every song has one.
            if (Changed(CodecTagKeys.Track) && tags.GetNumber(CodecTagKeys.Track) is { } track)
                MoveSong(path, subsong, track);
        }
    }

    /// <summary>
    /// The song's playlist entry, adding one when no playlist lists the song: to <c>&lt;file&gt;.m3u</c>, listing
    /// every song when no playlist lists the file at all, so the file keeps offering all of them.
    /// </summary>
    private NezEntry EnsureListed(string path, int subsong)
    {
        var entries = _playlists.Find(path);
        if (entries.FirstOrDefault(e => e.Song == subsong) is { } listed) return listed;

        var playlistPath = Path.ChangeExtension(path, ".m3u");
        var playlist = File.Exists(playlistPath) ? NezDocument.Load(playlistPath) : NezDocument.CreateNew();
        var fileName = Path.GetFileName(path);
        if (entries.Count == 0)
        {
            var songCount = ReadHeader(path)?.SongCount ?? subsong + 1;
            for (var song = 0; song < songCount; song++)
                playlist.Lines.Add(NezEntry.NewLine(fileName, song, null, null, null));
        }
        else
        {
            playlist.Lines.Add(NezEntry.NewLine(fileName, subsong, null, null, null));
        }
        Save(playlist, playlistPath);
        return _playlists.Find(path).First(e => e.Song == subsong);
    }

    private void Save(NezDocument document, string playlistPath)
    {
        document.Save(playlistPath);
        _playlists.Invalidate(playlistPath);
    }

    // ----- Helpers -----

    private static GbsHeader? ReadHeader(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[GbsHeader.Size];
            return stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false) == buffer.Length ? GbsHeader.TryParse(buffer) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    // The songs offered: the playlist entries when one lists the file (valid, first occurrence of each song),
    // otherwise every song of the header.
    private List<(int Song, NezEntry? Entry)> Songs(string path, GbsHeader header)
    {
        var listed = new List<(int, NezEntry?)>();
        var seen = new HashSet<int>();
        foreach (var entry in _playlists.Find(path))
            if (entry.Song < header.SongCount && seen.Add(entry.Song)) listed.Add((entry.Song, entry));
        if (listed.Count > 0) return listed;
        return Enumerable.Range(0, header.SongCount).Select(i => (i, (NezEntry?)null)).ToList();
    }

    private static (double Play, double Fade) PlayTime(NezEntry? entry) =>
        (entry?.PlaySeconds ?? DefaultPlaySeconds, entry?.FadeSeconds ?? DefaultFadeSeconds);

    private static CodecFileInfo Info(CodecTags tags, NezEntry? entry)
    {
        var (play, fade) = PlayTime(entry);
        return new CodecFileInfo
        {
            Tags = tags,
            Duration = TimeSpan.FromSeconds(play + fade),
            SampleRate = GbsDecoder.SampleRate,
            BitsPerSample = 16,
        };
    }

    private static CodecTags ToTags(GbsHeader header, NezAlbum? album, int song, NezEntry? entry, int position) => new()
    {
        [CodecTagKeys.Title] = entry?.Title ?? $"Song {song + 1}",
        [CodecTagKeys.Artist] = header.Author,
        [CodecTagKeys.Album] = header.Title,
        [CodecTagKeys.AlbumArtist] = album?.AlbumArtist ?? CopyrightHolder(header.Copyright) ?? header.Author,
        [CodecTagKeys.Genre] = album?.Genre,
        [CodecTagKeys.Year] = header.Copyright is { } copyright && YearPattern().Match(copyright) is { Success: true } m ? m.Value : null,
        [CodecTagKeys.Track] = position.ToString(CultureInfo.InvariantCulture),
        [CodecTagKeys.Disc] = entry?.Disc?.ToString(CultureInfo.InvariantCulture),
        [CopyrightKey] = header.Copyright,
        [LengthKey] = entry?.PlaySeconds is { } play ? NezTime.Format(play) : null,
        [FadeKey] = entry?.FadeSeconds is { } fade ? FormatFade(fade) : null,
    };

    private static string FormatFade(double seconds) => Math.Round(seconds, 3).ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>"Nintendo" from "1998 Nintendo", "(C) 1996-1997 Rare / Nintendo", "©1998 Konami".</summary>
    internal static string? CopyrightHolder(string? copyright)
    {
        if (string.IsNullOrWhiteSpace(copyright)) return null;
        var holder = CopyrightPrefix().Replace(copyright.Trim(), "").Trim(' ', ',', '.', '-');
        return holder.Length > 0 ? holder : null;
    }

    /// <summary>The copyright with its year replaced ("1998 Nintendo" → "1999 Nintendo"), or put in front when
    /// it has none; a cleared year is taken out ("1998 Nintendo" → "Nintendo").</summary>
    internal static string? WithYear(string? copyright, string? year)
    {
        copyright = copyright?.Trim() ?? "";
        year = year?.Trim();
        var match = YearPattern().Match(copyright);
        if (string.IsNullOrEmpty(year))
            return match.Success ? string.Join(' ', (copyright[..match.Index] + copyright[(match.Index + match.Length)..]).Split(' ', StringSplitOptions.RemoveEmptyEntries)) : copyright;
        return match.Success ? copyright[..match.Index] + year + copyright[(match.Index + match.Length)..]
            : copyright.Length == 0 ? year
            : $"{year} {copyright}";
    }

    [GeneratedRegex(@"\b(19|20)\d\d\b")]
    private static partial Regex YearPattern();

    // Leading "(C)", "©", "Copyright", "(P)" and years / year ranges, in any mix.
    [GeneratedRegex(@"^(?:\s*(?:\(c\)|\(p\)|©|℗|copyright\b|(?:19|20)\d\d(?:\s*[-–/,]\s*(?:19|20)?\d\d)?)[\s,.]*)+", RegexOptions.IgnoreCase)]
    private static partial Regex CopyrightPrefix();
}
