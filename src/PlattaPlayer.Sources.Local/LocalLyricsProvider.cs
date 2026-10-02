using System.Text;
using PlattaPlayer.Core.Abstractions;
using PlattaPlayer.Core.Parsing;
using PlattaPlayer.Core.Models;

namespace PlattaPlayer.Sources.Local;

/// <summary>
/// Lyrics for on-disk tracks, in priority order: a sidecar <c>.lrc</c> next to the file, embedded synced
/// lyrics (ID3 <c>SYLT</c>, or a lyrics tag such as Vorbis <c>LYRICS</c> holding LRC timestamps), then
/// embedded unsynced lyrics. Remote tracks (no local path) have none.
/// </summary>
public sealed class LocalLyricsProvider : ILyricsProvider
{
    public Task<Lyrics?> GetLyricsAsync(Track track, CancellationToken ct = default)
        => track.LocalPath is { } path
            ? Task.Run(() => Read(path), ct)
            : Task.FromResult<Lyrics?>(null);

    private static Lyrics? Read(string path)
    {
        var sidecar = Path.ChangeExtension(path, ".lrc");
        if (File.Exists(sidecar))
        {
            try
            {
                var lines = LrcParser.Parse(File.ReadAllText(sidecar, Encoding.UTF8));
                if (lines.Count > 0)
                    return new Lyrics { Lines = lines, IsSynced = true, SourceLabel = "LRC" };
            }
            catch (IOException)
            {
                // Unreadable sidecar: fall through to embedded lyrics.
            }
        }

        try
        {
            using var file = TagLib.File.Create(path);

            if (file.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id3)
            {
                var sylt = id3.GetFrames<TagLib.Id3v2.SynchronisedLyricsFrame>()
                    .Where(f => f.Type == TagLib.Id3v2.SynchedTextType.Lyrics && f.Text.Length > 0)
                    .FirstOrDefault();
                if (sylt is not null)
                {
                    var lines = sylt.Text
                        .Select(t => new LyricLine(ToTime(sylt.Format, t.Time), t.Text.Trim()))
                        .OrderBy(l => l.Time)
                        .ToList();
                    return new Lyrics { Lines = lines, IsSynced = true, SourceLabel = "SYLT" };
                }
            }

            var text = file.Tag.Lyrics;
            if (string.IsNullOrWhiteSpace(text))
                return null;

            if (LrcParser.LooksLikeLrc(text))
            {
                var lines = LrcParser.Parse(text);
                if (lines.Count > 0)
                    return new Lyrics { Lines = lines, IsSynced = true, SourceLabel = "LRC" };
            }

            var plain = text.Replace("\r", "").Split('\n').Select(l => new LyricLine(null, l.Trim())).ToList();
            return new Lyrics { Lines = plain, IsSynced = false, SourceLabel = "TAG" };
        }
        catch
        {
            // Corrupt or unsupported file: treat as no lyrics.
            return null;
        }
    }

    // SYLT timestamps are milliseconds unless the frame says MPEG frames, which we can't convert without
    // the stream's frame rate; treat those as unsynced positions of zero rather than guessing.
    private static TimeSpan? ToTime(TagLib.Id3v2.TimestampFormat format, long value)
        => format == TagLib.Id3v2.TimestampFormat.AbsoluteMilliseconds ? TimeSpan.FromMilliseconds(value) : TimeSpan.Zero;
}
