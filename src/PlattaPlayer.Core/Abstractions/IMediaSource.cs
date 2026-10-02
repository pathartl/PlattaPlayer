using PlattaPlayer.Core.Models;

namespace PlattaPlayer.Core.Abstractions;

/// <summary>
/// A pluggable provider of music (local files, Jellyfin, …). Implementations enumerate tracks
/// for the sync service and resolve playable handles / cover art on demand.
/// </summary>
public interface IMediaSource
{
    /// <summary>Matches the owning <see cref="SourceConfig.Id"/>.</summary>
    string Id { get; }

    SourceType Type { get; }

    string DisplayName { get; }

    /// <summary>Enumerate every track currently exposed by the source.</summary>
    IAsyncEnumerable<SourceTrack> EnumerateTracksAsync(CancellationToken ct = default);

    /// <summary>Resolve a stored track into a playable file path or stream URL.</summary>
    Task<PlayableMedia> ResolvePlayableAsync(Track track, CancellationToken ct = default);

    /// <summary>Return raw cover-art image bytes for a discovered track, or null if none.</summary>
    Task<byte[]?> GetCoverArtAsync(SourceTrack track, CancellationToken ct = default);
}

/// <summary>A source backed by local files that can re-read single files, for a targeted rescan.</summary>
public interface ILocalFileMediaSource : IMediaSource
{
    /// <summary>Whether <paramref name="path"/> lies in one of the source's folders.</summary>
    bool Owns(string path);

    /// <summary>Reads one file as <see cref="EnumerateTracksAsync"/> would, or null when it isn't a
    /// supported media file (or no longer exists).</summary>
    SourceTrack? ReadFile(string path);
}

/// <summary>Factory that builds a live <see cref="IMediaSource"/> from a stored configuration.</summary>
public interface IMediaSourceFactory
{
    SourceType Type { get; }

    Task<IMediaSource> CreateAsync(SourceConfig config, CancellationToken ct = default);
}
