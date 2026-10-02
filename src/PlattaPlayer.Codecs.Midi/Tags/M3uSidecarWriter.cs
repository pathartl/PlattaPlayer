using PlattaPlayer.Codecs.Abstractions;

namespace PlattaPlayer.Codecs.Midi;

/// <summary>
/// Writes album-level MIDI metadata into M3U sidecar playlists (the format <see cref="M3uSidecarIndex"/>
/// reads). The playlist edited for a file is the one the reader would pick for it, so edits always take
/// effect: the playlist already listing it, else the first playlist in its folder (the file is appended),
/// else a new <c>&lt;folder&gt;.m3u8</c>. The track order lives here too, as the order of the entries. Lines the writer doesn't own — entries, <c>#EXTINF</c>, unknown
/// directives — are kept as they are.
/// </summary>
internal static class M3uSidecarWriter
{
    // Album directives kept together right after #EXTM3U, where new ones are inserted.
    private static readonly string[] HeaderDirectives =
    {
        M3u.PlaylistTitle, M3u.Album, M3u.AlbumArtist, M3u.Genre, M3u.Image
    };

    public static IReadOnlyList<string> Apply(
        M3uSidecarIndex index, IReadOnlyCollection<string> mediaPaths, CodecAlbumChanges changes)
    {
        var targets = Targets(index, mediaPaths, changes.SetAlbum ? changes.Album : null);
        foreach (var (playlist, files) in targets)
        {
            Update(playlist, files.Where(f => !f.Listed).Select(f => f.Path).ToList(), changes);
            index.Invalidate(playlist);
        }

        return targets.Keys.ToList();
    }

    /// <summary>
    /// Reorders each playlist so the given files appear in the given order. The files take over the
    /// slots they already occupied (each entry moving with the <c>#EXTINF</c> and other per-entry lines
    /// above it), so entries not being reordered stay where they are; files not listed yet are appended.
    /// Returns each file's resulting track number: its position among the playlist's entries in the same
    /// folder, which keeps per-disc numbering when one playlist above disc folders lists them all.
    /// With <paramref name="save"/> false nothing is written: only the resulting numbers are computed.
    /// </summary>
    public static IReadOnlyDictionary<string, int> Reorder(
        M3uSidecarIndex index, IReadOnlyList<string> mediaPathsInOrder, bool save = true)
    {
        var numbers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (playlistPath, files) in Targets(index, mediaPathsInOrder))
        {
            var dir = Path.GetDirectoryName(playlistPath)!;
            var doc = LoadForEdit(playlistPath);
            var lines = doc.Lines;

            var order = files.Select(f => f.Path).ToList();
            var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < order.Count; i++)
                rank.TryAdd(order[i], i);

            // Cut the playlist into blocks [Start, End): each entry with its per-entry lines, and the
            // lines between. Ours (the first listing of each file being ordered) become movable slots.
            var blocks = new List<(int Start, int End, string? Entry)>();
            var start = 0;
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i].Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var blockStart = i;
                while (blockStart > start && IsEntryDirective(lines[blockStart - 1]))
                    blockStart--;
                if (blockStart > start) blocks.Add((start, blockStart, null));
                blocks.Add((blockStart, i + 1, M3u.Resolve(dir, line)));
                start = i + 1;
            }
            if (start < lines.Count) blocks.Add((start, lines.Count, null));

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var slots = Enumerable.Range(0, blocks.Count)
                .Where(i => blocks[i].Entry is { } e && rank.ContainsKey(e) && seen.Add(e))
                .ToList();
            var moved = slots.Select(i => blocks[i]).OrderBy(b => rank[b.Entry!]).ToList();
            var slotOf = slots.Select((blockIndex, k) => (blockIndex, k)).ToDictionary(t => t.blockIndex, t => t.k);

            var result = new List<string>(lines.Count + order.Count);
            for (var i = 0; i < blocks.Count; i++)
            {
                var b = slotOf.TryGetValue(i, out var k) ? moved[k] : blocks[i];
                result.AddRange(lines.GetRange(b.Start, b.End - b.Start));
            }
            foreach (var file in order)
                if (seen.Add(file))
                    result.Add(Path.GetRelativePath(dir, file));

            lines.Clear();
            lines.AddRange(result);
            if (save)
            {
                doc.Save(playlistPath);
                index.Invalidate(playlistPath);
            }

            // Track numbers as the files now stand: position among the entries sharing their folder.
            var perFolder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var counted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                if (M3u.Resolve(dir, line) is not { } entry || !counted.Add(entry)) continue;
                var folder = Path.GetDirectoryName(entry) ?? string.Empty;
                var n = perFolder[folder] = perFolder.GetValueOrDefault(folder) + 1;
                if (rank.ContainsKey(entry))
                    numbers[entry] = n;
            }
        }

        return numbers;
    }

    /// <summary>Lines that belong to the entry below them (<c>#EXTINF</c>, <c>#EXTVLCOPT</c>, …) rather
    /// than to the playlist as a whole.</summary>
    private static bool IsEntryDirective(string line)
    {
        line = line.Trim();
        return line.StartsWith('#')
               && !line.StartsWith(M3u.Header, StringComparison.OrdinalIgnoreCase)
               && !HeaderDirectives.Any(d => line.StartsWith(d, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The playlist each file belongs to (the one listing it, else the first in its folder, else
    /// a new <c>&lt;album&gt;.m3u8</c>, or <c>&lt;folder&gt;.m3u8</c> without an album name), with its files in
    /// the given order and whether each is listed yet.</summary>
    private static Dictionary<string, List<(string Path, bool Listed)>> Targets(
        M3uSidecarIndex index, IEnumerable<string> mediaPaths, string? album = null)
    {
        var targets = new Dictionary<string, List<(string Path, bool Listed)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var mediaPath in mediaPaths)
        {
            var full = Path.GetFullPath(mediaPath);
            var info = index.Find(full);
            var dir = Path.GetDirectoryName(full)!;
            var playlist = info?.AlbumFilePath
                           ?? M3uSidecarIndex.PlaylistsIn(dir).FirstOrDefault()
                           ?? Path.Combine(dir, NewPlaylistName(dir, album));
            if (!targets.TryGetValue(playlist, out var files))
                targets[playlist] = files = new List<(string Path, bool Listed)>();
            files.Add((full, info is not null));
        }
        return targets;
    }

    private static string NewPlaylistName(string dir, string? album)
    {
        var name = string.IsNullOrWhiteSpace(album)
            ? Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar))
            : string.Concat(album.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            name = "album";
        return name + ".m3u8";
    }

    private static void Update(string playlistPath, List<string> missing, CodecAlbumChanges changes)
    {
        var dir = Path.GetDirectoryName(playlistPath)!;
        var doc = LoadForEdit(playlistPath);
        var lines = doc.Lines;

        if (changes.SetAlbum)
        {
            // For MIDI the sidecar *is* the album, so its playlist name is the album name too (what other
            // players show for it); cleared together, since the reader falls back to it for the album.
            SetDirective(lines, M3u.Album, changes.Album);
            SetDirective(lines, M3u.PlaylistTitle, changes.Album);
        }
        if (changes.SetAlbumArtist) SetDirective(lines, M3u.AlbumArtist, changes.AlbumArtist);
        if (changes.SetGenre) SetDirective(lines, M3u.Genre, changes.Genre);
        if (changes.CoverSourcePath is { } cover)
            SetDirective(lines, M3u.Image, InstallCover(cover, dir));

        // Appended in file-name order, so a new album playlist gives the natural track numbering.
        foreach (var file in missing.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            lines.Add(Path.GetRelativePath(dir, file));

        doc.Save(playlistPath);
    }

    private static M3uDocument LoadForEdit(string playlistPath)
    {
        var doc = File.Exists(playlistPath) ? M3uDocument.Load(playlistPath) : M3uDocument.CreateNew();
        var lines = doc.Lines;
        var first = lines.FindIndex(l => l.Trim().Length > 0);
        if (first < 0 || !lines[first].Trim().StartsWith(M3u.Header, StringComparison.OrdinalIgnoreCase))
            lines.Insert(first < 0 ? 0 : first, M3u.Header); // #EXTM3U must lead, or the directives mean nothing
        return doc;
    }

    /// <summary>Copies the image to <c>cover.&lt;ext&gt;</c> in the playlist folder (the common folder-art
    /// name other players read too) and returns that name for <c>#EXTIMG:</c>.</summary>
    private static string InstallCover(string source, string dir)
    {
        var name = "cover" + Path.GetExtension(source).ToLowerInvariant();
        var dest = Path.Combine(dir, name);
        if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
            File.Copy(source, dest, overwrite: true);
        return name;
    }

    /// <summary>Replaces the directive's first occurrence and drops any repeats, inserts it into the header
    /// block when absent, or removes it entirely when the value is blank.</summary>
    private static void SetDirective(List<string> lines, string name, string? value)
    {
        value = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        var at = -1;
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            if (!lines[i].Trim().StartsWith(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (at >= 0) lines.RemoveAt(at);
            at = i;
        }

        if (value is null)
        {
            if (at >= 0) lines.RemoveAt(at);
            return;
        }

        var line = name + value;
        if (at >= 0)
        {
            lines[at] = line;
            return;
        }

        var insert = lines.FindIndex(l => l.Trim().StartsWith(M3u.Header, StringComparison.OrdinalIgnoreCase)) + 1;
        while (insert < lines.Count && HeaderDirectives.Any(d => lines[insert].Trim().StartsWith(d, StringComparison.OrdinalIgnoreCase)))
            insert++;
        lines.Insert(insert, line);
    }
}
