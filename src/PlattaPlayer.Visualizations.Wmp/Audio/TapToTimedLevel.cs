using System;
using System.Diagnostics;
using PlattaPlayer.Core.Abstractions;

namespace PlattaPlayer.Visualizations.Wmp.Audio;

/// <summary>
/// Builds the byte <c>TimedLevel</c> block the WMP engines consume from our live audio, the way Windows
/// Media Player itself does: per channel, the latest 2048 samples through <see cref="WmpSpectrumAnalyzer"/>,
/// a port of WMP's own analyzer (wmpeffects.dll's "WMPlayer Spectrum Analyzer DMO") that the harness verb
/// <c>verify-analyzer</c> checks byte for byte against the original.
///
/// This replaced a conversion from BASS's FFT magnitudes that was calibrated by eye. Measured against
/// TimedLevels captured from real WMP (<c>capture-timedlevel</c>), it was wrong in three ways:
/// <list type="bullet">
/// <item>The log encodes POWER (63.75 bytes per decade of amplitude), not magnitude (31.9), so it
/// compressed every dynamic in half.</item>
/// <item>WMP windows with a 4-term Blackman-Harris, which BASS cannot. A tone spreads differently
/// across neighbouring bins, which is exactly where Alchemy reads its bass.</item>
/// <item>It was mono, while WMP analyses the two channels separately. Alchemy deliberately sums odd
/// bins of the left channel and even bins of the right.</item>
/// </list>
/// Samples are converted as WMP's 32-bit pipeline hands them to the analyzer: the top 16 bits of the
/// sample, i.e. <c>floor(x · 32768)</c> clamped to the int16 range.
/// </summary>
public sealed class TapToTimedLevel
{
    private readonly WmpSpectrumAnalyzer _analyzer = new();
    private readonly TimedLevelFrame _frame = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly float[] _left = new float[WmpSpectrumAnalyzer.WindowSize];
    private readonly float[] _right = new float[WmpSpectrumAnalyzer.WindowSize];
    private readonly short[] _samples = new short[WmpSpectrumAnalyzer.WindowSize];
    private bool _wasActive;

    public TimedLevelFrame Frame => _frame;

    /// <summary>
    /// Reads one frame from <paramref name="tap"/>. A missing or idle tap yields the paused state if
    /// something was playing, otherwise the stopped state.
    /// </summary>
    public TimedLevelFrame Update(IAudioTap? tap)
    {
        var frames = tap is { IsActive: true } ? tap.ReadStereoWaveform(_left, _right) : 0;
        if (frames <= 0)
        {
            // Distinguish "was playing, now paused" from "stopped": the engines treat the former as
            // "reuse the previous frame" and the latter as "paint the background".
            _frame.State = _wasActive ? TimedLevelFrame.StatePaused : TimedLevelFrame.StateStopped;
            if (_frame.State == TimedLevelFrame.StateStopped) _frame.Clear();
            _wasActive = false;
            return _frame;
        }

        _wasActive = true;
        _frame.State = TimedLevelFrame.StatePlaying;

        // The renderers skip their entire redraw when the timestamp has not advanced, so this must be a
        // real monotonic clock. Our tap exposes no playback position, so elapsed wall time stands in for
        // it — while playing the two advance together, which is all the skip check cares about.
        // TimedLevel timestamps are in 100 ns units, which is exactly a TimeSpan tick.
        _frame.TimeStamp = _clock.Elapsed.Ticks;

        // Short reads (the first moments of playback) are padded with silence, as the analyzer's own
        // buffer would hold nothing there yet.
        var rate = tap!.SampleRate;
        Analyze(_left, frames, rate, _frame.Frequency0, _frame.Waveform0);
        Analyze(_right, frames, rate, _frame.Frequency1, _frame.Waveform1);
        _frame.ChannelCount = 2;
        return _frame;
    }

    private void Analyze(float[] pcm, int frames, int rate, byte[] spectrum, byte[] waveform)
    {
        for (var i = 0; i < WmpSpectrumAnalyzer.WindowSize; i++)
        {
            var v = i < frames ? Math.Floor(pcm[i] * 32768.0) : 0.0;
            _samples[i] = (short)Math.Clamp(v, short.MinValue, short.MaxValue);
        }
        _analyzer.Analyze(_samples, rate > 0 ? rate : 44100, spectrum, waveform);
    }
}
