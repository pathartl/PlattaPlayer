using System.Text.Json;
using PlattaPlayer.Core.Abstractions;
using PlattaPlayer.Core.Models;

namespace PlattaPlayer.Sources.Emby;

/// <summary>Builds an <see cref="EmbyMediaSource"/> from a stored <see cref="SourceConfig"/>.</summary>
public sealed class EmbyMediaSourceFactory : IMediaSourceFactory
{
    public SourceType Type => SourceType.Emby;

    public Task<IMediaSource> CreateAsync(SourceConfig config, CancellationToken ct = default)
    {
        var settings = string.IsNullOrWhiteSpace(config.SettingsJson)
            ? new EmbySourceSettings()
            : JsonSerializer.Deserialize<EmbySourceSettings>(config.SettingsJson) ?? new EmbySourceSettings();

        IMediaSource source = new EmbyMediaSource(config.Id, config.DisplayName, settings);
        return Task.FromResult(source);
    }
}
