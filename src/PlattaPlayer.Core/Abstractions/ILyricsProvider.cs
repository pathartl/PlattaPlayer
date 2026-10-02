using PlattaPlayer.Core.Models;

namespace PlattaPlayer.Core.Abstractions;

/// <summary>Finds lyrics for a track. Returns null when the track has none.</summary>
public interface ILyricsProvider
{
    Task<Lyrics?> GetLyricsAsync(Track track, CancellationToken ct = default);
}
