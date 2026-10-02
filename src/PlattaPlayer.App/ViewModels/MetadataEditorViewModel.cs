using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlattaPlayer.App.ViewModels.Items;
using PlattaPlayer.Codecs.Abstractions;
using PlattaPlayer.Core.Abstractions;
using PlattaPlayer.Core.Models;
using PlattaPlayer.Sources.Local;
using static PlattaPlayer.App.ViewModels.Items.TagFileItemViewModel;

namespace PlattaPlayer.App.ViewModels;

/// <summary>
/// mp3tag-style tag editor for the formats codec plugins handle (MIDI, SNES SPC, …). Files are
/// dragged into the grid on the right; the panel on the left edits the shared values of the current selection.
/// Edits are staged on the rows as they are typed (the grid shows them and marks changed rows) and written
/// together by Save all, so several selections can be edited before saving. A field is applied to the
/// selection only when it was actually changed, so untouched fields keep each file's own value ("mixed"
/// fields, shown blank, stay mixed unless edited, and putting a field back to its loaded value restores each
/// file's previous value). A field a format can't store is disabled, or skipped for those files in a mixed
/// selection.
/// <para>
/// Most formats keep every field in the file (an SPC's ID666 tag). A format whose plugin keeps album fields in
/// an album file (<see cref="ICodecAlbumFiles"/>, e.g. the M3U beside MIDI files) has each field saved where
/// it belongs: track-level fields in each file, album-level fields (album, album artist, genre, cover) and the
/// track order in the album file; the plugin clears a file's own copy of an album field when that field is
/// edited, since it would otherwise override the album file. Format-specific fields (an SPC's fade, a MIDI
/// file's SoundFont) are shown in their own section when the selection is all one format.
/// </para>
/// </summary>
public sealed partial class MetadataEditorViewModel : PageViewModelBase
{
    private const string NoValue = "—";

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp"
    };

    private readonly ILibrarySyncService _sync;
    private readonly CodecRegistry _codecs;
    private readonly List<TagFileItemViewModel> _selected = new();

    // Snapshot of the editor fields when the selection was loaded, used to detect what changed, and of
    // each selected file's staged edits, restored when a field is put back to its loaded value.
    private Loaded _loaded;
    private Dictionary<TagFileItemViewModel, EditState> _snapshot = new();

    // Set while the editor fields are filled from the selection, so that isn't taken as an edit.
    private bool _loading;

    public MetadataEditorViewModel(ILibrarySyncService sync, CodecRegistry codecs)
    {
        _sync = sync;
        _codecs = codecs;
    }

    public override string Title => "Tag Editor";

    public ObservableCollection<TagFileItemViewModel> Files { get; } = new();

    public int SelectionCount => _selected.Count;
    public bool HasSelection => _selected.Count > 0;
    public bool CanEditCover => _selected.Count > 0;

    // Whether any selected file's format stores the field.
    public bool CanEditTitle => CanEdit(TitleField);
    public bool CanEditArtist => CanEdit(ArtistField);
    public bool CanEditYear => CanEdit(YearField);
    public bool CanEditTrack => CanEdit(TrackField);
    public bool CanEditDisc => CanEdit(DiscField);
    public bool CanEditAlbum => CanEdit(AlbumField);
    public bool CanEditAlbumArtist => CanEdit(AlbumArtistField);
    public bool CanEditGenre => CanEdit(GenreField);

    // Field labels: a codec format may name a field its own way (an SPC's album is its game).
    public string TitleLabel => Label(TitleField, "Title");
    public string ArtistLabel => Label(ArtistField, "Artist");
    public string YearLabel => Label(YearField, "Year");
    public string TrackLabel => Label(TrackField, "Track");
    public string DiscLabel => Label(DiscField, "Disc");
    public string AlbumLabel => Label(AlbumField, "Album");
    public string AlbumArtistLabel => Label(AlbumArtistField, "Album artist");
    public string GenreLabel => Label(GenreField, "Genre");

    /// <summary>The formats that can be added, for the empty-grid hint ("MIDI or SPC files").</summary>
    public string FormatsText =>
        string.Join(" or ", _codecs.Plugins.Select(p => p.FormatName).Distinct().Order());

    /// <summary>File-picker patterns for every format the editor handles.</summary>
    public IReadOnlyList<string> FilePatterns =>
        _codecs.Extensions.Select(e => "*" + e.ToLowerInvariant()).ToList();

    private bool CanEdit(string field) => _selected.Any(f => f.CanEdit(field));

    private string Label(string field, string fallback)
    {
        var labels = _selected.Select(f => f.LabelFor(field)).Distinct().ToList();
        return labels is [{ } label] ? label : fallback;
    }

    // Track-level: the file's tags.
    [ObservableProperty] private string _editTitle = string.Empty;
    [ObservableProperty] private string _artist = string.Empty;
    [ObservableProperty] private string _year = string.Empty;
    [ObservableProperty] private string _track = string.Empty;
    [ObservableProperty] private string _disc = string.Empty;

    // Album-level: the album file (when the format has one) or the file's tags.
    [ObservableProperty] private string _albumArtist = string.Empty;
    [ObservableProperty] private string _album = string.Empty;
    [ObservableProperty] private string _genre = string.Empty;

    // What a blank field falls back to (the album file entry, or the file/folder name), shown as its placeholder.
    [ObservableProperty] private string _titleHint = NoValue;
    [ObservableProperty] private string _artistHint = NoValue;
    [ObservableProperty] private string _trackHint = NoValue;
    [ObservableProperty] private string _albumHint = NoValue;

    [ObservableProperty] private string? _coverPreviewPath;
    [ObservableProperty] private string _coverCaption = string.Empty;
    [ObservableProperty] private string _albumFileCaption = string.Empty;
    [ObservableProperty] private string _trackFieldsCaption = string.Empty;
    [ObservableProperty] private string _status = string.Empty;

    /// <summary>The grid's rows were dragged into a new order that hasn't been saved yet.</summary>
    [ObservableProperty] private bool _orderChanged;

    partial void OnOrderChangedChanged(bool value) => OnChangesChanged();

    /// <summary>Format-specific fields of the selection's format (shown when every selected file is of the
    /// same format).</summary>
    public ObservableCollection<FormatFieldViewModel> FormatFields { get; } = new();

    public bool HasFormatFields => FormatFields.Count > 0;

    [ObservableProperty] private string _formatFieldsTitle = string.Empty;

    /// <summary>Number of rows with unsaved edits.</summary>
    public int DirtyCount => Files.Count(f => f.IsDirty);

    public bool HasChanges => OrderChanged || Files.Any(f => f.IsDirty);

    private void OnChangesChanged()
    {
        OnPropertyChanged(nameof(DirtyCount));
        OnPropertyChanged(nameof(HasChanges));
        SaveCommand.NotifyCanExecuteChanged();
        RevertCommand.NotifyCanExecuteChanged();
    }

    // Each field edit is staged on the selected rows as it is typed.
    partial void OnEditTitleChanged(string value) =>
        Stage(TitleField, value, _loaded.Title, (f, v) => f.Tags.Title = NullIfBlank(v), s => s.Tags.Title);
    partial void OnArtistChanged(string value) =>
        Stage(ArtistField, value, _loaded.Artist, (f, v) => f.Tags.Artist = NullIfBlank(v), s => s.Tags.Artist);
    partial void OnYearChanged(string value) =>
        Stage(YearField, value, _loaded.Year, (f, v) => f.Tags.Year = ParsePositive(v), s => s.Tags.Year?.ToString());
    partial void OnTrackChanged(string value) =>
        Stage(TrackField, value, _loaded.Track, (f, v) => f.Tags.Track = ParsePositive(v), s => s.Tags.Track?.ToString());
    partial void OnDiscChanged(string value) =>
        Stage(DiscField, value, _loaded.Disc, (f, v) => f.Tags.Disc = ParsePositive(v), s => s.Tags.Disc?.ToString());
    partial void OnAlbumChanged(string value) =>
        Stage(AlbumField, value, _loaded.Album, (f, v) => f.AlbumEdit = v, s => s.Album);
    partial void OnAlbumArtistChanged(string value) =>
        Stage(AlbumArtistField, value, _loaded.AlbumArtist, (f, v) => f.AlbumArtistEdit = v, s => s.AlbumArtist);
    partial void OnGenreChanged(string value) =>
        Stage(GenreField, value, _loaded.Genre, (f, v) => f.GenreEdit = v, s => s.Genre);

    /// <summary>Stages a changed field on every selected row whose format stores it; a field put back to its
    /// loaded value restores each row's own previous value instead (so a blanked "mixed" field stays mixed).</summary>
    private void Stage(string field, string value, string loaded,
        Action<TagFileItemViewModel, string?> set, Func<EditState, string?> original)
    {
        if (_loading) return;
        foreach (var file in _selected)
        {
            if (!file.CanEdit(field)) continue;
            set(file, value == loaded ? original(_snapshot[file]) : value);
            file.Refresh();
        }
        OnChangesChanged();
    }

    /// <summary>Stages a format-specific field on the selected files, as <see cref="Stage"/> does.</summary>
    private void StageFormatField(FormatFieldViewModel field)
    {
        if (_loading) return;
        foreach (var file in _selected)
        {
            if (!file.Extra.ContainsKey(field.Key)) continue;
            file.Extra[field.Key] = field.Value == field.LoadedValue
                ? _snapshot[file].Extra?.GetValueOrDefault(field.Key)
                : NullIfBlank(field.Value);
            file.Refresh();
        }
        OnChangesChanged();
    }

    /// <summary>Called by the view when the grid selection changes.</summary>
    public void SetSelection(IList? items)
    {
        _selected.Clear();
        if (items is not null)
            foreach (var i in items.OfType<TagFileItemViewModel>())
                _selected.Add(i);

        LoadEditorFromSelection();

        OnPropertyChanged(nameof(SelectionCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanEditCover));
        foreach (var name in new[]
                 {
                     nameof(CanEditTitle), nameof(CanEditArtist), nameof(CanEditYear), nameof(CanEditTrack), nameof(CanEditDisc),
                     nameof(CanEditAlbum), nameof(CanEditAlbumArtist), nameof(CanEditGenre),
                     nameof(TitleLabel), nameof(ArtistLabel), nameof(YearLabel), nameof(TrackLabel), nameof(DiscLabel),
                     nameof(AlbumLabel), nameof(AlbumArtistLabel), nameof(GenreLabel),
                 })
            OnPropertyChanged(name);
    }

    private void LoadEditorFromSelection()
    {
        _loading = true;
        EditTitle = Shared(f => f.Tags.Title);
        Artist = Shared(f => f.Tags.Artist);
        Year = Shared(f => f.Tags.Year?.ToString());
        Track = Shared(f => f.PendingTrack?.ToString());
        Disc = Shared(f => f.Tags.Disc?.ToString());

        // Album fields show the effective value (a file's own leftover copy still wins over its album file).
        AlbumArtist = Shared(f => f.AlbumArtist);
        Album = Shared(f => f.AlbumEdit ?? f.Tags.Album ?? f.AlbumEntry?.Album);
        Genre = Shared(f => f.Genre);

        TitleHint = Hint(f => f.AlbumEntry?.Title ?? Path.GetFileNameWithoutExtension(f.FilePath));
        ArtistHint = Hint(f => f.AlbumEntry?.Artist);
        TrackHint = Hint(f => (f.OrderTrack ?? f.AlbumEntry?.TrackNo)?.ToString());
        AlbumHint = Hint(f => f.FolderName);

        _loaded = new Loaded(EditTitle, Artist, AlbumArtist, Album, Genre, Year, Track, Disc);
        _snapshot = _selected.ToDictionary(f => f, f => f.CaptureEdits());
        LoadFormatFields();
        _loading = false;
        UpdateCoverPreview();
        UpdateAlbumFileCaption();
    }

    /// <summary>Shows the format-specific fields when the selection is all one format.</summary>
    private void LoadFormatFields()
    {
        FormatFields.Clear();
        if (_selected.Count > 0 && _selected.Select(f => f.Plugin).Distinct().Count() == 1)
        {
            var first = _selected[0];
            FormatFieldsTitle = $"{first.FormatName} fields";
            foreach (var spec in first.ExtraFields)
            {
                var values = _selected.Select(f => f.Extra.GetValueOrDefault(spec.Key) ?? string.Empty)
                    .Distinct(spec.Kind == CodecTagFieldKind.Choice ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                    .ToList();
                var field = new FormatFieldViewModel(spec, StageFormatField);
                field.Load(values);
                FormatFields.Add(field);
            }
        }
        OnPropertyChanged(nameof(HasFormatFields));
    }

    /// <summary>Returns the value common to every selected file, or empty when they differ / none.</summary>
    private string Shared(Func<TagFileItemViewModel, string?> get)
    {
        if (_selected.Count == 0) return string.Empty;
        var first = get(_selected[0]) ?? string.Empty;
        return _selected.All(f => (get(f) ?? string.Empty) == first) ? first : string.Empty;
    }

    private string Hint(Func<TagFileItemViewModel, string?> get) =>
        Shared(get) is { Length: > 0 } value ? value : NoValue;

    /// <summary>Adds files of the formats codec plugins handle (expanding any dropped folders), skipping duplicates.</summary>
    public void AddFiles(IEnumerable<string> paths)
    {
        var added = 0;
        var batch = ExpandToFiles(paths, IsTaggable)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => !Files.Any(f => string.Equals(f.FilePath, path, StringComparison.OrdinalIgnoreCase)))
            .Select(CreateRow)
            // Album by album (its album file, else its folder), then in track order.
            .OrderBy(f => f.AlbumEntry?.AlbumFilePath ?? Path.GetDirectoryName(f.FilePath), StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Disc ?? 0)
            .ThenBy(f => f.Track ?? int.MaxValue)
            .ThenBy(f => f.FileName, StringComparer.OrdinalIgnoreCase);
        foreach (var file in batch)
        {
            Files.Add(file);
            added++;
        }

        if (added > 0)
        {
            Status = $"Added {added} file(s).";
            if (OrderChanged)
                UpdateOrderPreview();
        }
        OnChangesChanged();
    }

    private bool IsTaggable(string path) => _codecs.ForPath(path) is not null;

    private TagFileItemViewModel CreateRow(string path) => new(path, _codecs.ForPath(path)!);

    private static IEnumerable<string> ExpandToFiles(IEnumerable<string> paths, Func<string, bool> include)
    {
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories); }
                catch { continue; }
                foreach (var f in files)
                    if (include(f))
                        yield return f;
            }
            else if (File.Exists(path) && include(path))
            {
                yield return path;
            }
        }
    }

    /// <summary>Moves rows (keeping their relative order) to sit before the row currently at
    /// <paramref name="insertIndex"/> (<see cref="Files"/>.Count = the end). The grid order is the track
    /// order, saved to the album files (formats that have them) or as track numbers (other formats) on save.</summary>
    public void MoveFiles(IReadOnlyCollection<TagFileItemViewModel> items, int insertIndex)
    {
        var moving = Files.Where(items.Contains).ToList();
        if (moving.Count == 0) return;

        var target = Files.Where(f => !items.Contains(f)).ToList();
        var at = Math.Clamp(insertIndex - Files.Take(insertIndex).Count(items.Contains), 0, target.Count);
        target.InsertRange(at, moving);

        if (target.SequenceEqual(Files)) return;

        // Rebuilt rather than moved item by item: the DataGrid doesn't redraw rows on Move changes.
        Files.Clear();
        foreach (var file in target)
            Files.Add(file);
        OrderChanged = true;
        UpdateOrderPreview();
    }

    /// <summary>Shows the track numbers the unsaved row order would give, so the grid reflects it.</summary>
    private void UpdateOrderPreview()
    {
        IReadOnlyDictionary<string, int>? numbers = null;
        if (OrderChanged)
        {
            try
            {
                numbers = AlbumFileTrackOrder(save: false);
            }
            catch (Exception ex) { Status = $"Couldn't preview the track order: {ex.Message}"; }
        }

        foreach (var file in Files)
        {
            file.OrderTrack = numbers is not null && numbers.TryGetValue(Path.GetFullPath(file.FilePath), out var n) ? n : null;
            file.Refresh();
        }
    }

    /// <summary>
    /// The track numbers the grid order gives every row (keyed by full path): rows of formats with album files
    /// take their position in the album file (which <paramref name="save"/> writes), the others their position
    /// among the rows of the same folder (<see cref="InFileTrackOrder"/>).
    /// </summary>
    private Dictionary<string, int> AlbumFileTrackOrder(bool save)
    {
        var numbers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in Files.Where(f => f.AlbumFiles is not null).GroupBy(f => f.AlbumFiles!))
            foreach (var (path, n) in group.Key.WriteTrackOrder(group.Select(f => f.FilePath).ToList(), save))
                numbers[path] = n;
        foreach (var (path, n) in InFileTrackOrder())
            numbers[path] = n;
        return numbers;
    }

    /// <summary>Track numbers for the rows of formats without album files from the grid order: their position
    /// among those rows in the same folder (keyed by full path).</summary>
    private Dictionary<string, int> InFileTrackOrder()
    {
        var numbers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in Files.Where(f => !f.AlbumFieldsInAlbumFile && f.CanEdit(TrackField))
                     .GroupBy(f => Path.GetDirectoryName(Path.GetFullPath(f.FilePath)), StringComparer.OrdinalIgnoreCase))
        {
            var n = 0;
            foreach (var file in folder)
                numbers[Path.GetFullPath(file.FilePath)] = ++n;
        }
        return numbers;
    }

    /// <summary>Stages an image as the selection's album cover. On save it is handed to the album file of
    /// formats that have one (the MIDI plugin copies it to <c>cover.&lt;ext&gt;</c> beside the M3U), or copied
    /// into the folder of other formats' files; cover art stays a sidecar file, never embedded.</summary>
    public void PickCover(string imagePath)
    {
        if (_selected.Count == 0) return;
        if (!ImageExtensions.Contains(Path.GetExtension(imagePath))) return;

        foreach (var file in _selected)
        {
            file.CoverEdit = imagePath;
            file.Refresh();
        }
        UpdateCoverPreview();
        OnChangesChanged();
    }

    private void UpdateCoverPreview()
    {
        if (Shared(f => f.CoverEdit) is { Length: > 0 } pending)
        {
            var where = _selected.All(f => f.AlbumFieldsInAlbumFile) ? "beside the album file"
                : _selected.All(f => !f.AlbumFieldsInAlbumFile) ? "in the files' folder, replacing its cover image"
                : "beside the album file or in the files' folder, replacing its cover image";
            CoverPreviewPath = pending;
            CoverCaption = $"New cover, saved as cover{Path.GetExtension(pending).ToLowerInvariant()} {where}.";
            return;
        }

        var path = Shared(f => f.CoverPath);
        CoverPreviewPath = path.Length > 0 ? path : null;
        CoverCaption = _selected.Count == 0 ? string.Empty
            : path.Length == 0 && _selected.Any(f => f.CoverPath is not null) ? "Covers differ across the selection."
            : path.Length == 0 ? "No cover image."
            : _selected[0].CoverOrigin switch
            {
                CoverOrigin.File => $"From the file's cover tag: {Path.GetFileName(path)}",
                CoverOrigin.AlbumFile => $"From the album file: {Path.GetFileName(path)}",
                _ => $"Folder image: {Path.GetFileName(path)}",
            };
    }

    private void UpdateAlbumFileCaption()
    {
        var withAlbumFile = _selected.Where(f => f.AlbumFieldsInAlbumFile).ToList();
        var inFile = _selected.Count - withAlbumFile.Count;
        TrackFieldsCaption = withAlbumFile.Count == 0
            ? "Saved in each file's tags. A blank field falls back to the file name shown in grey."
            : "Saved in each file. A blank field falls back to the album file entry or file name shown in grey.";

        var albumFiles = withAlbumFile
            .Select(f => f.AlbumFileName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var caption = albumFiles switch
        {
            [] => string.Empty,
            [{ } name] => $"Album fields are saved in the album file {name}.",
            [null] => "These files have no album file yet. Saving album fields adds them to the album file in their folder, or creates one named after the album.",
            _ => "Album fields are saved in each file's own album file (files without one are added to their folder's)."
        };
        var formats = string.Join(", ", withAlbumFile.Select(f => f.FormatName).Distinct());
        AlbumFileCaption = inFile == 0 ? caption
            : withAlbumFile.Count == 0 ? "Album fields are saved in each file's tags."
            : $"{formats} files: {caption} Other files keep album fields in their own tags.";
    }

    private bool CanSave() => HasChanges && !IsBusy;

    /// <summary>Writes every row's staged edits, and the track order if the rows were reordered.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task Save()
    {
        if (!HasChanges) return;
        IsBusy = true;
        OnChangesChanged();
        try
        {
            var dirty = Files.Where(f => f.IsDirty).ToList();

            // Formats with album files: those first, since the files' own copies of album fields are only
            // cleared once their new home is written. Rows staged with the same album edits go together.
            var albumFiles = new List<string>();
            foreach (var group in dirty.Where(f => f.AlbumFiles is not null && f.HasAlbumEdits)
                         .GroupBy(f => (Plugin: f.AlbumFiles!, f.AlbumEdit, f.AlbumArtistEdit, f.GenreEdit, f.CoverEdit)))
            {
                var (plugin, album, albumArtist, genre, cover) = group.Key;
                var changes = new CodecAlbumChanges
                {
                    SetAlbum = album is not null, Album = NullIfBlank(album),
                    SetAlbumArtist = albumArtist is not null, AlbumArtist = NullIfBlank(albumArtist),
                    SetGenre = genre is not null, Genre = NullIfBlank(genre),
                    CoverSourcePath = cover,
                };
                var paths = group.Select(f => f.FilePath).ToList();
                foreach (var written in await Task.Run(() => plugin.WriteAlbum(paths, changes)))
                    if (!albumFiles.Contains(written, StringComparer.OrdinalIgnoreCase))
                        albumFiles.Add(written);
            }

            // The plugin cleared the files' own copies of those fields; keep them cleared when the rows' track
            // fields are written below.
            foreach (var file in dirty.Where(f => f.AlbumFieldsInAlbumFile))
            {
                if (file.AlbumEdit is not null) file.Tags.Album = null;
                if (file.AlbumArtistEdit is not null) file.Tags.AlbumArtist = null;
                if (file.GenreEdit is not null) file.Tags.Genre = null;
            }

            // Other formats: album fields go into the file's own tags, a new cover becomes the folder image.
            var covers = 0;
            foreach (var file in dirty.Where(f => !f.AlbumFieldsInAlbumFile))
                file.ApplyAlbumEdits();
            foreach (var group in dirty.Where(f => !f.AlbumFieldsInAlbumFile && f.CoverEdit is not null)
                         .GroupBy(f => (Folder: Path.GetDirectoryName(Path.GetFullPath(f.FilePath))!, Cover: f.CoverEdit!)))
            {
                await Task.Run(() => FolderCover.Install(group.Key.Cover, group.Key.Folder));
                covers++;
            }

            // Track order: album files take the grid order, and their files that also carry their own track
            // number get it updated so the tag doesn't contradict (and override) the new position; formats
            // without album files get their position as their track number. A number typed by the user wins.
            var orderSaved = OrderChanged;
            if (orderSaved)
            {
                var numbers = await Task.Run(() => AlbumFileTrackOrder(save: true));
                foreach (var file in Files)
                {
                    if (file.TrackEdited || !numbers.TryGetValue(Path.GetFullPath(file.FilePath), out var n)) continue;
                    if (!file.AlbumFieldsInAlbumFile || file.Tags.Track is { } track && n != track)
                        file.Tags.Track = n;
                }
            }

            var toWrite = Files.Where(f => f.TagsChanged).ToList();
            foreach (var file in toWrite)
                await file.WriteAsync();
            OrderChanged = false;

            // Album file edits can change rows that weren't edited (same album), so refresh them all.
            foreach (var file in Files)
                file.Reload();

            LoadEditorFromSelection(); // resolve any "mixed" fields now that values match
            Status = $"Saved {toWrite.Count} file(s)"
                     + (orderSaved ? "; saved track order" : string.Empty)
                     + (albumFiles.Count > 0 ? $"; updated {string.Join(", ", albumFiles.Select(Path.GetFileName))}" : string.Empty)
                     + (covers > 0 ? $"; installed {covers} folder cover(s)" : string.Empty)
                     + ".";

            Status += await RescanLibraryAsync(albumFiles);
        }
        catch (Exception ex)
        {
            Status = $"Save failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            OnChangesChanged();
        }
    }

    /// <summary>Re-reads the edited files into the library, along with the other files of every album file
    /// that was written (their album fields and track numbers come from it). Returns a status suffix.</summary>
    private async Task<string> RescanLibraryAsync(IReadOnlyList<string> albumFiles)
    {
        var paths = Files.Select(f => f.FilePath).ToList();
        foreach (var albumFile in albumFiles)
        {
            // The album file's own folder, plus disc folders directly below it.
            var dir = Path.GetDirectoryName(albumFile)!;
            try
            {
                paths.AddRange(ExpandToFiles(Directory.EnumerateDirectories(dir).Prepend(dir)
                    .SelectMany(d => Directory.EnumerateFiles(d)), f => _codecs.ForPath(f) is ICodecAlbumFiles));
            }
            catch
            {
                // Unreadable folder: its files just aren't rescanned.
            }
        }

        try
        {
            var updated = await _sync.SyncFilesAsync(paths);
            return updated > 0 ? $" Updated {updated} track(s) in the library." : string.Empty;
        }
        catch (Exception ex)
        {
            return $" Library rescan failed: {ex.Message}";
        }
    }

    private bool CanRevert() => HasChanges && !IsBusy;

    /// <summary>Discards every staged edit, re-reading the files from disk. The rows keep their current
    /// order, but it is no longer pending.</summary>
    [RelayCommand(CanExecute = nameof(CanRevert))]
    private void Revert()
    {
        foreach (var file in Files)
            file.Reload();
        OrderChanged = false;
        LoadEditorFromSelection();
        Status = "Discarded unsaved changes.";
        OnChangesChanged();
    }

    [RelayCommand]
    private void RemoveSelected()
    {
        foreach (var file in _selected.ToList())
            Files.Remove(file);
        SetSelection(null);
        UpdateOrderPreview();
        OnChangesChanged();
    }

    [RelayCommand]
    private void Clear()
    {
        Files.Clear();
        OrderChanged = false;
        SetSelection(null);
        Status = string.Empty;
        OnChangesChanged();
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int? ParsePositive(string? value) =>
        int.TryParse(value?.Trim(), out var n) && n > 0 ? n : null;

    private readonly record struct Loaded(
        string Title, string Artist, string AlbumArtist, string Album,
        string Genre, string Year, string Track, string Disc);
}
