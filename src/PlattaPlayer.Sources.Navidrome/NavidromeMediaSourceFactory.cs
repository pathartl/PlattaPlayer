using System.Text.Json;
using PlattaPlayer.Core.Abstractions;
using PlattaPlayer.Core.Models;

namespace PlattaPlayer.Sources.Navidrome;

/// <summary>Builds a <see cref="NavidromeMediaSource"/> from a stored <see cref="SourceConfig"/>.</summary>
public sealed class NavidromeMediaSourceFactory : IMediaSourceFactory
{
    public SourceType Type => SourceType.Navidrome;

    public Task<IMediaSource> CreateAsync(SourceConfig config, CancellationToken ct = default)
    {
        var settings = string.IsNullOrWhiteSpace(config.SettingsJson)
            ? new NavidromeSourceSettings()
            : JsonSerializer.Deserialize<NavidromeSourceSettings>(config.SettingsJson) ?? new NavidromeSourceSettings();

        IMediaSource source = new NavidromeMediaSource(config.Id, config.DisplayName, settings);
        return Task.FromResult(source);
    }
}
