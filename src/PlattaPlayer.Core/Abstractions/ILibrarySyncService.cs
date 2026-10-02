using PlattaPlayer.Core.Models;

namespace PlattaPlayer.Core.Abstractions;

/// <summary>Progress report emitted while a source is being synced into the local library.</summary>
public sealed record SyncProgress(string SourceName, int Processed, int? Total);

/// <summary>
/// Pulls tracks from configured <see cref="IMediaSource"/>s into the local database and
/// populates the cover-art cache. The UI always reads from the database, never a live scan.
/// </summary>
public interface ILibrarySyncService
{
    Task SyncSourceAsync(SourceConfig source, IProgress<SyncProgress>? progress = null, CancellationToken ct = default);

    Task SyncAllAsync(IProgress<SyncProgress>? progress = null, CancellationToken ct = default);

    /// <summary>Re-reads just the given local files in whichever sources contain them (e.g. after their
    /// tags were edited), refreshing their album cover too. Files outside every source are ignored.
    /// Returns the number of tracks updated.</summary>
    Task<int> SyncFilesAsync(IReadOnlyCollection<string> paths, CancellationToken ct = default);
}

/// <summary>Stores and retrieves cover-art images keyed by a content hash.</summary>
public interface ICoverArtCache
{
    /// <summary>Persist image bytes and return the cache key (content hash), or null for empty input.</summary>
    Task<string?> SaveAsync(byte[]? imageBytes, CancellationToken ct = default);

    /// <summary>Absolute path to the full-size cached image for a key, or null if absent.</summary>
    string? GetPath(string? coverArtKey);
}
