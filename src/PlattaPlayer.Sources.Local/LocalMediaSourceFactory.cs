using System.Text.Json;
using PlattaPlayer.Codecs.Abstractions;
using PlattaPlayer.Core.Abstractions;
using PlattaPlayer.Core.Models;

namespace PlattaPlayer.Sources.Local;

/// <summary>Builds a <see cref="LocalMediaSource"/> from a stored <see cref="SourceConfig"/>.</summary>
public sealed class LocalMediaSourceFactory : IMediaSourceFactory
{
    private readonly CodecRegistry? _codecs;

    public LocalMediaSourceFactory(CodecRegistry? codecs = null) => _codecs = codecs;

    public SourceType Type => SourceType.Local;

    public Task<IMediaSource> CreateAsync(SourceConfig config, CancellationToken ct = default)
    {
        var settings = string.IsNullOrWhiteSpace(config.SettingsJson)
            ? new LocalSourceSettings()
            : JsonSerializer.Deserialize<LocalSourceSettings>(config.SettingsJson) ?? new LocalSourceSettings();

        IMediaSource source = new LocalMediaSource(config.Id, config.DisplayName, settings.Folders, _codecs);
        return Task.FromResult(source);
    }
}
