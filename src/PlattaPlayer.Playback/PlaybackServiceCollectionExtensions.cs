using Microsoft.Extensions.DependencyInjection;
using PlattaPlayer.Core.Abstractions;

namespace PlattaPlayer.Playback;

public static class PlaybackServiceCollectionExtensions
{
    /// <summary>
    /// Registers the playback orchestration service. The concrete <see cref="IPlaybackEngine"/> is
    /// registered by the application head (it supplies the codec plugins and cache folders).
    /// </summary>
    public static IServiceCollection AddPlayback(this IServiceCollection services)
    {
        services.AddSingleton<IPlaybackService, PlaybackService>();
        return services;
    }
}
