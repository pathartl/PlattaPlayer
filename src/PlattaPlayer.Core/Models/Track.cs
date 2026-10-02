namespace PlattaPlayer.Core.Models;

/// <summary>A single playable track. The unit of playback and the densest table in the library.</summary>
public class Track
{
    public int Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public int AlbumId { get; set; }
    public Album? Album { get; set; }

    /// <summary>Track-level artist (may differ from the album artist, e.g. compilations/features).</summary>
    public string TrackArtist { get; set; } = string.Empty;

    public int? TrackNo { get; set; }
    public int? DiscNo { get; set; }

    public TimeSpan Duration { get; set; }

    /// <summary>File extension / container without the dot (e.g. "mp3", "flac"), or the format name of the codec
    /// plugin that plays it ("spc", "midi").</summary>
    public string Format { get; set; } = string.Empty;

    /// <summary>Source sample rate in Hz (e.g. 44100), when the source reports it.</summary>
    public int? SampleRate { get; set; }

    /// <summary>Source bit depth (e.g. 16, 24) for PCM/lossless formats; null for lossy codecs.</summary>
    public int? BitsPerSample { get; set; }

    /// <summary>Average bitrate in kbps, when the source reports it. Used to label lossy files ("MP3 320").</summary>
    public int? Bitrate { get; set; }

    public string? Genre { get; set; }

    /// <summary>The configured source this track belongs to (<see cref="SourceConfig.Id"/>).</summary>
    public string SourceId { get; set; } = string.Empty;

    /// <summary>Stable identifier of the item within its source (absolute path for local, item id for Jellyfin).</summary>
    public string SourceItemId { get; set; } = string.Empty;

    /// <summary>Local file path when the source is on-disk; null for remote sources.</summary>
    public string? LocalPath { get; set; }

    /// <summary>Hash key into the cover-art cache.</summary>
    public string? CoverArtKey { get; set; }

    public DateTimeOffset DateAdded { get; set; }

    // Denormalised play statistics for fast Home/most-played queries.
    public int PlayCount { get; set; }
    public DateTimeOffset? LastPlayedAt { get; set; }
}
