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

    /// <summary>
    /// Snapshot of the queue in play order (shuffle already applied). Entries before
    /// <see cref="CurrentIndex"/> have been played; those after it are up next.
    /// </summary>
    IReadOnlyList<QueueEntry> Queue { get; }

    /// <summary>Index of the current entry in <see cref="Queue"/>, or -1 when the queue is empty.</summary>
    int CurrentIndex { get; }

    /// <summary>Raised (possibly off the UI thread) whenever <see cref="Queue"/> or <see cref="CurrentIndex"/> changes.</summary>
    event EventHandler? QueueChanged;

    /// <summary>Play an ordered set of tracks starting at <paramref name="startIndex"/>, replacing the queue.</summary>
    Task PlayQueueAsync(IReadOnlyList<Track> tracks, int startIndex = 0, CancellationToken ct = default);

    /// <summary>Jumps to and plays the entry at <paramref name="index"/> in <see cref="Queue"/>.</summary>
    Task PlayQueueEntryAsync(int index, CancellationToken ct = default);

    /// <summary>Inserts tracks right after the current entry.</summary>
    void PlayNext(IReadOnlyList<Track> tracks);

    /// <summary>Appends tracks to the end of the queue.</summary>
    void AddToQueue(IReadOnlyList<Track> tracks);

    /// <summary>Moves the entry at <paramref name="from"/> so it ends up at <paramref name="to"/>.</summary>
    void MoveQueueEntry(int from, int to);

    /// <summary>Removes an entry. Removing the current entry skips to the next one (or stops at the end).</summary>
    Task RemoveQueueEntryAsync(int index, CancellationToken ct = default);

    /// <summary>Removes every entry after the current one.</summary>
    void ClearUpcoming();

    Task PlayTrackAsync(Track track, CancellationToken ct = default);

    Task TogglePlayPauseAsync(CancellationToken ct = default);
    Task NextAsync(CancellationToken ct = default);
    Task PreviousAsync(CancellationToken ct = default);

    void Seek(TimeSpan position);
}
