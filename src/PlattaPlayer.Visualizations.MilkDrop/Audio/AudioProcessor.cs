using System;

namespace PlattaPlayer.Visualizations.MilkDrop.Audio;

/// <summary>
/// Turns raw PCM into the per-frame band reactivity MilkDrop presets read. It splits the magnitude
/// spectrum into bass/mid/treb, normalises each against a slow running average so the values sit near
/// 1.0 regardless of overall volume, then keeps an attenuated (smoothed) copy. This is a faithful
/// reproduction of MilkDrop's <c>AnalyzeNewSound</c> behaviour rather than a byte-for-byte port.
/// </summary>
public sealed class AudioProcessor
{
    private const int WindowSize = 1024;           // PCM samples analysed per frame
    private const int Bins = WindowSize / 2;       // 512 magnitude bins

    // Band edges as a fraction of the (perceptually compressed) spectrum. Bass is the lowest sliver,
    // treble the long tail; tuned to feel like the original three-band split.
    private const int BassEnd = 8;                 // bins [0, 8)
    private const int MidEnd = 80;                 // bins [8, 80); treble is [80, Bins)

    private readonly Fft _fft = new(WindowSize);
    private readonly AudioFrame _frame = new(WindowSize);
    private readonly float[] _magnitudes = new float[Bins];

    // Long-term averages per band, used to normalise the instantaneous energy to ~1.0.
    private readonly float[] _longAvg = { 1f, 1f, 1f };
    // Attenuated (smoothed) normalised values exposed as *_att.
    private readonly float[] _att = { 1f, 1f, 1f };

    /// <summary>Latest analysed frame (reused; valid until the next <see cref="Process"/> call).</summary>
    public AudioFrame Frame => _frame;

    /// <summary>
    /// Analyses <paramref name="samples"/> (mono PCM, any length; the most recent
    /// <see cref="WindowSize"/> are used) and refreshes <see cref="Frame"/>.
    /// </summary>
    public AudioFrame Process(ReadOnlySpan<float> samples, int sampleCount)
    {
        var n = Math.Min(sampleCount, samples.Length);
        var wave = _frame.Waveform;
        Array.Clear(wave);
        // Right-align the newest samples into the window so partial reads still drive the FFT.
        var copy = Math.Min(n, WindowSize);
        for (var i = 0; i < copy; i++)
            wave[WindowSize - copy + i] = samples[n - copy + i];

        _fft.Forward(wave, _magnitudes);
        _magnitudes.AsSpan().CopyTo(_frame.Spectrum);

        Span<float> imm = stackalloc float[3];
        for (var i = 0; i < Bins; i++)
        {
            var m = _magnitudes[i];
            if (i < BassEnd) imm[0] += m;
            else if (i < MidEnd) imm[1] += m;
            else imm[2] += m;
        }

        // Normalise each band against its slow average; smooth the result for the *_att values.
        Span<float> norm = stackalloc float[3];
        for (var b = 0; b < 3; b++)
        {
            _longAvg[b] = _longAvg[b] * 0.992f + imm[b] * 0.008f;
            norm[b] = _longAvg[b] < 1e-4f ? 1f : imm[b] / _longAvg[b];
            _att[b] = _att[b] * 0.6f + norm[b] * 0.4f;
        }

        _frame.Bass = norm[0];
        _frame.Mid = norm[1];
        _frame.Treb = norm[2];
        _frame.BassAtt = _att[0];
        _frame.MidAtt = _att[1];
        _frame.TrebAtt = _att[2];
        return _frame;
    }

    /// <summary>Resets the running averages (e.g. on track change or when playback stops).</summary>
    public void Reset()
    {
        Array.Fill(_longAvg, 1f);
        Array.Fill(_att, 1f);
    }
}
