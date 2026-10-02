using System.Runtime.CompilerServices;
using PlattaPlayer.Codecs.Abstractions;
using PlattaPlayer.Core.Abstractions;
using PlattaPlayer.Core.Models;

namespace PlattaPlayer.Sources.Local;

/// <summary>
/// A media source backed by one or more local folders. Walks the folders for supported audio files and reads
/// tags / embedded art with TagLib#. Formats a codec plugin handles (MIDI, SNES .spc, …) are read through the
/// plugin instead, with the plugin's cover or else the folder's image.
/// </summary>
public sealed class LocalMediaSource : ILocalFileMediaSource
{
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".m4a", ".aac", ".ogg", ".oga", ".opus", ".wav",
        ".wma", ".aiff", ".aif", ".ape", ".wv", ".alac", ".mp4"
    };

    private readonly IReadOnlyList<string> _folders;
    private readonly CodecRegistry _codecs;

    public LocalMediaSource(string id, string displayName, IReadOnlyList<string> folders, CodecRegistry? codecs = null)
    {
        Id = id;
        DisplayName = displayName;
        _folders = folders;
        _codecs = codecs ?? CodecRegistry.Empty;
    }

    public string Id { get; }
    public SourceType Type => SourceType.Local;
    public string DisplayName { get; }

    public async IAsyncEnumerable<SourceTrack> EnumerateTracksAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var folder in _folders)
        {
            foreach (var path in SafeEnumerateFiles(folder))
            {
                ct.ThrowIfCancellationRequested();

                var track = Read(path);
                if (track is not null)
                    yield return track;

                await Task.Yield();
            }
        }
    }

    public bool Owns(string path)
    {
        var full = Path.GetFullPath(path);
        return _folders.Any(folder =>
            full.StartsWith(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase));
    }

    // SourceItemIds are the paths as enumerated from the configured folders, so build the same form.
    public SourceTrack? ReadFile(string path) =>
        File.Exists(path) ? Read(Path.GetFullPath(path)) : null;

    private SourceTrack? Read(string path)
    {
        var ext = Path.GetExtension(path);
        if (_codecs.ForPath(path) is { } codec)
            return ReadCodec(path, codec);
        return AudioExtensions.Contains(ext) ? ReadAudio(path, ext) : null;
    }

    public Task<PlayableMedia> ResolvePlayableAsync(Track track, CancellationToken ct = default) =>
        Task.FromResult(new PlayableMedia(PlayableKind.LocalFile, track.LocalPath ?? track.SourceItemId));

    public Task<byte[]?> GetCoverArtAsync(SourceTrack track, CancellationToken ct = default)
    {
        if (track.LocalPath is null)
            return Task.FromResult<byte[]?>(null);

        // Codec formats carry no embedded art: the plugin's own cover (a tag, a sidecar), else the folder's image.
        if (_codecs.ForPath(track.LocalPath) is { } codec)
            return Task.FromResult(ReadCodecCover(track.LocalPath, codec));

        try
        {
            using var file = TagLib.File.Create(track.LocalPath);
            var pic = file.Tag.Pictures.FirstOrDefault(p => p.Type == TagLib.PictureType.FrontCover)
                      ?? file.Tag.Pictures.FirstOrDefault();
            return Task.FromResult(pic?.Data?.Data);
        }
        catch
        {
            return Task.FromResult<byte[]?>(null);
        }
    }

    private static byte[]? ReadCodecCover(string path, ICodecPlugin codec)
    {
        try
        {
            var image = codec.FindCover(path) ?? FolderCover.Find(Path.GetDirectoryName(path));
            return image is null ? null : File.ReadAllBytes(image);
        }
        catch
        {
            // Unreadable image (or a failing plugin): treat as no cover rather than failing.
            return null;
        }
    }

    /// <summary>
    /// Reads a file through its codec plugin. The plugin maps its format's tags onto the standard keys; what's
    /// missing falls back like other audio (album artist to artist, album to the folder name).
    /// </summary>
    private SourceTrack ReadCodec(string path, ICodecPlugin codec)
    {
        var format = codec.FormatName.ToLowerInvariant();
        CodecFileInfo? info;
        try
        {
            info = codec.ReadInfo(path);
        }
        catch
        {
            info = null;
        }
        if (info is null)
            return FromFileName(path, format);

        var tags = info.Tags;
        var folderName = Path.GetFileName(Path.GetDirectoryName(path));
        var albumArtist = FirstNonEmpty(tags[CodecTagKeys.AlbumArtist], tags[CodecTagKeys.Artist], "Unknown Artist");
        return new SourceTrack
        {
            SourceId = Id,
            SourceItemId = path,
            LocalPath = path,
            Title = tags[CodecTagKeys.Title] ?? Path.GetFileNameWithoutExtension(path),
            AlbumTitle = tags[CodecTagKeys.Album] ?? (string.IsNullOrWhiteSpace(folderName) ? "Unknown Album" : folderName),
            AlbumArtist = albumArtist,
            TrackArtist = tags[CodecTagKeys.Artist] ?? albumArtist,
            TrackNo = tags.GetNumber(CodecTagKeys.Track),
            DiscNo = tags.GetNumber(CodecTagKeys.Disc),
            Year = tags.GetNumber(CodecTagKeys.Year),
            Genre = tags[CodecTagKeys.Genre],
            Duration = info.Duration,
            Format = format,
            SampleRate = info.SampleRate,
            BitsPerSample = info.BitsPerSample,
            Bitrate = info.Bitrate,
        };
    }

    private SourceTrack? ReadAudio(string path, string ext)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            var tag = file.Tag;

            var title = string.IsNullOrWhiteSpace(tag.Title)
                ? Path.GetFileNameWithoutExtension(path)
                : tag.Title.Trim();

            var albumArtist = FirstNonEmpty(tag.FirstAlbumArtist, tag.FirstPerformer, "Unknown Artist");
            var trackArtist = FirstNonEmpty(tag.FirstPerformer, tag.FirstAlbumArtist, albumArtist);
            var album = string.IsNullOrWhiteSpace(tag.Album) ? "Unknown Album" : tag.Album.Trim();
            var props = file.Properties;

            return new SourceTrack
            {
                SourceId = Id,
                SourceItemId = path,
                LocalPath = path,
                Title = title,
                AlbumTitle = album,
                AlbumArtist = albumArtist,
                TrackArtist = trackArtist,
                TrackNo = tag.Track > 0 ? (int)tag.Track : null,
                DiscNo = tag.Disc > 0 ? (int)tag.Disc : null,
                Year = tag.Year > 0 ? (int)tag.Year : null,
                Duration = props?.Duration ?? TimeSpan.Zero,
                Genre = tag.FirstGenre,
                Format = ext.TrimStart('.').ToLowerInvariant(),
                SampleRate = props?.AudioSampleRate > 0 ? props.AudioSampleRate : null,
                // TagLib reports 0 bits for lossy codecs, which have no fixed bit depth.
                BitsPerSample = props?.BitsPerSample > 0 ? props.BitsPerSample : null,
                Bitrate = props?.AudioBitrate > 0 ? props.AudioBitrate : null,
            };
        }
        catch
        {
            // Unreadable / corrupt file — fall back to a filename-only entry.
            return FromFileName(path, ext.TrimStart('.').ToLowerInvariant());
        }
    }

    private SourceTrack FromFileName(string path, string format)
    {
        var folderName = Path.GetFileName(Path.GetDirectoryName(path)) ?? "Unknown Album";
        return new SourceTrack
        {
            SourceId = Id,
            SourceItemId = path,
            LocalPath = path,
            Title = Path.GetFileNameWithoutExtension(path),
            AlbumTitle = string.IsNullOrWhiteSpace(folderName) ? "Unknown Album" : folderName,
            AlbumArtist = "Unknown Artist",
            TrackArtist = "Unknown Artist",
            Format = format,
        };
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
            if (!string.IsNullOrWhiteSpace(v))
                return v.Trim();
        return string.Empty;
    }

    /// <summary>Recursive file walk that skips folders it cannot read instead of throwing.</summary>
    private static IEnumerable<string> SafeEnumerateFiles(string root)
    {
        if (!Directory.Exists(root))
            yield break;

        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            string[] subDirs;
            try { subDirs = Directory.GetDirectories(dir); }
            catch { subDirs = Array.Empty<string>(); }
            foreach (var sub in subDirs)
                stack.Push(sub);

            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch { files = Array.Empty<string>(); }
            foreach (var file in files)
                yield return file;
        }
    }
}
