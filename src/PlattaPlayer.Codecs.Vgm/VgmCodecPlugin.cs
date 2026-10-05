using System.Globalization;
using PlattaPlayer.Codecs.Abstractions;
using PlattaPlayer.Codecs.AlbumFiles;
using PlattaPlayer.Codecs.Vgm.Emulation;

namespace PlattaPlayer.Codecs.Vgm;

/// <summary>
/// VGM / VGZ sound-chip logs: the register writes a game made to its sound chips, played back on emulated
/// chips by libvgm, the reference VGM player. That covers the Sega Mega Drive / Genesis (YM2612 + SN76489,
/// with Mega-CD and 32X), Master System and Game Gear, most arcade boards of the 80s and 90s, the PC Engine,
/// Neo Geo, MSX, the PC's OPL cards and more, about forty chips in all.
/// <para>
/// Tags are GD3 (see <see cref="VgmTagMap"/>): title, composer, game (the album), release date (the year) and
/// notes stay in the file, where every VGM player reads them. What GD3 lacks lives in the album's M3U, as for
/// MIDI (<see cref="ICodecAlbumFiles"/>): the album artist and genre (<c>#EXTART:</c>, <c>#EXTGENRE:</c>), the
/// cover (<c>#EXTIMG:</c>), the track number (the entry's position) and the disc (<c>#EXTDISC:</c> above the
/// entry). vgmrips packs already ship that M3U, named after the game; one is created when needed. Without it,
/// the track number and album artist come from the folder (see <see cref="VgmFolder"/>). A looping song plays its loop twice and fades out over
/// 5 seconds, VGMPlay's defaults, scaled by the file's own loop modifier.
/// </para>
/// </summary>
public sealed class VgmCodecPlugin : ICodecPlugin, ICodecSettings, ICodecAlbumFiles
{
    /// <summary>How many times a looping song's looped section plays (VGMPlay's default).</summary>
    public const int DefaultLoops = 2;

    /// <summary>The fade-out after the last loop, in seconds (VGMPlay's default).</summary>
    public const double FadeSeconds = 5;

    /// <summary>The OPL4 (YMF278B) wavetable ROM, looked for in the shared ROMs folder.</summary>
    public const string Opl4RomName = "yrw801.rom";

    public const string FmSetting = "fm";
    private const string FmNuked = "nuked";
    private const string FmStandard = "standard";

    private static readonly CodecTagField[] Fields =
    [
        new(CodecTagKeys.Title, "Title"),
        new(VgmTagMap.TitleJapaneseKey, "Title (Japanese)"),
        new(CodecTagKeys.Artist, "Composer", Hint: "Several are separated by semicolons; the file lists them with commas."),
        new(VgmTagMap.AuthorJapaneseKey, "Composer (Japanese)"),
        new(CodecTagKeys.Album, "Game"),
        new(VgmTagMap.GameJapaneseKey, "Game (Japanese)"),
        new(CodecTagKeys.AlbumArtist, "Album artist"),
        new(CodecTagKeys.Genre, "Genre"),
        new(CodecTagKeys.Year, "Year", CodecTagFieldKind.Number),
        new(CodecTagKeys.Track, "Track", CodecTagFieldKind.Number),
        new(CodecTagKeys.Disc, "Disc", CodecTagFieldKind.Number),
        new(VgmTagMap.SystemKey, "System", Hint: "E.g. \"Sega Mega Drive / Genesis\"."),
        new(VgmTagMap.SystemJapaneseKey, "System (Japanese)"),
        new(VgmTagMap.ReleaseDateKey, "Release date", Hint: "E.g. \"1991/06/23\". The year is taken from it."),
        new(VgmTagMap.RipperKey, "VGM by"),
        new(CodecTagKeys.Comment, "Notes"),
    ];

    private static readonly CodecChoice[] FmChoices =
    [
        new(FmNuked, "Nuked (most accurate)"),
        new(FmStandard, "Standard (faster)"),
    ];

    private readonly M3uSidecarIndex _m3u = new();
    private readonly VgmFolder _folders;
    private readonly Lock _writeGate = new();
    private ICodecHost? _host;
    private IReadOnlyList<CodecSetting>? _settings;

    public VgmCodecPlugin() => _folders = new VgmFolder(_m3u);

    public string Id => "vgm";
    public string DisplayName => "VGM sound-chip logs (libvgm emulation)";
    public string FormatName => "VGM";
    public IReadOnlyCollection<string> Extensions { get; } = [".vgm", ".vgz"];
    public IReadOnlyList<CodecTagField> TagFields => Fields;

    public IReadOnlyList<CodecSetting> Settings => _settings ??=
    [
        new CodecSetting
        {
            Key = FmSetting,
            Label = "Yamaha FM chips (VGM)",
            Description = "How the FM chips of VGM files (the Mega Drive's YM2612, YM2151, YM2413, OPL2/OPL3) are "
                + "emulated. Nuked emulates them gate by gate from die shots and matches the hardware, at a higher "
                + "CPU cost; standard uses libvgm's usual cores (Genesis Plus GX, MAME…). OPL4 songs also need the "
                + $"chip's sample ROM, {Opl4RomName}, in the ROMs folder.",
            GetChoices = () => FmChoices,
            GetEffective = () => UseNuked ? FmNuked : FmStandard,
            Folders = _host is null ? [] : [new CodecFolder("ROMs folder", _host.UserFolder("Roms"))],
        },
    ];

    private bool UseNuked => _host?.GetSetting(FmSetting) != FmStandard;

    public void Initialize(ICodecHost host)
    {
        _host = host;
        _settings = null;
    }

    public CodecFileInfo? ReadInfo(string path)
    {
        if (VgmFile.TryRead(path) is not { } file) return null;
        var gd3 = file.Gd3;
        var tags = VgmTagMap.ToCodecTags(gd3);
        var entry = _m3u.Find(path);

        // The library's view: Japanese names where the English are blank, then the album file, then the folder.
        tags[CodecTagKeys.Title] = tags[CodecTagKeys.Title] ?? tags[VgmTagMap.TitleJapaneseKey] ?? entry?.Title ?? Path.GetFileNameWithoutExtension(path);
        tags[CodecTagKeys.Album] = VgmTagMap.Game(gd3) ?? entry?.Album;
        tags[CodecTagKeys.Artist] = VgmTagMap.Authors(VgmTagMap.Author(gd3)) ?? entry?.Artist;
        tags[CodecTagKeys.AlbumArtist] = entry?.AlbumArtist ?? string.Join("; ", _folders.GameAuthors(path, VgmTagMap.Game(gd3)));
        tags[CodecTagKeys.Genre] = entry?.Genre;
        tags[CodecTagKeys.Track] = Number(entry?.TrackNo ?? VgmFolder.NameNumber(Path.GetFileName(path)));
        tags[CodecTagKeys.Disc] = Number(entry?.Disc);

        var (play, fade) = VgmDecoder.Length(file, DefaultLoops, FadeSeconds);
        return new CodecFileInfo
        {
            Tags = tags,
            Duration = TimeSpan.FromSeconds((play + fade) / (double)VgmFile.SampleRate),
            SampleRate = VgmFile.SampleRate,
            BitsPerSample = 16,
        };
    }

    public ICodecDecoder Open(string path)
    {
        if (VgmFile.TryLoad(path) is not var (data, _) || VgmFile.Parse(data) is not { } file)
            throw new InvalidDataException($"{Path.GetFileName(path)} is not a VGM file.");
        if (!LibVgmNative.IsAvailable)
            throw new DllNotFoundException("ppvgm.dll is missing from the VGM plugin's folder (build it with native/libvgm/build.ps1).");

        return new VgmDecoder(data, file, DefaultLoops, FadeSeconds, UseNuked, file.UsesOpl4 ? Opl4Rom() : null, file.ChipNames);
    }

    /// <summary>The album file's cover (<c>#EXTIMG:</c>), else the folder's own image (see
    /// <see cref="VgmFolder.FindCover"/>).</summary>
    public string? FindCover(string path) =>
        _m3u.Find(path)?.CoverPath is { } cover && File.Exists(cover) ? cover : VgmFolder.FindCover(path);

    /// <summary>The GD3 fields, plus the track number and disc the album file (or the file name) gives. The
    /// album artist and genre are the album file's alone (<see cref="AlbumFileKeys"/>).</summary>
    public CodecTags ReadTags(string path)
    {
        var tags = VgmTagMap.ToCodecTags(VgmFile.TryRead(path)?.Gd3);
        tags[CodecTagKeys.Track] = Number(_folders.TrackNumber(path));
        tags[CodecTagKeys.Disc] = Number(_m3u.Find(path)?.Disc);
        return tags;
    }

    /// <summary>Writes the GD3 fields into the file. A new track number moves the file's entry in the album
    /// file, and the disc is set on that entry.</summary>
    public void WriteTags(string path, CodecTags tags)
    {
        lock (_writeGate)
        {
            if (VgmFile.TryLoad(path) is not var (data, gzipped) || VgmFile.Parse(data) is not { } file)
                throw new InvalidDataException($"{Path.GetFileName(path)} is not a VGM file.");

            var gd3 = file.Gd3?.Clone() ?? new Gd3Tag();
            if (VgmTagMap.Apply(gd3, tags, VgmTagMap.ToCodecTags(file.Gd3)))
            {
                var bytes = VgmFile.WithGd3(data, gd3.IsEmpty ? null : gd3);
                if (gzipped) bytes = VgmFile.Compress(bytes);

                // Write beside the file, then swap it in, so a failure can't leave a half-written VGM.
                var temp = path + ".tmp";
                File.WriteAllBytes(temp, bytes);
                File.Move(temp, path, overwrite: true);
                _folders.Invalidate(path);
            }

            // A cleared track number is left as it is: every listed file has one.
            if (tags.GetNumber(CodecTagKeys.Track) is { } track && track != _folders.TrackNumber(path))
            {
                SeedAlbumFiles([path]);
                M3uSidecarWriter.MoveTo(_m3u, path, track, NewAlbumFileName(path));
            }
            if (tags.GetNumber(CodecTagKeys.Disc) is var disc && disc != _m3u.Find(path)?.Disc)
            {
                SeedAlbumFiles([path]);
                M3uSidecarWriter.SetDisc(_m3u, path, disc, NewAlbumFileName(path));
            }
        }
    }

    // --- Album files: M3U playlists ---------------------------------------------------------------------

    /// <summary>The game stays in GD3, where other players read it; only what GD3 lacks is the album file's.</summary>
    public IReadOnlyCollection<string> AlbumFileKeys { get; } = [CodecTagKeys.AlbumArtist, CodecTagKeys.Genre];

    public CodecAlbumEntry? FindAlbumEntry(string path) => _m3u.Find(path);

    public IReadOnlyList<string> WriteAlbum(IReadOnlyCollection<string> paths, CodecAlbumChanges changes)
    {
        // The game is never written here (see AlbumFileKeys), nor cleared from the files' GD3.
        var albumChanges = new CodecAlbumChanges
        {
            SetAlbumArtist = changes.SetAlbumArtist, AlbumArtist = changes.AlbumArtist,
            SetGenre = changes.SetGenre, Genre = changes.Genre,
            CoverSourcePath = changes.CoverSourcePath,
        };
        if (albumChanges.IsEmpty || paths.Count == 0) return [];
        lock (_writeGate)
        {
            SeedAlbumFiles(paths);
            return M3uSidecarWriter.Apply(_m3u, paths, albumChanges, NewAlbumFileName(paths.First()));
        }
    }

    public IReadOnlyDictionary<string, int> WriteTrackOrder(IReadOnlyList<string> pathsInOrder, bool save)
    {
        if (!save) return M3uSidecarWriter.Reorder(_m3u, pathsInOrder, save: false);
        lock (_writeGate)
        {
            SeedAlbumFiles(pathsInOrder);
            return M3uSidecarWriter.Reorder(_m3u, pathsInOrder);
        }
    }

    /// <summary>
    /// Before a folder's first album file is written, lists every VGM of the folder in it, in the order the file
    /// names give, so files that weren't edited keep their track numbers instead of colliding with the new
    /// list's. A folder that has a playlist, or whose files a playlist above it lists, is left alone.
    /// </summary>
    private void SeedAlbumFiles(IEnumerable<string> paths)
    {
        foreach (var dir in paths.Select(p => Path.GetDirectoryName(Path.GetFullPath(p))!).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (M3uSidecarIndex.PlaylistsIn(dir).Any()) continue;
            var songs = Directory.EnumerateFiles(dir)
                .Where(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .OrderBy(f => VgmFolder.NameNumber(Path.GetFileName(f)) ?? int.MaxValue)
                .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (songs.Count == 0 || songs.Any(s => _m3u.Find(s) is not null)) continue;
            M3uSidecarWriter.Apply(_m3u, songs, new CodecAlbumChanges(), NewAlbumFileName(songs[0]));
            M3uSidecarWriter.Reorder(_m3u, songs);
        }
    }

    /// <summary>A new album file is named after the game, as in vgmrips packs ("Sonic the Hedgehog.m3u8").</summary>
    private static string? NewAlbumFileName(string path) => VgmTagMap.Game(VgmFile.TryRead(path)?.Gd3);

    private static string? Number(int? n) => n?.ToString(CultureInfo.InvariantCulture);

    // The OPL4's sample ROM from the ROMs folder, if the user has put it there.
    private byte[]? Opl4Rom()
    {
        if (_host is null) return null;
        try
        {
            var path = Path.Combine(_host.UserFolder("Roms"), Opl4RomName);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
