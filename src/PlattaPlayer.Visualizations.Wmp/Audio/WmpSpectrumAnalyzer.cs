using System;
using PlattaPlayer.Visualizations.Wmp.BarsAndWaves;

namespace PlattaPlayer.Visualizations.Wmp.Audio;

/// <summary>
/// Windows Media Player's own audio analysis: the code that turns PCM into the TimedLevel bytes every
/// WMP visualization receives. Ported from the "WMPlayer Spectrum Analyzer DMO",
/// <c>WMPEffects::CDMOSA</c> in wmpeffects.dll, read from Microsoft's public PDB. The harness verb
/// <c>verify-analyzer</c> runs that DMO in-process and checks this class byte for byte against it.
///
/// Per channel, over a window of 2048 samples (WMP takes a new window every <c>rate/30</c> samples):
/// <list type="number">
/// <item>Samples are the top 16 bits of the PCM, as numbers in the int16 range.</item>
/// <item>A 4-term Blackman-Harris window (<c>FFTWrapper::Initialize</c>, <c>0x180022320</c>), then a
/// 2048-point real FFT.</item>
/// <item>Power <c>(re² + im²) · 2^-22</c> for bins 1..1023. Bin 0 keeps the raw, unsquared DC term.</item>
/// <item>At any rate other than 44.1 kHz the power spectrum is resampled onto the 44.1 kHz bin grid
/// (21.533 Hz per bin) by a streaming fractional-overlap pass that SUMS what each output bin covers.</item>
/// <item>Each bin becomes a byte through a table-driven log2: <c>Mantissa[(bits >> 12) &amp; 0x7FF] +
/// Exponent[bits >> 23]</c> of its float, clamped to 0..255. A non-positive value is read as 1.0.</item>
/// </list>
/// The waveform bytes are the first 1024 of the 2048 samples, as <c>(sample &gt;&gt; 8) + 128</c>.
///
/// This is <c>ComputeFrequenciesDbl</c>, the double-precision path the DMO takes for input wider than 16
/// bits. WMP's pipeline delivers that, and it is the one whose bins line up with captured TimedLevels.
/// The float path additionally drops the bins below 20 Hz, which WMP's output does not show.
///
/// The log tables are the same ones Bars and Waves uses (<see cref="BarsLevelTables"/>, extracted from
/// wmp.dll). The two DLLs' copies are identical.
/// </summary>
public sealed class WmpSpectrumAnalyzer
{
    public const int WindowSize = 2048;
    public const int Bins = 1024;

    /// <summary><c>0x4035888000000000</c>: 44100/2048 Hz, the bin width every effect assumes.</summary>
    private const double ReferenceBinHz = 21.533203125;

    /// <summary><c>0x3e90000000000000</c>: the power scale.</summary>
    private const double PowerScale = 2.384185791015625e-07;

    private static readonly double[] Window = BuildWindow();
    private static readonly double[] Cos = new double[WindowSize / 2];
    private static readonly double[] Sin = new double[WindowSize / 2];
    private static readonly int[] BitReverse = new int[WindowSize];

    private readonly double[] _re = new double[WindowSize];
    private readonly double[] _im = new double[WindowSize];
    private readonly double[] _out = new double[WindowSize];
    private readonly double[] _resampled = new double[Bins];

    static WmpSpectrumAnalyzer()
    {
        for (var k = 0; k < WindowSize / 2; k++)
        {
            Cos[k] = Math.Cos(2 * Math.PI * k / WindowSize);
            Sin[k] = -Math.Sin(2 * Math.PI * k / WindowSize);
        }
        var bits = 0;
        for (var n = WindowSize; n > 1; n >>= 1) bits++;
        for (var i = 0; i < WindowSize; i++)
        {
            var r = 0;
            for (var b = 0; b < bits; b++) r |= ((i >> b) & 1) << (bits - 1 - b);
            BitReverse[i] = r;
        }
    }

    /// <summary>
    /// The window, built exactly as <c>FFTWrapper::Initialize</c> does: the step is a stored constant
    /// (<c>0x401921FB54442D11</c>, a few ulps off 2π) over N−1, and the three cosine terms are added in
    /// this order.
    /// </summary>
    private static double[] BuildWindow()
    {
        var twoPi = BitConverter.Int64BitsToDouble(0x401921fb54442d11);
        var a0 = BitConverter.Int64BitsToDouble(0x3fd6f5c28f5c28f6); // 0.35875
        var a1 = BitConverter.Int64BitsToDouble(0x3fdf4024b33daf8e); // 0.48829
        var a2 = BitConverter.Int64BitsToDouble(0x3fc2157689ca18bd); // 0.14128
        var a3 = BitConverter.Int64BitsToDouble(0x3f87ebaf102363b2); // 0.01168
        var d = twoPi / (WindowSize - 1);
        var d2 = d + d;
        var d3 = d * 3.0;
        var w = new double[WindowSize];
        for (var n = 0; n < WindowSize; n++)
        {
            double x = n;
            w[n] = a0 - Math.Cos(x * d) * a1 + Math.Cos(x * d2) * a2 - Math.Cos(x * d3) * a3;
        }
        return w;
    }

    /// <summary>
    /// Analyzes one channel's window.
    /// </summary>
    /// <param name="samples">2048 samples, oldest first, in the int16 range.</param>
    /// <param name="sampleRate">The stream's rate; anything but 44.1 kHz is resampled onto its grid.</param>
    /// <param name="spectrum">Receives 1024 frequency bytes.</param>
    /// <param name="waveform">Receives 1024 waveform bytes.</param>
    public void Analyze(ReadOnlySpan<short> samples, int sampleRate, Span<byte> spectrum, Span<byte> waveform)
    {
        if (samples.Length < WindowSize) throw new ArgumentException("Need 2048 samples.", nameof(samples));

        for (var i = 0; i < Bins; i++) waveform[i] = (byte)((samples[i] >> 8) + 128);

        // Windowed, bit-reversed into the FFT's working arrays.
        for (var n = 0; n < WindowSize; n++)
        {
            _re[BitReverse[n]] = Window[n] * samples[n];
            _im[BitReverse[n]] = 0;
        }
        Fft();

        // The DMO's in-place layout: re[0..1024] at their own index, im[k] at N−k. Then power for 1..1023,
        // leaving index 0 (DC) and the rest raw, because the resampler can read past 1023.
        _out[0] = _re[0];
        for (var k = 1; k < WindowSize / 2; k++)
        {
            _out[k] = _re[k];
            _out[WindowSize - k] = _im[k];
        }
        _out[WindowSize / 2] = _re[WindowSize / 2];
        for (var k = 1; k < WindowSize / 2; k++)
            _out[k] = (_out[WindowSize - k] * _out[WindowSize - k] + _out[k] * _out[k]) * PowerScale;

        var binHz = sampleRate * (1.0 / WindowSize);
        var off = ReferenceBinHz - binHz;
        if (off < -0.001 || 0.001 < off) Resample(binHz);

        for (var j = 0; j < Bins; j++) spectrum[j] = Encode((float)_out[j]);
    }

    /// <summary>
    /// The fractional-overlap pass from <c>ComputeFrequenciesDbl</c>: each output bin takes
    /// <c>21.533 / binHz</c> input bins, splitting an input bin across two outputs where they straddle.
    /// </summary>
    private void Resample(double binHz)
    {
        Array.Clear(_resampled);
        var ratio = ReferenceBinHz / binHz;
        var available = 1.0;
        var input = 0;
        for (var j = 0; j < Bins; j++)
        {
            if (input > Bins - 1) break;
            var sum = 0.0;
            if (0.0 < ratio)
            {
                var k = input;
                var need = ratio;
                do
                {
                    var take = need <= available ? need : available;
                    if (take > 1.0) take = 1.0;
                    var left = available - take;
                    need -= take;
                    sum += take * _out[k];
                    input = k + 1;
                    available = 1.0;
                    if (0.0 < left)
                    {
                        input = k;
                        available = left;
                    }
                    k = input;
                } while (0.0 < need);
            }
            _resampled[j] = sum;
        }
        Array.Copy(_resampled, _out, Bins);
    }

    /// <summary>The table-driven log2 shared by every WMP spectrum consumer.</summary>
    private static byte Encode(float value)
    {
        if (value <= 0f) value = 1f;
        var bits = (uint)BitConverter.SingleToInt32Bits(value);
        var v = BarsLevelTables.Mantissa[(bits >> 12) & 0x7FF] + BarsLevelTables.Exponent[(bits >> 23) & 0xFF];
        return v < 0 ? (byte)0 : v > 255 ? (byte)255 : (byte)v;
    }

    /// <summary>In-place iterative radix-2 FFT over the bit-reversed arrays.</summary>
    private void Fft()
    {
        for (var size = 2; size <= WindowSize; size <<= 1)
        {
            var half = size >> 1;
            var step = WindowSize / size;
            for (var start = 0; start < WindowSize; start += size)
            {
                for (var k = 0; k < half; k++)
                {
                    var c = Cos[k * step];
                    var s = Sin[k * step];
                    var a = start + k;
                    var b = a + half;
                    var tr = _re[b] * c - _im[b] * s;
                    var ti = _re[b] * s + _im[b] * c;
                    _re[b] = _re[a] - tr;
                    _im[b] = _im[a] - ti;
                    _re[a] += tr;
                    _im[a] += ti;
                }
            }
        }
    }
}
