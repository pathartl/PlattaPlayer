using System.Text.RegularExpressions;
using PlattaPlayer.Codecs.AlbumFiles;

namespace PlattaPlayer.Codecs.Vgm;

/// <summary>
/// What a VGM's folder says about it. VGM rips come as a folder per game (a vgmrips.net pack: the songs, an
/// M3U playlist in play order and an image), and GD3 has neither a track number nor an album artist, so these
/// come from the folder when the playlist doesn't set them:
/// <list type="bullet">
/// <item>The track number is the file's position in its playlist (see <see cref="M3uSidecarIndex"/>: in its
/// folder or the one above), else the number its name starts with ("03 Green Hill Zone.vgz").</item>
/// <item>The album artist, unless the playlist names one, is every author of the game's songs in the folder, in track order, so a game whose
/// songs credit different composers stays one album.</item>
/// </list>
/// A folder is read again when its time stamp changes (a file added, removed or replaced, as tag writes do),
/// and of its files only those whose size or time stamp changed.
/// </summary>
internal sealed partial class VgmFolder
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg"];
    private static readonly string[] StandardCovers = ["cover.jpg", "cover.jpeg", "cover.png", "folder.jpg", "folder.jpeg", "folder.png"];

    private readonly M3uSidecarIndex _playlists;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, FileEntry> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Snapshot> _folders = new(StringComparer.OrdinalIgnoreCase);

    public VgmFolder(M3uSidecarIndex playlists) => _playlists = playlists;

    private sealed record FileEntry(DateTime Stamp, long Size, string? Game, IReadOnlyList<string> Authors);

    /// <summary>A folder as of its time stamp: each game's authors.</summary>
    private sealed record Snapshot(DateTime Stamp, IReadOnlyDictionary<string, IReadOnlyList<string>> AuthorsByGame);

    /// <summary>The file's track number, or null when neither a playlist nor its name gives one.</summary>
    public int? TrackNumber(string path) => _playlists.Find(path)?.TrackNo ?? NameNumber(Path.GetFileName(path));

    /// <summary>The authors of every song of <paramref name="game"/> in the file's folder, in track order.</summary>
    public IReadOnlyList<string> GameAuthors(string path, string? game) =>
        !string.IsNullOrWhiteSpace(game) && Folder(Path.GetDirectoryName(path)) is { } folder
        && folder.AuthorsByGame.TryGetValue(game, out var authors)
            ? authors
            : [];

    /// <summary>Forgets what was read about a folder (after a file in it was rewritten).</summary>
    public void Invalidate(string path)
    {
        if (Path.GetDirectoryName(path) is { } dir)
            lock (_gate)
                _folders.Remove(dir);
    }

    /// <summary>
    /// The folder's image when it has no cover.* / folder.* (which the host finds itself): the image named as
    /// the folder's playlist (vgmrips packs ship "Game.png" beside "Game.m3u"), else the only image there.
    /// </summary>
    public static string? FindCover(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
        if (StandardCovers.Any(name => File.Exists(Path.Combine(dir, name)))) return null;

        var images = Directory.EnumerateFiles(dir)
            .Where(f => ImageExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .ToList();
        foreach (var playlist in Directory.EnumerateFiles(dir, "*.m3u*"))
        {
            var stem = Path.GetFileNameWithoutExtension(playlist);
            var match = images.FirstOrDefault(i => string.Equals(Path.GetFileNameWithoutExtension(i), stem, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }
        return images.Count == 1 ? images[0] : null;
    }

    /// <summary>"A; B" from GD3's "A, B", "A & B" or "A / B": the host's multi-value form.</summary>
    public static IReadOnlyList<string> SplitAuthors(string? authors)
    {
        if (string.IsNullOrWhiteSpace(authors)) return [];
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in AuthorSeparator().Split(authors))
        {
            var name = part.Trim();
            if (name.Length > 0 && seen.Add(name)) result.Add(name);
        }
        return result;
    }

    // The folder's snapshot, read again when the folder's time stamp has changed (a file added, removed or
    // replaced, as WriteTags does). Unchanged files keep their cached tags.
    private Snapshot? Folder(string? dir)
    {
        if (string.IsNullOrEmpty(dir)) return null;
        try
        {
            var info = new DirectoryInfo(dir);
            if (!info.Exists) return null;
            var stamp = info.LastWriteTimeUtc;
            lock (_gate)
                if (_folders.TryGetValue(dir, out var cached) && cached.Stamp == stamp)
                    return cached;

            var snapshot = Read(info, stamp);
            lock (_gate)
                _folders[dir] = snapshot;
            return snapshot;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private Snapshot Read(DirectoryInfo dir, DateTime stamp)
    {
        var songs = dir.EnumerateFiles().Where(f => f.Extension.Equals(".vgm", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".vgz", StringComparison.OrdinalIgnoreCase)).ToList();

        var tracks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var song in songs)
            if (TrackNumber(song.FullName) is { } number)
                tracks[song.Name] = number;

        // Each game's authors, over its songs in track order.
        var authorsByGame = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var ordered = songs
            .Select(f => (File: f, Entry: Entry(f)))
            .Where(s => s.Entry.Game is not null)
            .OrderBy(s => tracks.TryGetValue(s.File.Name, out var t) ? t : int.MaxValue)
            .ThenBy(s => s.File.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var game in ordered.GroupBy(s => s.Entry.Game!, StringComparer.OrdinalIgnoreCase))
            authorsByGame[game.Key] = game.SelectMany(s => s.Entry.Authors).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        return new Snapshot(stamp, authorsByGame);
    }

    // The file's game and authors, read again only when its size or time stamp has changed.
    private FileEntry Entry(FileInfo file)
    {
        lock (_gate)
            if (_files.TryGetValue(file.FullName, out var cached) && cached.Stamp == file.LastWriteTimeUtc && cached.Size == file.Length)
                return cached;

        var gd3 = VgmFile.TryRead(file.FullName)?.Gd3;
        var entry = new FileEntry(file.LastWriteTimeUtc, file.Length, VgmTagMap.Game(gd3), SplitAuthors(VgmTagMap.Author(gd3)));
        lock (_gate)
            _files[file.FullName] = entry;
        return entry;
    }

    /// <summary>The number a file's name starts with ("03 Green Hill Zone.vgz"), or null.</summary>
    public static int? NameNumber(string name) =>
        LeadingNumber().Match(name) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var n) && n > 0 ? n : null;

    [GeneratedRegex(@"^(\d{1,4})(?=[\s._\-])")]
    private static partial Regex LeadingNumber();

    // Commas, semicolons, ampersands and slashes between names.
    [GeneratedRegex(@"\s*[,;&/]\s*")]
    private static partial Regex AuthorSeparator();
}
