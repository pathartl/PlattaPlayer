using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using PlattaPlayer.Codecs.Abstractions;
using PlattaPlayer.Sources.Local;

namespace PlattaPlayer.App.ViewModels.Items;

/// <summary>
/// One row in the Tag Editor grid: a file of a codec plugin's format (MIDI, SNES SPC, …), its editable tags, and
/// where the values it doesn't carry come from. Display columns show the effective values the library would
/// use, including edits that are staged on the row but not saved yet (<see cref="IsDirty"/>).
/// <para>
/// The plugin's standard keys fill the editor's standard fields (<see cref="Tags"/>); its other fields are
/// edited as format-specific fields (<see cref="Extra"/>). Track-level fields are saved in the file. Album
/// fields are saved in the file too, unless the plugin keeps them in an album file shared by the album's files
/// (<see cref="ICodecAlbumFiles"/>, e.g. an M3U beside MIDI files). Covers are the plugin's own (a tag, the
/// album file) or the folder's image.
/// </para>
/// <para>A file holding several songs (<see cref="ICodecSubsongs"/>, e.g. a Game Boy .gbs) has a row per song;
/// fields the format keeps once per file (the game) are shared by those rows.</para>
/// </summary>
public sealed class TagFileItemViewModel : ObservableObject
{
    // Field names, as used by CanEdit.
    public const string TitleField = "title", ArtistField = "artist", YearField = "year", TrackField = "track",
        DiscField = "disc", AlbumField = "album", AlbumArtistField = "albumartist", GenreField = "genre",
        CoverField = "cover";

    // The editor's standard fields and the codec keys they show.
    private static readonly Dictionary<string, string> StandardKeys = new()
    {
        [TitleField] = CodecTagKeys.Title,
        [ArtistField] = CodecTagKeys.Artist,
        [AlbumField] = CodecTagKeys.Album,
        [AlbumArtistField] = CodecTagKeys.AlbumArtist,
        [GenreField] = CodecTagKeys.Genre,
        [YearField] = CodecTagKeys.Year,
        [TrackField] = CodecTagKeys.Track,
        [DiscField] = CodecTagKeys.Disc,
    };

    // The tags as last read from disk; Tags and Extra are the working copies edits are staged on.
    private StandardTags _saved = new();
    private CodecTags _savedTags = new();
    private Dictionary<string, string?> _savedExtra = new();
    private string? _resolvedCover;

    public TagFileItemViewModel(string filePath, ICodecPlugin plugin, int? subsong = null)
    {
        FilePath = filePath;
        Subsong = subsong;
        Key = subsong is { } n ? $"{Path.GetFullPath(filePath)}::{n}" : Path.GetFullPath(filePath);
        FileName = subsong is { } song ? $"{Path.GetFileName(filePath)} #{song}" : Path.GetFileName(filePath);
        FolderName = Path.GetFileName(Path.GetDirectoryName(filePath));
        Plugin = plugin;
        AlbumFiles = plugin as ICodecAlbumFiles;
        ExtraFields = plugin.TagFields.Where(f => !StandardKeys.ContainsValue(f.Key)).ToList();
        Reload();
    }

    public string FilePath { get; }

    /// <summary>Which song of a multi-song file the row is, or null for a file that is one track.</summary>
    public int? Subsong { get; }

    /// <summary>Identifies the row: the full path, plus the song number for a song of a multi-song file.</summary>
    public string Key { get; }

    public string FileName { get; }
    public string? FolderName { get; }

    public ICodecPlugin Plugin { get; }

    /// <summary>Set when the format keeps album fields in an album file rather than in each file.</summary>
    public ICodecAlbumFiles? AlbumFiles { get; }

    /// <summary>The file's format as shown in the grid ("MIDI", "SPC"…).</summary>
    public string FormatName => Plugin.FormatName;

    /// <summary>The format's fields beyond the editor's standard ones, in the plugin's order.</summary>
    public IReadOnlyList<CodecTagField> ExtraFields { get; }

    /// <summary>Working copy of the format-specific fields (keyed as in <see cref="ExtraFields"/>; null = unset).</summary>
    public Dictionary<string, string?> Extra { get; private set; } = new();

    /// <summary>Whether the format stores <paramref name="field"/> (one of the <c>…Field</c> names).</summary>
    public bool CanEdit(string field) =>
        field == CoverField || (StandardKeys.TryGetValue(field, out var key) && Plugin.TagFields.Any(f => f.Key == key));

    /// <summary>True when the album fields (album, album artist, genre, cover) live in an album file shared by
    /// the album's files rather than in each file.</summary>
    public bool AlbumFieldsInAlbumFile => AlbumFiles is not null;

    /// <summary>Whether the album field (<see cref="AlbumField"/>, <see cref="AlbumArtistField"/> or
    /// <see cref="GenreField"/>) is saved in the album file rather than in the file: a format with an album file
    /// may keep some album fields in its own tags (a VGM's game).</summary>
    public bool InAlbumFile(string field) =>
        AlbumFiles is not null && StandardKeys.TryGetValue(field, out var key) && AlbumFiles.AlbumFileKeys.Contains(key, StringComparer.OrdinalIgnoreCase);

    /// <summary>Working copy of the standard tag values: staged edits live here until saved.</summary>
    public StandardTags Tags { get; private set; } = new();

    /// <summary>What the album file listing this file says about it, if any.</summary>
    public CodecAlbumEntry? AlbumEntry { get; private set; }

    // Staged album-level edits: null = unchanged, "" = cleared.
    public string? AlbumEdit { get; set; }
    public string? AlbumArtistEdit { get; set; }
    public string? GenreEdit { get; set; }

    /// <summary>A staged image to install as this file's album cover, or null.</summary>
    public string? CoverEdit { get; set; }

    /// <summary>The cover image the player would show for this file (a staged cover wins), if any.</summary>
    public string? CoverPath => CoverEdit ?? _resolvedCover;

    /// <summary>Where the current (saved) cover comes from.</summary>
    public CoverOrigin CoverOrigin { get; private set; }

    public bool HasAlbumEdits => AlbumEdit is not null || AlbumArtistEdit is not null || GenreEdit is not null || CoverEdit is not null;

    /// <summary>The staged album edits that go to the album file (null for those saved in the file).</summary>
    public (string? Album, string? AlbumArtist, string? Genre, string? Cover) AlbumFileEdits => AlbumFiles is null
        ? default
        : (InAlbumFile(AlbumField) ? AlbumEdit : null, InAlbumFile(AlbumArtistField) ? AlbumArtistEdit : null,
            InAlbumFile(GenreField) ? GenreEdit : null, CoverEdit);

    /// <summary>The track number this file would get from the reordered rows, while the new order is
    /// unsaved; null otherwise.</summary>
    public int? OrderTrack { get; set; }

    /// <summary>The user changed the track number (as opposed to it following the saved order).</summary>
    public bool TrackEdited => Tags.Track != _saved.Track;

    /// <summary>
    /// The track number as it will be saved, given an unsaved new order. With an album file, the order is saved
    /// there and only renumbers a file that carries its own track number; without one, the new order is saved as
    /// track numbers. Either way a number the user typed wins.
    /// </summary>
    public int? PendingTrack => AlbumFieldsInAlbumFile
        ? !TrackEdited && Tags.Track is not null && OrderTrack is { } n ? n : Tags.Track
        : !TrackEdited && OrderTrack is { } m && CanEdit(TrackField) ? m : Tags.Track;

    public bool TagsChanged =>
        !Tags.ContentEquals(_saved)
        || Extra.Any(kv => !string.Equals(Blank(kv.Value ?? ""), Blank(_savedExtra.GetValueOrDefault(kv.Key) ?? ""), StringComparison.Ordinal));

    /// <summary>The row has edits that haven't been saved.</summary>
    public bool IsDirty => TagsChanged || HasAlbumEdits;

    public string Title => Tags.Title ?? AlbumEntry?.Title ?? FallbackTitle;

    /// <summary>The title the library shows when the file gives none, as <c>LocalMediaSource</c> names it.</summary>
    public string FallbackTitle => Subsong is { } song
        ? $"{Path.GetFileNameWithoutExtension(FilePath)} #{song}"
        : Path.GetFileNameWithoutExtension(FilePath);
    public string? Artist => Tags.Artist ?? AlbumEntry?.Artist;
    public string? AlbumArtist => AlbumArtistEdit is { } edit ? Blank(edit) : Tags.AlbumArtist ?? AlbumEntry?.AlbumArtist;
    public string? Album => AlbumEdit is { } edit ? Blank(edit) ?? FolderName : Tags.Album ?? AlbumEntry?.Album ?? FolderName;
    public string? Genre => GenreEdit is { } edit ? Blank(edit) : Tags.Genre ?? AlbumEntry?.Genre;
    public int? Year => Tags.Year;
    public int? Track => PendingTrack ?? OrderTrack ?? AlbumEntry?.TrackNo;
    public int? Disc => Tags.Disc;
    public string? AlbumFileName => AlbumEntry is null ? null : Path.GetFileName(AlbumEntry.AlbumFilePath);

    /// <summary>The label the format gives a standard field (e.g. "Game" for an SPC's album), or null.</summary>
    public string? LabelFor(string field) =>
        StandardKeys.TryGetValue(field, out var key) ? Plugin.TagFields.FirstOrDefault(f => f.Key == key)?.Label : null;

    /// <summary>Captures the staged edits, so a field can later be put back to what it was.</summary>
    public EditState CaptureEdits() =>
        new(Tags.Copy(), AlbumEdit, AlbumArtistEdit, GenreEdit, new Dictionary<string, string?>(Extra));

    /// <summary>Re-reads the file (and its album file) from disk, discarding staged edits.</summary>
    public void Reload()
    {
        Load();
        AlbumEdit = AlbumArtistEdit = GenreEdit = CoverEdit = null;
        OrderTrack = null;
        Refresh();
    }

    private void Load()
    {
        try
        {
            _savedTags = ReadTags();
        }
        catch
        {
            _savedTags = new CodecTags();  // unreadable: shown untagged; saving will report the error
        }

        _saved = new StandardTags
        {
            Title = _savedTags[CodecTagKeys.Title],
            Artist = _savedTags[CodecTagKeys.Artist],
            AlbumArtist = _savedTags[CodecTagKeys.AlbumArtist],
            Album = _savedTags[CodecTagKeys.Album],
            Genre = _savedTags[CodecTagKeys.Genre],
            Year = _savedTags.GetNumber(CodecTagKeys.Year),
            Track = _savedTags.GetNumber(CodecTagKeys.Track),
            Disc = _savedTags.GetNumber(CodecTagKeys.Disc),
        };
        Tags = _saved.Copy();
        _savedExtra = ExtraFields.ToDictionary(f => f.Key, f => _savedTags[f.Key]);
        Extra = new Dictionary<string, string?>(_savedExtra);

        try
        {
            AlbumEntry = AlbumFiles?.FindAlbumEntry(FilePath);
        }
        catch
        {
            AlbumEntry = null;
        }

        string? own;
        try { own = Plugin.FindCover(FilePath); }
        catch { own = null; }
        _resolvedCover = own ?? FolderCover.Find(Path.GetDirectoryName(FilePath));
        CoverOrigin = _resolvedCover is null ? CoverOrigin.None
            : own is null ? CoverOrigin.Folder
            : string.Equals(own, AlbumEntry?.CoverPath, StringComparison.OrdinalIgnoreCase) ? CoverOrigin.AlbumFile
            : CoverOrigin.File;
    }

    /// <summary>Moves staged album-field edits into the tags, for the fields the format saves in the file. The
    /// file's own copy of a field saved in the album file is cleared, as the plugin cleared it.</summary>
    public void ApplyAlbumEdits()
    {
        if (AlbumEdit is not null) Tags.Album = InAlbumFile(AlbumField) ? null : Blank(AlbumEdit);
        if (AlbumArtistEdit is not null) Tags.AlbumArtist = InAlbumFile(AlbumArtistField) ? null : Blank(AlbumArtistEdit);
        if (GenreEdit is not null) Tags.Genre = InAlbumFile(GenreField) ? null : Blank(GenreEdit);
    }

    /// <summary>
    /// Writes the fields edited on this row into the file. The rest are written as the file has them now, not
    /// as they were loaded: another row may have changed them since (a song of the same multi-song file
    /// sharing its game field), and this row must not put them back.
    /// </summary>
    public Task WriteAsync() => Task.Run(() =>
    {
        // Start from the file, so keys the editor doesn't show survive.
        var tags = ReadTags();
        Set(tags, TitleField, Tags.Title, _saved.Title);
        Set(tags, ArtistField, Tags.Artist, _saved.Artist);
        Set(tags, AlbumField, Tags.Album, _saved.Album);
        Set(tags, AlbumArtistField, Tags.AlbumArtist, _saved.AlbumArtist);
        Set(tags, GenreField, Tags.Genre, _saved.Genre);
        Set(tags, YearField, Number(Tags.Year), Number(_saved.Year));
        Set(tags, TrackField, Number(Tags.Track), Number(_saved.Track));
        Set(tags, DiscField, Number(Tags.Disc), Number(_saved.Disc));
        foreach (var (key, value) in Extra)
            if (!string.Equals(Blank(value ?? ""), Blank(_savedExtra.GetValueOrDefault(key) ?? ""), StringComparison.Ordinal))
                tags[key] = value;

        if (Subsong is { } subsong && Plugin is ICodecSubsongs subsongs)
            subsongs.WriteTags(FilePath, subsong, tags);
        else
            Plugin.WriteTags(FilePath, tags);
    });

    private CodecTags ReadTags() =>
        Subsong is { } subsong && Plugin is ICodecSubsongs subsongs ? subsongs.ReadTags(FilePath, subsong) : Plugin.ReadTags(FilePath);

    private void Set(CodecTags tags, string field, string? value, string? saved)
    {
        if (CanEdit(field) && value != saved) tags[StandardKeys[field]] = value;
    }

    private static string? Number(int? value) => value?.ToString(CultureInfo.InvariantCulture);

    /// <summary>Refreshes the displayed columns after staged edits change.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Artist));
        OnPropertyChanged(nameof(AlbumArtist));
        OnPropertyChanged(nameof(Album));
        OnPropertyChanged(nameof(Genre));
        OnPropertyChanged(nameof(Year));
        OnPropertyChanged(nameof(Track));
        OnPropertyChanged(nameof(Disc));
        OnPropertyChanged(nameof(AlbumFileName));
        OnPropertyChanged(nameof(CoverPath));
        OnPropertyChanged(nameof(IsDirty));
    }

    private static string? Blank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <param name="Extra">The format-specific fields.</param>
    public sealed record EditState(StandardTags Tags, string? Album, string? AlbumArtist, string? Genre,
        IReadOnlyDictionary<string, string?> Extra);
}

/// <summary>Where a file's cover image comes from.</summary>
public enum CoverOrigin
{
    None,
    /// <summary>The file's own tags name it (e.g. a MIDI file's cover= tag).</summary>
    File,
    /// <summary>The album file names it.</summary>
    AlbumFile,
    /// <summary>The folder's cover.jpg / folder.jpg.</summary>
    Folder,
}

/// <summary>The Tag Editor's standard fields.</summary>
public sealed class StandardTags
{
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? AlbumArtist { get; set; }
    public string? Album { get; set; }
    public string? Genre { get; set; }
    public int? Year { get; set; }
    public int? Track { get; set; }
    public int? Disc { get; set; }

    public StandardTags Copy() => new()
    {
        Title = Title, Artist = Artist, AlbumArtist = AlbumArtist, Album = Album, Genre = Genre,
        Year = Year, Track = Track, Disc = Disc,
    };

    public bool ContentEquals(StandardTags o) =>
        Title == o.Title && Artist == o.Artist && AlbumArtist == o.AlbumArtist && Album == o.Album
        && Genre == o.Genre && Year == o.Year && Track == o.Track && Disc == o.Disc;
}
