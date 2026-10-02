namespace PlattaPlayer.Core.Abstractions;

/// <summary>
/// Whole-track loudness overview of the loaded media, for drawing behind the seek bar. Computed in the
/// background after a track loads, so it starts null and <see cref="WaveformChanged"/> fires once it is
/// ready (or cleared). An <see cref="IPlaybackEngine"/> may implement this when its backend can decode
/// ahead of playback (BASS can, with a separate decode-only stream).
/// </summary>
public interface IWaveformSource
{
    /// <summary>
    /// Smoothed loudness envelope across the whole track, evenly spaced in time, normalised so the loudest
    /// point is 1; null while computing or when unavailable (remote streams, external MIDI).
    /// </summary>
    float[]? Waveform { get; }

    /// <summary>Raised on a background thread whenever <see cref="Waveform"/> changes.</summary>
    event EventHandler? WaveformChanged;
}
