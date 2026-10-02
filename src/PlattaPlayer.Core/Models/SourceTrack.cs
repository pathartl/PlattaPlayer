namespace PlattaPlayer.Core.Models;

/// <summary>
/// A track as discovered from a media source during enumeration, before it is mapped into
/// the local database. Carries the raw metadata the sync service needs to build/refresh
/// <see cref="Artist"/>/<see cref="Album"/>/<see cref="Track"/> rows.
/// </summary>
public sealed class SourceTrack
{
    public required string SourceId { get; init; }
    public required string SourceItemId { get; init; }

    public string Title { get; init; } = string.Empty;
    public string AlbumTitle { get; init; } = string.Empty;
    public string AlbumArtist { get; init; } = string.Empty;
    public string TrackArtist { get; init; } = string.Empty;

    public int? TrackNo { get; init; }
    public int? DiscNo { get; init; }
    public int? Year { get; init; }
    public TimeSpan Duration { get; init; }
    public string? Genre { get; init; }

    public string Format { get; init; } = string.Empty;

    public int? SampleRate { get; init; }
    public int? BitsPerSample { get; init; }
    public int? Bitrate { get; init; }

    /// <summary>Local path for on-disk sources; null for remote sources.</summary>
    public string? LocalPath { get; init; }
}
