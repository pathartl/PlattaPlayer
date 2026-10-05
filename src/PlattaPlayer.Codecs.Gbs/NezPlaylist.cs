using System.Globalization;
using System.Text;
using PlattaPlayer.Codecs.AlbumFiles;

namespace PlattaPlayer.Codecs.Gbs;

/// <summary>
/// One song line of a NEZplug-style extended M3U, the playlist format GBS rips ship with:
/// <code>file.gbs::GBS,track,title,time,loop,fade,loopcount</code>
/// The track is 1-based when decimal and 0-based when written as <c>$hex</c> (as Game Music Emu, the reference
/// reader, interprets GBS playlists). Times are <c>[h:]m:ss</c> or seconds, optionally with a decimal fraction
/// (<c>.5</c>) or frames (<c>'30</c>, sixtieths). The loop is a time, <c>-</c> (the whole song loops) or a time
/// ending in <c>-</c> (the intro). Commas and backslashes in the title are escaped with a backslash. Fields
/// are kept as written, so rewriting one leaves the others untouched. A <c>#EXTDISC:</c> line above an entry
/// gives the song's disc.
/// </summary>
internal sealed class NezEntry
{
    public required string PlaylistPath { get; init; }
    public required int LineIndex { get; init; }
    public required string File { get; init; }
    public required string Type { get; init; }

    /// <summary>The GBS song index (0-based).</summary>
    public required int Song { get; init; }

    public required string TrackField { get; init; }
    public required string TitleField { get; init; }
    public required string TimeField { get; init; }
    public required string LoopField { get; init; }
    public required string FadeField { get; init; }

    /// <summary>Everything after the fade field (the loop count and anything else), with its leading comma.</summary>
    public required string Tail { get; init; }

    /// <summary>The disc, from a <c>#EXTDISC:</c> line above the entry.</summary>
    public int? Disc { get; init; }

    public string? Title => Unescape(TitleField) is { Length: > 0 } title ? title : null;

    /// <summary>The play time before the fade: the time field, else the intro plus two loops, else null.</summary>
    public double? PlaySeconds
    {
        get
        {
            if (NezTime.Parse(TimeField) is > 0 and var time) return time;
            // "intro-" or "-" need the time field to make a length.
            var loop = LoopField.Trim();
            if (loop.Length == 0 || loop.EndsWith('-')) return null;
            return NezTime.Parse(loop) is > 0 and var loopTime ? 2 * loopTime : null;
        }
    }

    public double? FadeSeconds => NezTime.Parse(FadeField) is >= 0 and var fade ? fade : null;

    /// <summary>The line with the given fields replaced (null keeps a field as it is).</summary>
    public string WithFields(string? title = null, string? time = null, string? fade = null)
    {
        var fields = new List<string>
        {
            TrackField,
            title is null ? TitleField : Escape(title),
            time ?? TimeField,
            LoopField,
            fade ?? FadeField,
        };
        var line = $"{File}::{Type},{string.Join(",", fields)}{Tail}";
        // Drop trailing empty fields (but always keep the track and title).
        while (line.EndsWith(',') && line.Count(c => c == ',') > 2 && !line.EndsWith("\\,", StringComparison.Ordinal))
            line = line[..^1];
        return line;
    }

    public static string NewLine(string file, int song, string? title, string? time, string? fade)
    {
        var line = $"{file}::GBS,{(song + 1).ToString(CultureInfo.InvariantCulture)},{Escape(title ?? "")},{time},,{fade}";
        while (line.EndsWith(',') && line.Count(c => c == ',') > 2 && !line.EndsWith("\\,", StringComparison.Ordinal))
            line = line[..^1];
        return line;
    }

    /// <summary>Parses a song line; null for comments, blank lines and lines that aren't NEZplug entries.</summary>
    public static NezEntry? Parse(string playlistPath, int lineIndex, string rawLine, int? disc = null)
    {
        var line = rawLine.Trim();
        if (line.Length == 0 || line[0] == '#') return null;
        var separator = line.IndexOf("::", StringComparison.Ordinal);
        if (separator <= 0) return null;
        var file = line[..separator].Trim();
        var rest = line[(separator + 2)..];

        var comma = rest.IndexOf(',');
        var type = (comma < 0 ? rest : rest[..comma]).Trim();
        rest = comma < 0 ? "" : rest[(comma + 1)..];

        // Track: up to the next comma.
        comma = rest.IndexOf(',');
        var track = comma < 0 ? rest : rest[..comma];
        rest = comma < 0 ? "" : rest[(comma + 1)..];

        // Title: a comma ends it only when what follows looks like the next field (a comma, '-' or a digit).
        var titleEnd = rest.Length;
        var titleNext = rest.Length;
        for (var i = 0; i < rest.Length; i++)
        {
            if (rest[i] == '\\')
            {
                i++;
                continue;
            }
            if (rest[i] != ',') continue;
            var j = i + 1;
            while (j < rest.Length && char.IsWhiteSpace(rest[j])) j++;
            if (j < rest.Length && (rest[j] == ',' || rest[j] == '-' || char.IsAsciiDigit(rest[j])))
            {
                titleEnd = i;
                titleNext = i + 1;
                break;
            }
        }
        var title = rest[..titleEnd];
        rest = titleNext < rest.Length ? rest[titleNext..] : "";

        var fields = rest.Length == 0 ? [] : rest.Split(',');
        string Field(int i) => i < fields.Length ? fields[i] : "";
        var tail = fields.Length > 3 ? "," + string.Join(",", fields[3..]) : "";

        return new NezEntry
        {
            PlaylistPath = playlistPath,
            LineIndex = lineIndex,
            File = file,
            Type = type,
            Song = ParseTrack(track),
            TrackField = track,
            TitleField = title,
            TimeField = Field(0),
            LoopField = Field(1),
            FadeField = Field(2),
            Tail = tail,
            Disc = disc,
        };
    }

    // Decimal tracks count from 1, $hex ones from 0; a missing track means the first song.
    private static int ParseTrack(string field)
    {
        var text = field.Trim();
        if (text.StartsWith('$'))
            return int.TryParse(text[1..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hex) ? hex : 0;
        var digits = new string(text.TakeWhile(char.IsAsciiDigit).ToArray());
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? Math.Max(0, n - 1) : 0;
    }

    private static string Unescape(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length) i++;
            sb.Append(text[i]);
        }
        return sb.ToString().Trim();
    }

    private static string Escape(string text) => text.Trim().Replace("\\", "\\\\").Replace(",", "\\,");
}

/// <summary>NEZplug time fields.</summary>
internal static class NezTime
{
    /// <summary>Parses "[h:]m:ss", "ss", with an optional ".fraction" or "'frames" (1/60 s); null when blank or
    /// not a time.</summary>
    public static double? Parse(string? field)
    {
        var text = field?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        double fraction = 0;
        var tick = text.IndexOf('\'');
        if (tick >= 0)
        {
            if (!int.TryParse(text[(tick + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var frames)) return null;
            fraction = frames / 60.0;
            text = text[..tick];
        }
        var dot = text.IndexOf('.');
        if (dot >= 0)
        {
            var digits = text[(dot + 1)..];
            if (digits.Length > 0 && !digits.All(char.IsAsciiDigit)) return null;
            fraction = digits.Length == 0 ? 0 : double.Parse("0." + digits, CultureInfo.InvariantCulture);
            text = text[..dot];
        }
        var total = 0.0;
        foreach (var part in text.Split(':'))
        {
            if (part.Length == 0 || !part.All(char.IsAsciiDigit)) return null;
            total = total * 60 + int.Parse(part, CultureInfo.InvariantCulture);
        }
        return total + fraction;
    }

    /// <summary>"2:30", "1:02:03", "0:05.250".</summary>
    public static string Format(double seconds)
    {
        var ms = (long)Math.Round(seconds * 1000);
        var whole = ms / 1000;
        var rest = ms % 1000;
        var text = whole >= 3600
            ? $"{whole / 3600}:{whole / 60 % 60:00}:{whole % 60:00}"
            : $"{whole / 60}:{whole % 60:00}";
        return rest == 0 ? text : $"{text}.{rest:000}".TrimEnd('0');
    }
}

/// <summary>
/// The lines of an M3U plus what's needed to write it back the way it was found (encoding, BOM, line endings).
/// Plain .m3u is UTF-8 when it decodes as such, otherwise Latin-1; .m3u8 is UTF-8.
/// </summary>
internal sealed class NezDocument
{
    private readonly bool _latin1;
    private readonly bool _bom;
    private readonly string _newLine;

    private NezDocument(List<string> lines, bool latin1, bool bom, string newLine)
    {
        Lines = lines;
        _latin1 = latin1;
        _bom = bom;
        _newLine = newLine;
    }

    public List<string> Lines { get; }

    public static NezDocument CreateNew() => new(new List<string>(), false, false, "\r\n");

    public static NezDocument Load(string path)
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
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return new NezDocument(lines, latin1, bom, newLine);
    }

    /// <summary>Writes the lines back in the original encoding unless new text needs UTF-8; via a temporary file
    /// swapped in, so a failure never truncates the playlist.</summary>
    public void Save(string path)
    {
        var text = string.Join(_newLine, Lines) + _newLine;
        var latin1 = _latin1 && Encoding.Latin1.GetString(Encoding.Latin1.GetBytes(text)) == text;
        Encoding encoding = latin1 ? Encoding.Latin1 : new UTF8Encoding(_bom);
        var temp = path + ".tmp";
        File.WriteAllText(temp, text, encoding);
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>What the playlists listing a GBS say about its album: the first (by name) to give each field
/// wins.</summary>
/// <param name="PlaylistPath">The first playlist listing the file: where album fields are written.</param>
/// <param name="CoverPath">The <c>#EXTIMG:</c> image, resolved to a full path.</param>
internal sealed record NezAlbum(string PlaylistPath, string? AlbumArtist, string? Genre, string? CoverPath);

/// <summary>
/// Finds the NEZplug entries for a GBS among the playlists (.m3u, .m3u8) in its folder: one playlist may list
/// every song, or there may be one per song. Playlists are read in name order and their entries kept in line
/// order. Parsed playlists are cached until they change on disk.
/// <para>A playlist may also carry the album fields GBS lacks, as extended-M3U directives (which NEZplug and
/// Game Music Emu skip as comments): <c>#EXTART:</c> (album artist), <c>#EXTGENRE:</c> and <c>#EXTIMG:</c>
/// (cover).</para>
/// </summary>
internal sealed class NezPlaylistIndex
{
    private sealed record Playlist(NezEntry[] Entries, string? AlbumArtist, string? Genre, string? CoverPath);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, (DateTime WriteTime, long Length, Playlist Playlist)> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The GBS entries pointing at <paramref name="gbsPath"/>, in album order.</summary>
    public List<NezEntry> Find(string gbsPath)
    {
        var result = new List<NezEntry>();
        try
        {
            var target = Path.GetFullPath(gbsPath);
            var dir = Path.GetDirectoryName(target);
            if (dir is null) return result;
            foreach (var playlist in PlaylistsIn(dir))
                result.AddRange(Load(playlist).Entries.Where(e => Lists(dir, e, target)));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        return result;
    }

    /// <summary>The album fields of the playlists listing <paramref name="gbsPath"/>, or null when none does.</summary>
    public NezAlbum? FindAlbum(string gbsPath)
    {
        try
        {
            var target = Path.GetFullPath(gbsPath);
            var dir = Path.GetDirectoryName(target);
            if (dir is null) return null;
            NezAlbum? album = null;
            foreach (var path in PlaylistsIn(dir))
            {
                var playlist = Load(path);
                if (!playlist.Entries.Any(e => Lists(dir, e, target))) continue;
                album = album is null
                    ? new NezAlbum(Path.GetFullPath(path), playlist.AlbumArtist, playlist.Genre, playlist.CoverPath)
                    : album with
                    {
                        AlbumArtist = album.AlbumArtist ?? playlist.AlbumArtist,
                        Genre = album.Genre ?? playlist.Genre,
                        CoverPath = album.CoverPath ?? playlist.CoverPath,
                    };
            }
            return album;
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

    /// <summary>Whether the entry is a GBS song of <paramref name="target"/> (a full path).</summary>
    public static bool Lists(string dir, NezEntry entry, string target) =>
        entry.Type.Equals("GBS", StringComparison.OrdinalIgnoreCase)
        && Resolve(dir, entry.File) is { } file && file.Equals(target, StringComparison.OrdinalIgnoreCase);

    public void Invalidate(string playlistPath)
    {
        lock (_gate) _cache.Remove(Path.GetFullPath(playlistPath));
    }

    public static IEnumerable<string> PlaylistsIn(string dir)
    {
        var files = Directory.GetFiles(dir);
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        return files.Where(f => Path.GetExtension(f).ToLowerInvariant() is ".m3u" or ".m3u8");
    }

    public static string? Resolve(string dir, string file)
    {
        try
        {
            return Path.GetFullPath(Path.Combine(dir, file));
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private Playlist Load(string path)
    {
        var info = new FileInfo(path);
        var key = info.FullName;
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var cached) && cached.WriteTime == info.LastWriteTimeUtc && cached.Length == info.Length)
                return cached.Playlist;
        }

        Playlist playlist;
        try
        {
            playlist = Parse(key, NezDocument.Load(path).Lines);
        }
        catch (IOException)
        {
            playlist = new Playlist([], null, null, null);
        }

        lock (_gate) _cache[key] = (info.LastWriteTimeUtc, info.Length, playlist);
        return playlist;
    }

    private static Playlist Parse(string path, List<string> lines)
    {
        var dir = Path.GetDirectoryName(path)!;
        var root = dir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var entries = new List<NezEntry>();
        string? albumArtist = null, genre = null, cover = null;
        int? disc = null;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith('#'))
            {
                if (M3u.Directive(line, M3u.AlbumArtist) is { } artist) albumArtist ??= artist;
                else if (M3u.Directive(line, M3u.Genre) is { } g) genre ??= g;
                else if (M3u.Directive(line, M3u.Image) is { } image) cover ??= M3u.ResolveContained(dir, root, image);
                else if (M3u.Directive(line, M3u.Disc) is { } d) disc = M3u.Number(d);
                continue;
            }
            if (NezEntry.Parse(path, i, line, disc) is { } entry) entries.Add(entry);
            if (line.Length > 0) disc = null;
        }
        return new Playlist(entries.ToArray(), albumArtist, genre, cover);
    }
}
