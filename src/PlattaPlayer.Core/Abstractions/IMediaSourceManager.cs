using PlattaPlayer.Core.Models;

namespace PlattaPlayer.Core.Abstractions;

/// <summary>
/// Builds and caches live <see cref="IMediaSource"/> instances from stored <see cref="SourceConfig"/>s
/// using the registered <see cref="IMediaSourceFactory"/>s. Shared by sync and playback.
/// </summary>
public interface IMediaSourceManager
{
    Task<IMediaSource> GetAsync(string sourceId, CancellationToken ct = default);

    Task<IReadOnlyList<IMediaSource>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Drop the cached instance for a source (e.g. after its configuration changes).</summary>
    void Invalidate(string sourceId);
}
