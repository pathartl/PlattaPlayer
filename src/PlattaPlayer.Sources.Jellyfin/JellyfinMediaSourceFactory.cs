using System.Text.Json;
using PlattaPlayer.Core.Abstractions;
using PlattaPlayer.Core.Models;

namespace PlattaPlayer.Sources.Jellyfin;

/// <summary>Builds a <see cref="JellyfinMediaSource"/> from a stored <see cref="SourceConfig"/>.</summary>
public sealed class JellyfinMediaSourceFactory : IMediaSourceFactory
{
    public SourceType Type => SourceType.Jellyfin;

    public Task<IMediaSource> CreateAsync(SourceConfig config, CancellationToken ct = default)
    {
        var settings = string.IsNullOrWhiteSpace(config.SettingsJson)
            ? new JellyfinSourceSettings()
            : JsonSerializer.Deserialize<JellyfinSourceSettings>(config.SettingsJson) ?? new JellyfinSourceSettings();

        IMediaSource source = new JellyfinMediaSource(config.Id, config.DisplayName, settings);
        return Task.FromResult(source);
    }
}
