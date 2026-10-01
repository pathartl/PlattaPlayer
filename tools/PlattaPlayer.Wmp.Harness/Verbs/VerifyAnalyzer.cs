using PlattaPlayer.Visualizations.Wmp.Audio;
using PlattaPlayer.Wmp.Harness.Interop;

namespace PlattaPlayer.Wmp.Harness.Verbs;

/// <summary>
/// Diffs <see cref="WmpSpectrumAnalyzer"/> against the REAL analyzer (<see cref="SpectrumAnalyzerOracle"/>,
/// wmpeffects.dll's CDMOSA) on identical PCM: every node, both channels, spectrum and waveform, byte for
/// byte. The PCM is 16-bit, widened to 32-bit for the DMO so it takes the double-precision path WMP's
/// pipeline uses (the top 16 bits are the same numbers our port receives).
///
/// Material: the capture's test WAV at 44.1 kHz, and synthetic stereo programmes (tones, noise, clipping,
/// DC offset, silence) at 44.1, 48, 32 and 22.05 kHz to exercise the resampling path.
///
/// Usage: verify-analyzer
/// </summary>
internal static class VerifyAnalyzer
{
    public static int Run(string[] args)
    {
        var failures = 0;
        var wav = Path.Combine(CaptureTimedLevel.Dir, "test.wav");
        if (!File.Exists(wav))
        {
            Directory.CreateDirectory(CaptureTimedLevel.Dir);
            TestSignal.Write(wav);
        }
        failures += Check("test signal 44100", File.ReadAllBytes(wav)[44..], 44100);
        foreach (var rate in new[] { 44100, 48000, 32000, 22050 })
            failures += Check($"programme {rate}", Programme(rate, 12, rate), rate);

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "verify-analyzer: our analyzer is byte-identical to WMP's on every node."
            : $"verify-analyzer: {failures} case(s) DIVERGE — see above.");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Spectrum: every window <c>[i·hop, i·hop + 2048)</c> of each channel through the real
    /// <c>ComputeFrequenciesDbl</c> and through ours. Waveform: node 0 of the real <c>ProcessNbit</c>, the
    /// one window whose extent is unambiguous (later windows depend on how the host chunks its input).
    /// </summary>
    private static int Check(string name, byte[] pcm16, int rate)
    {
        var frames = pcm16.Length / 4;
        var left = new short[frames];
        var right = new short[frames];
        for (var i = 0; i < frames; i++)
        {
            left[i] = BitConverter.ToInt16(pcm16, i * 4);
            right[i] = BitConverter.ToInt16(pcm16, i * 4 + 2);
        }

        var oracle = new SpectrumAnalyzerOracle(rate, 2, 32);
        var ours = new WmpSpectrumAnalyzer();
        var spectrum = new byte[WmpSpectrumAnalyzer.Bins];
        var wave = new byte[WmpSpectrumAnalyzer.Bins];
        var hop = rate / 30;
        int windows = 0, bad = 0, worst = 0;
        string? first = null;

        for (var start = 0; start + WmpSpectrumAnalyzer.WindowSize <= frames; start += hop)
        {
            for (var ch = 0; ch < 2; ch++)
            {
                var src = (ch == 0 ? left : right).AsSpan(start, WmpSpectrumAnalyzer.WindowSize);
                var real = oracle.Analyze(src, ch);
                ours.Analyze(src, rate, spectrum, wave);
                for (var k = 0; k < WmpSpectrumAnalyzer.Bins; k++)
                {
                    if (spectrum[k] == real[k]) continue;
                    bad++;
                    worst = Math.Max(worst, Math.Abs(spectrum[k] - real[k]));
                    first ??= $"window @{start} ch{ch} bin {k}: real {real[k]} ours {spectrum[k]}";
                }
            }
            windows++;
        }

        // Waveform, from node 0 of a fresh streaming analyzer.
        var streaming = new SpectrumAnalyzerOracle(rate, 2, 32);
        streaming.Process(Widen(pcm16[..(WmpSpectrumAnalyzer.WindowSize * 4)]));
        var node = streaming.TakeNew()[0];
        var waveBad = 0;
        for (var ch = 0; ch < 2; ch++)
        {
            ours.Analyze((ch == 0 ? left : right).AsSpan(0, WmpSpectrumAnalyzer.WindowSize), rate, spectrum, wave);
            var realWave = ch == 0 ? node.Wave0 : node.Wave1;
            var realSpectrum = ch == 0 ? node.Spectrum0 : node.Spectrum1;
            for (var k = 0; k < WmpSpectrumAnalyzer.Bins; k++)
            {
                if (wave[k] != realWave[k]) waveBad++;
                if (spectrum[k] != realSpectrum[k]) waveBad++;
            }
        }

        var total = windows * 2 * WmpSpectrumAnalyzer.Bins;
        Console.WriteLine($"  {name,-20} {windows,4} windows: spectrum {total - bad}/{total} identical (worst {worst});" +
                          $" node 0 spectrum+waveform {(waveBad == 0 ? "identical" : $"{waveBad} bytes differ")}" +
                          (first is null ? "" : $"   first: {first}"));
        return bad == 0 && waveBad == 0 ? 0 : 1;
    }

    /// <summary>
    /// A stereo programme: a 55 Hz + 1.2 kHz chord rising from silence, uncorrelated noise, a clipped
    /// square-ish tone, a DC offset, and true silence, a few seconds each.
    /// </summary>
    private static byte[] Programme(int rate, int seconds, int seed)
    {
        var random = new Random(seed);
        var frames = rate * seconds;
        var pcm = new byte[frames * 4];
        for (var i = 0; i < frames; i++)
        {
            var t = (double)i / rate;
            var part = (int)(t / (seconds / 5.0));
            double l, r;
            switch (part)
            {
                case 0:
                    var ramp = t / (seconds / 5.0);
                    l = ramp * (0.4 * Math.Sin(2 * Math.PI * 55 * t) + 0.2 * Math.Sin(2 * Math.PI * 1200 * t));
                    r = ramp * (0.3 * Math.Sin(2 * Math.PI * 55 * t + 1) + 0.3 * Math.Sin(2 * Math.PI * 3300 * t));
                    break;
                case 1:
                    l = (random.NextDouble() * 2 - 1) * 0.3;
                    r = (random.NextDouble() * 2 - 1) * 0.05;
                    break;
                case 2:
                    l = Math.Clamp(1.6 * Math.Sin(2 * Math.PI * 220 * t), -1, 1);
                    r = Math.Clamp(1.2 * Math.Sin(2 * Math.PI * 97 * t), -1, 1);
                    break;
                case 3:
                    l = 0.25 + 0.1 * Math.Sin(2 * Math.PI * 440 * t);
                    r = -0.5;
                    break;
                default:
                    l = 0;
                    r = 0;
                    break;
            }
            BitConverter.TryWriteBytes(pcm.AsSpan(i * 4), (short)Math.Clamp(Math.Round(l * 32767), short.MinValue, short.MaxValue));
            BitConverter.TryWriteBytes(pcm.AsSpan(i * 4 + 2), (short)Math.Clamp(Math.Round(r * 32767), short.MinValue, short.MaxValue));
        }
        return pcm;
    }

    /// <summary>16-bit interleaved PCM widened to 32-bit, as WMP's pipeline delivers it to the analyzer.</summary>
    private static byte[] Widen(byte[] pcm16)
    {
        var result = new byte[pcm16.Length * 2];
        for (var i = 0; i < pcm16.Length / 2; i++)
            BitConverter.TryWriteBytes(result.AsSpan(i * 4), BitConverter.ToInt16(pcm16, i * 2) << 16);
        return result;
    }
}
