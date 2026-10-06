using System.Text.Json;
using PlattaPlayer.Core.Abstractions;
using PlattaPlayer.Core.Models;

namespace PlattaPlayer.Sources.Plex;

/// <summary>Builds a <see cref="PlexMediaSource"/> from a stored <see cref="SourceConfig"/>.</summary>
public sealed class PlexMediaSourceFactory : IMediaSourceFactory
{
    public SourceType Type => SourceType.Plex;

    public Task<IMediaSource> CreateAsync(SourceConfig config, CancellationToken ct = default)
    {
        var settings = string.IsNullOrWhiteSpace(config.SettingsJson)
            ? new PlexSourceSettings()
            : JsonSerializer.Deserialize<PlexSourceSettings>(config.SettingsJson) ?? new PlexSourceSettings();

        IMediaSource source = new PlexMediaSource(config.Id, config.DisplayName, settings);
        return Task.FromResult(source);
    }
}
