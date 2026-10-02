using PlattaPlayer.Core.Models;

namespace PlattaPlayer.Core.Abstractions;

/// <summary>
/// Low-level audio output. Plays a single <see cref="PlayableMedia"/> at a time; queueing,
/// shuffle and repeat live one layer up in the playback service.
/// </summary>
public interface IPlaybackEngine : IDisposable
{
    PlaybackState State { get; }

    TimeSpan Position { get; }
    TimeSpan Duration { get; }

    /// <summary>
    /// What actually decodes or synthesizes the loaded media (e.g. the emulated module, SoundFont or MIDI
    /// port that MIDI ended up on after any fallback); empty when nothing is loaded.
    /// </summary>
    string Renderer { get; }

    /// <summary>Output volume, 0–100.</summary>
    int Volume { get; set; }

    event EventHandler<PlaybackState>? StateChanged;

    /// <summary>Raised when the current media reaches its natural end.</summary>
    event EventHandler? PlaybackEnded;

    event EventHandler<TimeSpan>? PositionChanged;

    Task LoadAsync(PlayableMedia media, CancellationToken ct = default);

    void Play();
    void Pause();
    void Stop();
    void Seek(TimeSpan position);
}
