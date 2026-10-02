using System.ComponentModel;
using PlattaPlayer.Core.Models;

namespace PlattaPlayer.Core.Abstractions;

/// <summary>
/// High-level playback orchestration over an <see cref="IPlaybackEngine"/>: owns the queue,
/// shuffle/repeat and current-track state, resolves playable media from sources, and records
/// play statistics. The UI and OS media controls observe this service.
/// </summary>
public interface IPlaybackService : INotifyPropertyChanged
{
    Track? CurrentTrack { get; }
    PlaybackState State { get; }
    TimeSpan Position { get; }
    TimeSpan Duration { get; }

    /// <summary>The engine's <see cref="IPlaybackEngine.Renderer"/> for the current track.</summary>
    string Renderer { get; }

    /// <summary>0–100.</summary>
    int Volume { get; set; }

    bool ShuffleEnabled { get; set; }
    RepeatMode RepeatMode { get; set; }

    bool HasNext { get; }
    bool HasPrevious { get; }

    /// <summary>Play an ordered set of tracks starting at <paramref name="startIndex"/>.</summary>
    Task PlayQueueAsync(IReadOnlyList<Track> tracks, int startIndex = 0, CancellationToken ct = default);

    Task PlayTrackAsync(Track track, CancellationToken ct = default);

    Task TogglePlayPauseAsync(CancellationToken ct = default);
    Task NextAsync(CancellationToken ct = default);
    Task PreviousAsync(CancellationToken ct = default);

    void Seek(TimeSpan position);
}
