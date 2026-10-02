namespace PlattaPlayer.Core.Abstractions;

/// <summary>
/// Passive, pull-based access to the live audio signal for visualizations. Consumers (e.g. the
/// Now Playing spectrum) poll at their own cadence; reads are non-blocking and never disturb
/// playback. An <see cref="IPlaybackEngine"/> may also implement this when its backend exposes a
/// cheap tap (BASS does, via <c>ChannelGetData</c>).
/// </summary>
public interface IAudioTap
{
    /// <summary>True while audio is actively playing and spectrum data is available.</summary>
    bool IsActive { get; }

    /// <summary>
    /// Sample rate of the stream being analysed, in Hz, or 0 when nothing is playing.
    ///
    /// Needed because an FFT bin spans <c>SampleRate / windowSize</c> Hz, so the same bin index means a
    /// different frequency for a 44.1 kHz track than a 48 kHz one. Any consumer that maps bins onto
    /// fixed frequency bands (the WMP visualizers do, over 20 Hz - 22.05 kHz) is otherwise wrong by ~9%
    /// on every band edge for half of modern files.
    /// </summary>
    int SampleRate { get; }

    /// <summary>
    /// Fills <paramref name="destination"/> with the latest FFT magnitude bins (each roughly 0..1)
    /// and returns the number of bins written, or 0 when nothing is playing. The natural bin count
    /// is half the FFT window (a 2048-point FFT yields 1024 bins); pass a buffer at least that large.
    /// </summary>
    int ReadSpectrum(float[] destination);

    /// <summary>
    /// Fills <paramref name="destination"/> with the latest raw time-domain PCM samples (mono,
    /// downmixed, roughly -1..1) and returns the number of samples written, or 0 when nothing is
    /// playing. Unlike <see cref="ReadSpectrum"/> this is the unprocessed waveform, which the
    /// MilkDrop visualizer needs to draw waveforms and run its own frequency analysis.
    /// </summary>
    int ReadWaveform(float[] destination);

    /// <summary>
    /// Like <see cref="ReadWaveform"/>, but keeps the channels apart: fills <paramref name="left"/> and
    /// <paramref name="right"/> with the latest PCM (roughly -1..1) and returns the number of frames
    /// written, or 0 when nothing is playing. A mono source fills both with the same samples. The WMP
    /// visualizations need this because Windows Media Player analyses each channel separately.
    /// </summary>
    int ReadStereoWaveform(float[] left, float[] right);
}
