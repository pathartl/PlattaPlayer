using System;

namespace PlattaPlayer.Visualizations.MilkDrop.Audio;

/// <summary>
/// Minimal real-input FFT (iterative radix-2 Cooley–Tukey) used to turn the PCM waveform into a
/// magnitude spectrum. MilkDrop/butterchurn drive the bass/mid/treb reactivity off this spectrum
/// rather than off the player's own FFT so the analysis windowing matches the original.
/// </summary>
public sealed class Fft
{
    private readonly int _n;
    private readonly int _bits;
    private readonly int[] _reverse;
    private readonly float[] _cos;
    private readonly float[] _sin;
    private readonly float[] _window;
    private readonly float[] _re;
    private readonly float[] _im;

    /// <param name="size">FFT window length; must be a power of two.</param>
    public Fft(int size)
    {
        if (size < 2 || (size & (size - 1)) != 0)
            throw new ArgumentException("FFT size must be a power of two.", nameof(size));

        _n = size;
        _bits = (int)Math.Log2(size);
        _reverse = new int[size];
        _cos = new float[size / 2];
        _sin = new float[size / 2];
        _window = new float[size];
        _re = new float[size];
        _im = new float[size];

        for (var i = 0; i < size; i++)
            _reverse[i] = ReverseBits(i, _bits);

        for (var i = 0; i < size / 2; i++)
        {
            _cos[i] = (float)Math.Cos(-2.0 * Math.PI * i / size);
            _sin[i] = (float)Math.Sin(-2.0 * Math.PI * i / size);
        }

        // A Hann window suppresses spectral leakage so beats read cleanly.
        for (var i = 0; i < size; i++)
            _window[i] = 0.5f * (1f - (float)Math.Cos(2.0 * Math.PI * i / (size - 1)));
    }

    public int Size => _n;

    /// <summary>Number of usable magnitude bins (half the window).</summary>
    public int BinCount => _n / 2;

    /// <summary>
    /// Computes the windowed magnitude spectrum of <paramref name="samples"/> (length <see cref="Size"/>)
    /// into <paramref name="magnitudes"/> (length <see cref="BinCount"/>).
    /// </summary>
    public void Forward(ReadOnlySpan<float> samples, Span<float> magnitudes)
    {
        var count = Math.Min(samples.Length, _n);
        for (var i = 0; i < count; i++)
        {
            var j = _reverse[i];
            _re[j] = samples[i] * _window[i];
            _im[j] = 0f;
        }
        for (var i = count; i < _n; i++)
        {
            _re[_reverse[i]] = 0f;
            _im[_reverse[i]] = 0f;
        }

        for (var size = 2; size <= _n; size <<= 1)
        {
            var half = size >> 1;
            var step = _n / size;
            for (var i = 0; i < _n; i += size)
            {
                var k = 0;
                for (var j = i; j < i + half; j++)
                {
                    var c = _cos[k];
                    var s = _sin[k];
                    var tre = _re[j + half] * c - _im[j + half] * s;
                    var tim = _re[j + half] * s + _im[j + half] * c;
                    _re[j + half] = _re[j] - tre;
                    _im[j + half] = _im[j] - tim;
                    _re[j] += tre;
                    _im[j] += tim;
                    k += step;
                }
            }
        }

        var bins = Math.Min(magnitudes.Length, _n / 2);
        for (var i = 0; i < bins; i++)
            magnitudes[i] = MathF.Sqrt(_re[i] * _re[i] + _im[i] * _im[i]);
    }

    private static int ReverseBits(int value, int bits)
    {
        var result = 0;
        for (var i = 0; i < bits; i++)
        {
            result = (result << 1) | (value & 1);
            value >>= 1;
        }
        return result;
    }
}
