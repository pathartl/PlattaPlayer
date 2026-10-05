using System.Globalization;
using PlattaPlayer.Codecs.Abstractions;
using PlattaPlayer.Codecs.AlbumFiles;

namespace PlattaPlayer.Codecs.Gbs;

// The playlists as album files (ICodecAlbumFiles): the album artist, genre and cover are directives of the
// playlist listing the file, and the track order is its entries' order. The game stays in the GBS header.
public sealed partial class GbsCodecPlugin
{
    /// <summary>The game is the header's; only what GBS has no field for is the playlist's.</summary>
    public IReadOnlyCollection<string> AlbumFileKeys { get; } = [CodecTagKeys.AlbumArtist, CodecTagKeys.Genre];

    /// <summary>The playlist's album cover (<c>#EXTIMG:</c>), else null for the folder's image.</summary>
    public string? FindCover(string path) =>
        _playlists.FindAlbum(path)?.CoverPath is { } cover && File.Exists(cover) ? cover : null;

    public CodecAlbumEntry? FindAlbumEntry(string path) =>
        _playlists.FindAlbum(path) is { } album
            ? new CodecAlbumEntry(album.PlaylistPath, null, album.AlbumArtist, album.Genre, album.CoverPath, null, null, 1)
            : null;

    public IReadOnlyList<string> WriteAlbum(IReadOnlyCollection<string> paths, CodecAlbumChanges changes)
    {
        // The game is never written here (see AlbumFileKeys).
        if (!changes.SetAlbumArtist && !changes.SetGenre && changes.CoverSourcePath is null) return [];
        lock (_writeGate)
        {
            var playlists = paths
                .Select(p => _playlists.FindAlbum(p)?.PlaylistPath ?? EnsureListed(p, 0).PlaylistPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var playlistPath in playlists)
            {
                var document = NezDocument.Load(playlistPath);
                if (changes.SetAlbumArtist) M3uSidecarWriter.SetDirective(document.Lines, M3u.AlbumArtist, changes.AlbumArtist);
                if (changes.SetGenre) M3uSidecarWriter.SetDirective(document.Lines, M3u.Genre, changes.Genre);
                if (changes.CoverSourcePath is { } cover)
                    M3uSidecarWriter.SetDirective(document.Lines, M3u.Image, M3uSidecarWriter.InstallCover(cover, Path.GetDirectoryName(playlistPath)!));
                Save(document, playlistPath);
            }
            return playlists;
        }
    }

    /// <summary>Orders songs, given as <c>path::song</c> (the host's keys for the songs of a multi-song file).</summary>
    public IReadOnlyDictionary<string, int> WriteTrackOrder(IReadOnlyList<string> pathsInOrder, bool save)
    {
        var numbers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var songs = pathsInOrder.Select(ParseKey).OfType<(string Path, int Song)>();
        foreach (var file in songs.GroupBy(s => s.Path, StringComparer.OrdinalIgnoreCase))
        {
            IReadOnlyDictionary<int, int> positions;
            lock (_writeGate)
                positions = ReorderSongs(file.Key, file.Select(s => s.Song).ToList(), save);
            foreach (var (path, song) in file)
                if (positions.TryGetValue(song, out var n))
                    numbers[$"{path}::{song.ToString(CultureInfo.InvariantCulture)}"] = n;
        }
        return numbers;
    }

    /// <summary>Moves the song to <paramref name="track"/> in its file's playlist.</summary>
    private void MoveSong(string path, int subsong, int track)
    {
        EnsureListed(path, subsong);
        var header = ReadHeader(path) ?? throw new InvalidDataException($"{Path.GetFileName(path)} is not a GBS file.");
        var order = Songs(path, header).Select(s => s.Song).Where(s => s != subsong).ToList();
        order.Insert(Math.Clamp(track - 1, 0, order.Count), subsong);
        ReorderSongs(path, order, save: true);
    }

    /// <summary>
    /// Puts the given songs in the given order in the file's playlist (each taking a slot one of them had, with
    /// the lines above it, so other songs keep their places) and returns every listed song's resulting track
    /// number. A file no playlist lists gets the <c>&lt;file&gt;.m3u</c> listing every song (in memory only
    /// unless <paramref name="save"/>). Songs spread over several playlists can't be reordered.
    /// </summary>
    private IReadOnlyDictionary<int, int> ReorderSongs(string path, IReadOnlyList<int> order, bool save)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full)!;
        var header = ReadHeader(full) ?? throw new InvalidDataException($"{Path.GetFileName(path)} is not a GBS file.");

        var entries = _playlists.Find(full);
        string playlistPath;
        NezDocument document;
        if (entries.Count == 0)
        {
            playlistPath = Path.ChangeExtension(full, ".m3u");
            document = File.Exists(playlistPath) ? NezDocument.Load(playlistPath) : NezDocument.CreateNew();
            for (var song = 0; song < header.SongCount; song++)
                document.Lines.Add(NezEntry.NewLine(Path.GetFileName(full), song, null, null, null));
        }
        else
        {
            var playlists = entries.Select(e => e.PlaylistPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (playlists.Count > 1)
                throw new InvalidOperationException(
                    $"The songs of {Path.GetFileName(path)} are listed in {playlists.Count} playlists, so their order can't be changed. Merge them into one playlist first.");
            playlistPath = playlists[0];
            document = NezDocument.Load(playlistPath);
        }

        string? Key(string line) =>
            NezEntry.Parse(playlistPath, 0, line) is { } e && NezPlaylistIndex.Lists(dir, e, full) && e.Song < header.SongCount
                ? e.Song.ToString(CultureInfo.InvariantCulture)
                : null;

        M3uSidecarWriter.MoveEntries(document.Lines, Key, order.Select(s => s.ToString(CultureInfo.InvariantCulture)).ToList());
        if (save) Save(document, playlistPath);

        // Track numbers as the songs now stand: the first listing of each song counts.
        var positions = new Dictionary<int, int>();
        foreach (var line in document.Lines)
            if (Key(line) is { } key && int.Parse(key, CultureInfo.InvariantCulture) is var song && !positions.ContainsKey(song))
                positions[song] = positions.Count + 1;
        return positions;
    }

    // "C:\…\Game.gbs::3" → (full path, 3); null for a key without a song.
    private static (string Path, int Song)? ParseKey(string key)
    {
        var separator = key.LastIndexOf("::", StringComparison.Ordinal);
        return separator > 0 && int.TryParse(key[(separator + 2)..], NumberStyles.None, CultureInfo.InvariantCulture, out var song)
            ? (Path.GetFullPath(key[..separator]), song)
            : null;
    }
}
