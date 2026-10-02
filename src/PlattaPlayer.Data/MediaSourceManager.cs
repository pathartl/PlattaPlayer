using System.Collections.Concurrent;
using PlattaPlayer.Core.Abstractions;
using PlattaPlayer.Core.Models;

namespace PlattaPlayer.Data;

/// <summary>
/// Default <see cref="IMediaSourceManager"/>. Loads <see cref="SourceConfig"/>s from the repository
/// and instantiates them via the matching <see cref="IMediaSourceFactory"/>, caching live instances.
/// </summary>
public sealed class MediaSourceManager : IMediaSourceManager
{
    private readonly ILibraryRepository _repository;
    private readonly IReadOnlyDictionary<SourceType, IMediaSourceFactory> _factories;
    private readonly ConcurrentDictionary<string, IMediaSource> _cache = new();

    public MediaSourceManager(ILibraryRepository repository, IEnumerable<IMediaSourceFactory> factories)
    {
        _repository = repository;
        _factories = factories.ToDictionary(f => f.Type);
    }

    public async Task<IMediaSource> GetAsync(string sourceId, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(sourceId, out var existing))
            return existing;

        var config = (await _repository.GetSourcesAsync(ct)).FirstOrDefault(s => s.Id == sourceId)
            ?? throw new InvalidOperationException($"Unknown source '{sourceId}'.");

        return await CreateAndCacheAsync(config, ct);
    }

    public async Task<IReadOnlyList<IMediaSource>> GetAllAsync(CancellationToken ct = default)
    {
        var configs = await _repository.GetSourcesAsync(ct);
        var result = new List<IMediaSource>(configs.Count);
        foreach (var config in configs)
        {
            result.Add(_cache.TryGetValue(config.Id, out var existing)
                ? existing
                : await CreateAndCacheAsync(config, ct));
        }
        return result;
    }

    public void Invalidate(string sourceId) => _cache.TryRemove(sourceId, out _);

    private async Task<IMediaSource> CreateAndCacheAsync(SourceConfig config, CancellationToken ct)
    {
        if (!_factories.TryGetValue(config.Type, out var factory))
            throw new InvalidOperationException($"No factory registered for source type '{config.Type}'.");

        var source = await factory.CreateAsync(config, ct);
        _cache[config.Id] = source;
        return source;
    }
}
