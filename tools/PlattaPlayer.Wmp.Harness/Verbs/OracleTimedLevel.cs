using PlattaPlayer.Wmp.Harness.Interop;
using PlattaPlayer.Wmp.Harness.Synth;

namespace PlattaPlayer.Wmp.Harness.Verbs;

/// <summary>
/// Checks that <see cref="SpectrumAnalyzerOracle"/> (the real analyzer, driven in-process) reproduces
/// what WMP actually delivered (<see cref="CaptureTimedLevel"/>). It feeds the analyzer the same test WAV
/// and matches nodes to captured frames by timestamp, comparing the spectrum (both copies the node
/// holds) and the waveform byte for byte.
///
/// Usage: oracle-timedlevel
/// </summary>
internal static class OracleTimedLevel
{
    public static int Run(string[] args)
    {
        if (args.Contains("--search")) return Search(args);
        if (args.Contains("--closeness")) return Closeness();
        var framesPath = Path.Combine(CaptureTimedLevel.Dir, "frames.bin");
        var wav = Path.Combine(CaptureTimedLevel.Dir, "test.wav");
        if (!File.Exists(framesPath) || !File.Exists(wav))
        {
            Console.Error.WriteLine("oracle-timedlevel: no capture — run 'capture-timedlevel' first.");
            return 2;
        }

        var pcm = File.ReadAllBytes(wav).AsSpan(44); // canonical 44-byte header written by TestSignal
        var oracle = new SpectrumAnalyzerOracle(TestSignal.Rate, 2);
        var nodes = new List<SpectrumAnalyzerOracle.Node>();
        for (var o = 0; o < pcm.Length; o += 17640) // 100 ms chunks, as a renderer would hand them over
        {
            oracle.Process(pcm.Slice(o, Math.Min(17640, pcm.Length - o)));
            nodes.AddRange(oracle.TakeNew());
        }
        Console.WriteLine($"analyzer produced {nodes.Count} TimedLevels; first at {nodes[0].Time / 1e7:0.0000}s, " +
                          $"step {(nodes[1].Time - nodes[0].Time) / 1e4:0.000} ms");

        var captured = AudioFrameSet.Read(framesPath).Where(f => f.State == 2)
            .GroupBy(f => f.TimeStamp).Select(g => g.First()).ToList();
        var byTime = nodes.ToDictionary(n => n.Time);

        int matchedTime = 0, mainExact = 0, altExact = 0, waveExact = 0, compared = 0;
        var offsets = new Dictionary<long, int>();
        foreach (var f in captured)
        {
            compared++;
            // Exact time match first; otherwise the nearest node, recording the offset.
            if (!byTime.TryGetValue(f.TimeStamp, out var n))
            {
                n = nodes.MinBy(x => Math.Abs(x.Time - f.TimeStamp))!;
                var d = n.Time - f.TimeStamp;
                offsets[d] = offsets.GetValueOrDefault(d) + 1;
            }
            else matchedTime++;

            if (n.Spectrum0.AsSpan().SequenceEqual(f.Frequency0) && n.Spectrum1.AsSpan().SequenceEqual(f.Frequency1)) mainExact++;
            if (n.Unshifted0.AsSpan().SequenceEqual(f.Frequency0) && n.Unshifted1.AsSpan().SequenceEqual(f.Frequency1)) altExact++;
            if (n.Wave0.AsSpan().SequenceEqual(f.Waveform0) && n.Wave1.AsSpan().SequenceEqual(f.Waveform1)) waveExact++;
        }

        // Content match: the host stamps frames with its own presentation clock, so find each non-silent
        // captured frame among the nodes by its bytes, and report the timestamp offset of the match.
        int nonSilent = 0, contentMain = 0, contentAlt = 0, contentWave = 0;
        var contentOffsets = new Dictionary<long, int>();
        foreach (var f in captured)
        {
            if (!f.Frequency0.Any(b => b != 0) && !f.Frequency1.Any(b => b != 0)) continue;
            nonSilent++;
            var main = nodes.FirstOrDefault(n => n.Spectrum0.AsSpan().SequenceEqual(f.Frequency0) && n.Spectrum1.AsSpan().SequenceEqual(f.Frequency1));
            if (main is not null)
            {
                contentMain++;
                var d = (f.TimeStamp - main.Time) / 10000;
                contentOffsets[d] = contentOffsets.GetValueOrDefault(d) + 1;
                if (main.Wave0.AsSpan().SequenceEqual(f.Waveform0) && main.Wave1.AsSpan().SequenceEqual(f.Waveform1)) contentWave++;
            }
            if (nodes.Any(n => n.Unshifted0.AsSpan().SequenceEqual(f.Frequency0))) contentAlt++;
        }
        Console.WriteLine($"non-silent captured frames: {nonSilent}");
        Console.WriteLine($"  found exactly among the analyzer's nodes (+0 spectrum):      {contentMain}");
        Console.WriteLine($"  found exactly among the analyzer's nodes (+0x1018 spectrum): {contentAlt}");
        Console.WriteLine($"  of the +0 matches, waveform also identical:                 {contentWave}");
        Console.WriteLine("  capture time - node time (ms): " +
                          string.Join(", ", contentOffsets.OrderByDescending(kv => kv.Value).Take(8).Select(kv => $"{kv.Key}x{kv.Value}")));
        Console.WriteLine();
        Console.WriteLine($"captured fresh frames: {compared}; exact timestamp matches {matchedTime}");
        if (offsets.Count > 0)
            Console.WriteLine("  nearest-node offsets (100 ns): " +
                              string.Join(", ", offsets.OrderByDescending(kv => kv.Value).Take(6).Select(kv => $"{kv.Key}x{kv.Value}")));
        Console.WriteLine($"  spectrum identical (+0 copy):      {mainExact}/{compared}");
        Console.WriteLine($"  spectrum identical (+0x1018 copy): {altExact}/{compared}");
        Console.WriteLine($"  waveform identical:                {waveExact}/{compared}");
        return 0;
    }

    /// <summary>
    /// How close the analyzer gets, per steady segment: mean absolute byte difference between the captured
    /// spectrum and the analyzer's, for both node copies, at the WAV's 44.1 kHz and resampled (linear) to
    /// 48 kHz. Matched by segment, not by time, so alignment does not matter.
    /// </summary>
    private static int Closeness()
    {
        var wav = File.ReadAllBytes(Path.Combine(CaptureTimedLevel.Dir, "test.wav"))[44..];
        var captured = AudioFrameSet.Read(Path.Combine(CaptureTimedLevel.Dir, "frames.bin")).Where(f => f.State == 2).ToList();
        foreach (var rate in new[] { 44100, 48000 })
        {
            var pcm = rate == 44100 ? wav : Resample(wav, 44100, rate);
            var oracle = new SpectrumAnalyzerOracle(rate, 2, 32);
            oracle.Process(Widen(pcm));
            var nodes = oracle.TakeNew();
            Console.WriteLine($"=== analyzer at {rate} Hz ({nodes.Count} nodes)");
            for (var s = 1; s < TestSignal.Segments.Length - 1; s++)
            {
                double T0(long t) => t / 1e7 - s * TestSignal.SegmentSeconds;
                var cap = captured.Where(f => T0(f.TimeStamp) is > 0.6 and < 2.4).ToList();
                var mine = nodes.Where(n => T0(n.Time) is > 0.6 and < 2.4).ToList();
                if (cap.Count == 0 || mine.Count == 0) continue;
                var c = MeanSpectrum(cap.Select(f => f.Frequency0));
                var a = MeanSpectrum(mine.Select(n => n.Spectrum0));
                var b = MeanSpectrum(mine.Select(n => n.Unshifted0));
                var peak = Array.IndexOf(c, c.Max());
                Console.WriteLine($"  {TestSignal.Segments[s].Name,-14} peak capture {c[peak],6:0.0}  +0: {Diff(c, a),5:0.00} (peak {a[peak],6:0.0})  +0x1018: {Diff(c, b),5:0.00} (peak {b[peak],6:0.0})" +
                                  $"   own peaks +0@{Array.IndexOf(a, a.Max())}={a.Max():0.0} +0x1018@{Array.IndexOf(b, b.Max())}={b.Max():0.0}");
            }
        }
        return 0;
    }

    /// <summary>16-bit interleaved PCM widened to 32-bit, as WMP's pipeline delivers it to the analyzer.</summary>
    private static byte[] Widen(byte[] pcm16)
    {
        var result = new byte[pcm16.Length * 2];
        for (var i = 0; i < pcm16.Length / 2; i++)
            BitConverter.TryWriteBytes(result.AsSpan(i * 4), BitConverter.ToInt16(pcm16, i * 2) << 16);
        return result;
    }

    private static double[] MeanSpectrum(IEnumerable<byte[]> rows)
    {
        var list = rows.ToList();
        var m = new double[1024];
        foreach (var r in list) for (var k = 0; k < 1024; k++) m[k] += r[k];
        for (var k = 0; k < 1024; k++) m[k] /= list.Count;
        return m;
    }

    private static double Diff(double[] x, double[] y) => Enumerable.Range(0, 1024).Average(k => Math.Abs(x[k] - y[k]));

    private static byte[] Resample(byte[] pcm, int from, int to)
    {
        var frames = pcm.Length / 4;
        var outFrames = (int)((long)frames * to / from);
        var result = new byte[outFrames * 4];
        for (var i = 0; i < outFrames; i++)
        {
            var src = (double)i * from / to;
            var j = (int)src;
            var f = src - j;
            for (var ch = 0; ch < 2; ch++)
            {
                var a = BitConverter.ToInt16(pcm, Math.Min(j, frames - 1) * 4 + ch * 2);
                var b = BitConverter.ToInt16(pcm, Math.Min(j + 1, frames - 1) * 4 + ch * 2);
                BitConverter.TryWriteBytes(result.AsSpan(i * 4 + ch * 2), (short)Math.Round(a + (b - a) * f));
            }
        }
        return result;
    }

    /// <summary>
    /// Finds the window alignment: prepends (positive) or drops (negative) s samples of the WAV before
    /// analysis, and counts how many non-silent captured frames then appear byte-exact (both channels,
    /// +0x1018 spectrum) among the nodes.
    /// </summary>
    private static int Search(string[] args)
    {
        var wav = File.ReadAllBytes(Path.Combine(CaptureTimedLevel.Dir, "test.wav"))[44..];
        var captured = AudioFrameSet.Read(Path.Combine(CaptureTimedLevel.Dir, "frames.bin"))
            .Where(f => f.State == 2 && f.Frequency0.Any(b => b != 0)).ToList();
        var targets = captured.Select(f => Convert.ToHexString(f.Frequency0) + Convert.ToHexString(f.Frequency1)).ToHashSet();
        int lo = -1470, hi = 1470, step = 1;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--lo") lo = int.Parse(args[i + 1]);
            if (args[i] == "--hi") hi = int.Parse(args[i + 1]);
            if (args[i] == "--step") step = int.Parse(args[i + 1]);
        }
        var best = (Offset: 0, Hits: -1);
        for (var s = lo; s <= hi; s += step)
        {
            byte[] pcm = s >= 0 ? [.. new byte[s * 4], .. wav] : wav[(-s * 4)..];
            var oracle = new SpectrumAnalyzerOracle(TestSignal.Rate, 2, 32);
            oracle.Process(Widen(pcm));
            var nodes = oracle.TakeNew();
            var hitsMain = nodes.Count(n => targets.Contains(Convert.ToHexString(n.Spectrum0) + Convert.ToHexString(n.Spectrum1)));
            var hitsAlt = nodes.Count(n => targets.Contains(Convert.ToHexString(n.Unshifted0) + Convert.ToHexString(n.Unshifted1)));
            var hits = Math.Max(hitsMain, hitsAlt);
            if (hits > 0) Console.WriteLine($"  offset {s,6}: +0 {hitsMain}, +0x1018 {hitsAlt}");
            if (hits > best.Hits) best = (s, hits);
            if (hits > 0 && step == 1 || s % (step * 10) == lo % (step * 10)) Console.WriteLine($"  offset {s,6}: {hits} exact");
        }
        Console.WriteLine($"best offset {best.Offset} samples: {best.Hits} of {targets.Count} distinct non-silent captured spectra found exactly");
        return 0;
    }
}
