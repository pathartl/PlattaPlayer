using System.Text;
using PlattaPlayer.Codecs.Abstractions;

namespace PlattaPlayer.Codecs.Midi;

/// <summary>
/// Finds album metadata for media files in M3U playlists lying next to them. MIDI has no standard tags,
/// so an album folder can describe itself with an extended M3U that lists its files in order:
/// <code>
/// #EXTM3U
/// #EXTALB:Album title          (or #PLAYLIST:)
/// #EXTART:Album artist
/// #EXTGENRE:Genre
/// #EXTIMG:cover.jpg
/// #EXTINF:180,Track artist - Track title
/// 01 First track.mid
/// </code>
/// Playlists (<c>.m3u</c>/<c>.m3u8</c>) are looked for in the file's own folder, then its parent (for albums
/// split into disc folders); the first, by name, that lists the file wins. Parsed playlists are cached
/// until the playlist file changes.
/// </summary>
internal sealed class M3uSidecarIndex
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (DateTime WriteTime, Playlist Playlist)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public CodecAlbumEntry? Find(string mediaPath)
    {
        try
        {
            var target = Path.GetFullPath(mediaPath);
            var dir = Path.GetDirectoryName(target);
            for (var depth = 0; depth < 2 && !string.IsNullOrEmpty(dir); depth++, dir = Path.GetDirectoryName(dir))
            {
                foreach (var playlistPath in PlaylistsIn(dir))
                {
                    var playlist = Load(playlistPath);
                    if (playlist?.Find(target) is { } info)
                        return info;
                }
            }
        }
        catch
        {
            // Unreadable folder or playlist — no sidecar metadata.
        }

        return null;
    }

    /// <summary>Drops a cached playlist (after writing it, so a same-timestamp rewrite isn't missed).</summary>
    public void Invalidate(string playlistPath)
    {
        lock (_gate) _cache.Remove(Path.GetFullPath(playlistPath));
    }

    /// <summary>The playlists directly in <paramref name="dir"/>, ordered by name.</summary>
    internal static IEnumerable<string> PlaylistsIn(string dir)
    {
        string[] files;
        try { files = Directory.GetFiles(dir); }
        catch { yield break; }

        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var ext = Path.GetExtension(file);
            if (ext.Equals(".m3u", StringComparison.OrdinalIgnoreCase) || ext.Equals(".m3u8", StringComparison.OrdinalIgnoreCase))
                yield return file;
        }
    }

    private Playlist? Load(string path)
    {
        var writeTime = File.GetLastWriteTimeUtc(path);
        lock (_gate)
        {
            if (_cache.TryGetValue(path, out var cached) && cached.WriteTime == writeTime)
                return cached.Playlist;
        }

        Playlist? playlist;
        try { playlist = Playlist.Parse(path); }
        catch { playlist = null; }
        if (playlist is null) return null;

        lock (_gate) _cache[path] = (writeTime, playlist);
        return playlist;
    }

    private sealed class Playlist
    {
        private readonly Dictionary<string, CodecAlbumEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

        public CodecAlbumEntry? Find(string fullPath) => _entries.GetValueOrDefault(fullPath);

        public static Playlist Parse(string path)
        {
            var fullPath = Path.GetFullPath(path);
            var dir = Path.GetDirectoryName(fullPath)!;
            var root = dir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

            string? album = null, playlistTitle = null, albumArtist = null, genre = null, cover = null;
            var pending = new List<(string Path, string? Title, string? Artist)>();
            string? infTitle = null, infArtist = null;

            foreach (var raw in M3uDocument.Load(path).Lines)
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;

                if (line[0] == '#')
                {
                    if (M3u.Directive(line, M3u.Album) is { } a) album = a;
                    else if (M3u.Directive(line, M3u.PlaylistTitle) is { } p) playlistTitle = p;
                    else if (M3u.Directive(line, M3u.AlbumArtist) is { } ar) albumArtist = ar;
                    else if (M3u.Directive(line, M3u.Genre) is { } g) genre = g;
                    else if (M3u.Directive(line, M3u.Image) is { } img) cover = M3u.ResolveContained(dir, root, img);
                    else if (M3u.Directive(line, "#EXTINF:") is { } inf) (infArtist, infTitle) = SplitDisplay(inf);
                    continue;
                }

                if (M3u.Resolve(dir, line) is { } entry)
                    pending.Add((entry, infTitle, infArtist));
                infTitle = infArtist = null;
            }

            // Header directives describe the whole playlist wherever they appear. Track numbers count
            // within each folder, so one playlist above disc folders numbers every disc from 1.
            var playlist = new Playlist();
            var perFolder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var (entry, title, artist) in pending)
            {
                if (playlist._entries.ContainsKey(entry)) continue;
                var folder = Path.GetDirectoryName(entry) ?? string.Empty;
                var trackNo = perFolder[folder] = perFolder.GetValueOrDefault(folder) + 1;
                playlist._entries.Add(entry, new CodecAlbumEntry(
                    fullPath, album ?? playlistTitle, albumArtist, genre, cover, title, artist, trackNo));
            }
            return playlist;
        }

        /// <summary>
        /// <c>#EXTINF:&lt;seconds&gt;[ attributes],&lt;display&gt;</c>; the display text is <c>Artist - Title</c>
        /// or just a title.
        /// </summary>
        private static (string? Artist, string? Title) SplitDisplay(string inf)
        {
            var comma = inf.IndexOf(',');
            if (comma < 0) return (null, null);
            var display = inf[(comma + 1)..].Trim();
            if (display.Length == 0) return (null, null);

            var dash = display.IndexOf(" - ", StringComparison.Ordinal);
            if (dash <= 0) return (null, display);
            var artist = display[..dash].Trim();
            var title = display[(dash + 3)..].Trim();
            return (artist.Length == 0 ? null : artist, title.Length == 0 ? display : title);
        }
    }
}

/// <summary>M3U directive names and path helpers shared by the sidecar reader and writer.</summary>
internal static class M3u
{
    public const string Header = "#EXTM3U";
    public const string Album = "#EXTALB:";
    public const string PlaylistTitle = "#PLAYLIST:";
    public const string AlbumArtist = "#EXTART:";
    public const string Genre = "#EXTGENRE:";
    public const string Image = "#EXTIMG:";

    public static string? Directive(string line, string name)
    {
        if (!line.StartsWith(name, StringComparison.OrdinalIgnoreCase)) return null;
        var value = line[name.Length..].Trim();
        return value.Length == 0 ? null : value;
    }

    /// <summary>An entry: a path relative to the playlist, an absolute path, or a <c>file:</c> URI.</summary>
    public static string? Resolve(string dir, string entry)
    {
        try
        {
            if (Uri.TryCreate(entry, UriKind.Absolute, out var uri) && !uri.IsFile)
                return null; // streams and other remote entries
            var local = uri is { IsFile: true } ? uri.LocalPath : entry;
            return Path.GetFullPath(Path.Combine(dir, local));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Like <see cref="Resolve"/>, but only inside the playlist's folder subtree (it names a file to read).</summary>
    public static string? ResolveContained(string dir, string root, string entry)
        => Resolve(dir, entry) is { } full && full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
}

/// <summary>
/// The lines of an M3U file plus what's needed to write it back the way it was found (encoding, BOM,
/// line endings). <c>.m3u8</c> is UTF-8 by definition. Plain <c>.m3u</c> is whatever the writer used:
/// UTF-8 when it decodes as such (or has a BOM), otherwise the legacy Latin-1 reading.
/// </summary>
internal sealed class M3uDocument
{
    private M3uDocument(List<string> lines, bool latin1, bool bom, string newLine)
    {
        Lines = lines;
        _latin1 = latin1;
        _bom = bom;
        _newLine = newLine;
    }

    private readonly bool _latin1;
    private readonly bool _bom;
    private readonly string _newLine;

    /// <summary>The lines without their terminators.</summary>
    public List<string> Lines { get; }

    public static M3uDocument CreateNew() => new(new List<string> { M3u.Header }, false, false, "\r\n");

    public static M3uDocument Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        string text;
        var latin1 = false;
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            text = Encoding.Latin1.GetString(bytes);
            latin1 = true;
        }

        var bom = text.StartsWith('﻿');
        if (bom) text = text[1..];
        var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1); // the final terminator, not an empty line
        return new M3uDocument(lines, latin1, bom, newLine);
    }

    /// <summary>Writes the lines back, keeping the original encoding unless new text can't be
    /// represented in Latin-1 (then it becomes UTF-8, which the reader tries first anyway).</summary>
    public void Save(string path)
    {
        var text = string.Join(_newLine, Lines) + _newLine;
        var latin1 = _latin1 && Encoding.Latin1.GetString(Encoding.Latin1.GetBytes(text)) == text;
        Encoding encoding = latin1 ? Encoding.Latin1 : new UTF8Encoding(_bom);

        // Write beside the target and swap in, so a failed write never truncates the playlist.
        var temp = path + ".tmp";
        File.WriteAllText(temp, text, encoding);
        File.Move(temp, path, overwrite: true);
    }
}
