using PlattaPlayer.Wmp.Harness.Synth;

namespace PlattaPlayer.Wmp.Harness.Verbs;

/// <summary>
/// Reads the TimedLevels <see cref="CaptureTimedLevel"/> recorded and reports, per test segment, what WMP
/// put in the bytes: the spectrum peak and its neighbours (window and leakage), the floor, the bass bins
/// Alchemy sums (ch0 1,3,5 / ch1 2,4,6), the treble bins it sums, and the waveform's range. The segment
/// comes from the TimedLevel's own media timestamp (100 ns units). Frames near a boundary are skipped.
///
/// Usage: analyze-timedlevel [--bins]
/// </summary>
internal static class AnalyzeTimedLevel
{
    private const double Guard = 0.4;

    public static int Run(string[] args)
    {
        var path = Path.Combine(CaptureTimedLevel.Dir, "frames.bin");
        if (!File.Exists(path))
        {
            Console.Error.WriteLine("analyze-timedlevel: no capture — run 'capture-timedlevel' first.");
            return 2;
        }

        var frames = AudioFrameSet.Read(path).Where(f => f.State == 2).ToList();
        var bySegment = new List<AudioFrame>[TestSignal.Segments.Length];
        for (var i = 0; i < bySegment.Length; i++) bySegment[i] = [];

        long? lastStamp = null;
        foreach (var f in frames)
        {
            if (f.TimeStamp == lastStamp) continue; // a repeated frame carries no new analysis
            lastStamp = f.TimeStamp;
            var t = f.TimeStamp / 1e7;
            var s = (int)(t / TestSignal.SegmentSeconds);
            var within = t - s * TestSignal.SegmentSeconds;
            if (s < 0 || s >= bySegment.Length || within < Guard || within > TestSignal.SegmentSeconds - Guard) continue;
            bySegment[s].Add(f);
        }

        Console.WriteLine($"{frames.Count} fresh frames, timestamps {frames.First().TimeStamp / 1e7:0.00}s .. {frames.Last().TimeStamp / 1e7:0.00}s");
        Console.WriteLine();
        for (var s = 0; s < bySegment.Length; s++)
        {
            var set = bySegment[s];
            var seg = TestSignal.Segments[s];
            Console.WriteLine($"[{s}] {seg.Name}  ({set.Count} frames)");
            if (set.Count == 0) continue;

            var f0 = Median(set, f => f.Frequency0);
            var f1 = Median(set, f => f.Frequency1);
            var peak = Array.IndexOf(f0, f0.Max());
            Console.WriteLine($"    ch0 peak bin {peak} = {f0[peak]}   neighbours " +
                              string.Join(" ", Enumerable.Range(peak - 4, 9).Where(i => i is >= 0 and < 1024).Select(i => f0[i])));
            Console.WriteLine($"    ch0 floor (median of all bins) {Sorted(f0)[512]}, min {f0.Min()}   " +
                              $"ch1 peak bin {Array.IndexOf(f1, f1.Max())} = {f1.Max()}");
            Console.WriteLine($"    bass bins ch0[1,3,5] {f0[1]} {f0[3]} {f0[5]}  ch1[2,4,6] {f1[2]} {f1[4]} {f1[6]}  " +
                              $"-> Alchemy bass {(f0[1] + f0[3] + f0[5] + f1[2] + f1[4] + f1[6]) / 1200.0:0.000}");
            Console.WriteLine($"    treble ch0[501,503,505] {f0[501]} {f0[503]} {f0[505]}  ch1[502,504,506] {f1[502]} {f1[504]} {f1[506]}");
            var w0 = set.Select(f => (int)f.Waveform0.Min()).Min();
            var w1 = set.Select(f => (int)f.Waveform0.Max()).Max();
            Console.WriteLine($"    waveform ch0 range {w0}..{w1}   ch1 range " +
                              $"{set.Select(f => (int)f.Waveform1.Min()).Min()}..{set.Select(f => (int)f.Waveform1.Max()).Max()}");

            if (args.Contains("--bins"))
                Console.WriteLine("    ch0 bins 0..63: " + string.Join(" ", f0.Take(64)));
        }
        return 0;
    }

    private static byte[] Median(List<AudioFrame> set, Func<AudioFrame, byte[]> pick)
    {
        var result = new byte[1024];
        var column = new byte[set.Count];
        for (var i = 0; i < 1024; i++)
        {
            for (var k = 0; k < set.Count; k++) column[k] = pick(set[k])[i];
            Array.Sort(column);
            result[i] = column[column.Length / 2];
        }
        return result;
    }

    private static byte[] Sorted(byte[] values)
    {
        var copy = (byte[])values.Clone();
        Array.Sort(copy);
        return copy;
    }
}
