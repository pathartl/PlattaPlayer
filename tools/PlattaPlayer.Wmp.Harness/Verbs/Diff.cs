using PlattaPlayer.Wmp.Harness.Imaging;
using PlattaPlayer.Wmp.Harness.Synth;

namespace PlattaPlayer.Wmp.Harness.Verbs;

/// <summary>
/// Compares our rendered frames against the captured ground truth and reports where they diverge.
///
/// The per-segment breakdown matters more than the headline number: the synthetic sequence is built from
/// segments that each exercise a specific behaviour, so a failure localised to one segment names the
/// broken feature directly. Contact sheets (ours | theirs | amplified difference) are written for the
/// worst frames, because seeing WHAT differs is what actually finds the bug.
/// </summary>
internal static class Diff
{
    public static int Run(string[] args)
    {
        var effect = args.Length > 0 ? args[0].ToLowerInvariant() : "barswaves";
        var preset = ArgInt(args, "--preset", 0);
        var (w, h) = Capture.ArgSize(args, "--size", 640, 480);
        var freshState = ArgInt(args, "--fresh-state", 2);

        var truthPath = HarnessPaths.FrameSetFile(HarnessPaths.TruthDir, effect, preset, w, h, freshState);
        var oursPath = HarnessPaths.FrameSetFile(HarnessPaths.OursDir, effect, preset, w, h, freshState);
        if (!File.Exists(truthPath)) { Console.Error.WriteLine($"missing {truthPath}"); return 2; }
        if (!File.Exists(oursPath)) { Console.Error.WriteLine($"missing {oursPath}"); return 2; }

        var truth = ImageFrameSet.Read(truthPath);
        var ours = ImageFrameSet.Read(oursPath);
        var count = Math.Min(truth.Frames.Count, ours.Frames.Count);

        Console.WriteLine($"== {effect} preset {preset} at {w}x{h} (fresh state {freshState}) ==");
        Console.WriteLine();

        var perFrame = new FrameStats[count];
        for (var i = 0; i < count; i++) perFrame[i] = Compare(truth.Frames[i], ours.Frames[i]);

        Console.WriteLine("   segment                          frames  exact%   mean|d|  max|d|");
        Console.WriteLine("   ------------------------------- ------- -------  -------  ------");
        foreach (var (start, end, name) in SyntheticSequence.Segments)
        {
            var take = Math.Min(end, count - 1) - start + 1;
            if (take <= 0) continue;
            var slice = perFrame.Skip(start).Take(take).ToList();
            if (slice.Count == 0) continue;
            Console.WriteLine($"   {name,-31} {slice.Count,7} {slice.Average(s => s.ExactFraction) * 100,7:F3} " +
                              $"{slice.Average(s => s.MeanAbs),8:F3} {slice.Max(s => s.MaxAbs),7}");
        }

        var overallExact = perFrame.Average(s => s.ExactFraction);
        var perfect = perFrame.Count(s => s.MaxAbs == 0);
        Console.WriteLine();
        Console.WriteLine($"   {perfect}/{count} frames are pixel-identical; overall exact {overallExact:P4}");

        var firstBad = Array.FindIndex(perFrame, s => s.MaxAbs > 0);
        if (firstBad >= 0)
        {
            Console.WriteLine($"   first differing frame: {firstBad} " +
                              $"(exact {perFrame[firstBad].ExactFraction:P3}, max |d| {perFrame[firstBad].MaxAbs})");
            ReportDifferingPixels(truth.Frames[firstBad], ours.Frames[firstBad], w, h, 12);
        }

        // Explicit frames beat "the worst" when hunting a cause: the earliest divergence is usually the
        // simplest picture, before feedback and history have compounded it.
        var explicitFrames = ArgList(args, "--frames");
        var worst = explicitFrames.Count > 0
            ? explicitFrames.Where(i => i >= 0 && i < count).Select(i => (s: perFrame[i], i)).ToList()
            : perFrame.Select((s, i) => (s, i)).Where(t => t.s.MaxAbs > 0)
                      .OrderByDescending(t => t.s.MeanAbs).Take(4).ToList();
        if (firstBad >= 0 && explicitFrames.Count == 0 && worst.All(w => w.i != firstBad))
            worst.Add((perFrame[firstBad], firstBad));
        if (worst.Count > 0)
        {
            var dir = Path.Combine(HarnessPaths.ReportDir, $"{effect}-p{preset}-diff");
            Directory.CreateDirectory(dir);
            foreach (var (s, i) in worst)
            {
                WriteContactSheet(Path.Combine(dir, $"frame{i:D4}.png"),
                    ours.Frames[i], truth.Frames[i], w, h);
                Console.WriteLine($"   frame {i,3}: exact {s.ExactFraction:P3}, mean |d| {s.MeanAbs:F2}, max |d| {s.MaxAbs}");
            }
            Console.WriteLine($"   contact sheets (ours | theirs | 8x difference) in {dir}");
        }

        return perfect == count ? 0 : 1;
    }

    /// <summary>
    /// Prints the first few differing pixels with both colours. A contact sheet shows WHERE a frame is
    /// wrong; this shows exactly WHAT the two renderers put there, which is what identifies the cause.
    /// </summary>
    private static void ReportDifferingPixels(byte[] truth, byte[] ours, int w, int h, int limit)
    {
        var shown = 0;
        for (var y = 0; y < h && shown < limit; y++)
        {
            for (var x = 0; x < w && shown < limit; x++)
            {
                var i = (y * w + x) * 4;
                if (truth[i] == ours[i] && truth[i + 1] == ours[i + 1] && truth[i + 2] == ours[i + 2]) continue;
                Console.WriteLine($"     ({x,4},{y,4})  theirs #{truth[i + 2]:X2}{truth[i + 1]:X2}{truth[i]:X2}" +
                                  $"   ours #{ours[i + 2]:X2}{ours[i + 1]:X2}{ours[i]:X2}");
                shown++;
            }
        }
    }

    private readonly record struct FrameStats(double ExactFraction, double MeanAbs, int MaxAbs);

    private static FrameStats Compare(byte[] truth, byte[] ours)
    {
        long exact = 0, sum = 0;
        var max = 0;
        var pixels = truth.Length / 4;
        for (var i = 0; i < truth.Length; i += 4)
        {
            var db = Math.Abs(truth[i] - ours[i]);
            var dg = Math.Abs(truth[i + 1] - ours[i + 1]);
            var dr = Math.Abs(truth[i + 2] - ours[i + 2]);
            if ((db | dg | dr) == 0) exact++;
            var worst = Math.Max(db, Math.Max(dg, dr));
            if (worst > max) max = worst;
            sum += db + dg + dr;
        }
        return new FrameStats((double)exact / pixels, (double)sum / (pixels * 3), max);
    }

    private static void WriteContactSheet(string path, byte[] ours, byte[] truth, int w, int h)
    {
        var sheet = new byte[w * 3 * h * 4];
        var stride = w * 3;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var src = (y * w + x) * 4;
                Copy(ours, src, sheet, (y * stride + x) * 4);
                Copy(truth, src, sheet, (y * stride + w + x) * 4);

                var d = (y * stride + 2 * w + x) * 4;
                for (var c = 0; c < 3; c++)
                    sheet[d + c] = (byte)Math.Min(255, Math.Abs(ours[src + c] - truth[src + c]) * 8);
                sheet[d + 3] = 0xFF;
            }
        }
        PngWriter.WriteBgra(path, sheet, w * 3, h);
    }

    private static void Copy(byte[] src, int si, byte[] dst, int di)
    {
        dst[di] = src[si];
        dst[di + 1] = src[si + 1];
        dst[di + 2] = src[si + 2];
        dst[di + 3] = 0xFF;
    }

    private static int ArgInt(string[] args, string name, int fallback)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(args[i + 1], out var v)) return v;
        return fallback;
    }

    private static List<int> ArgList(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                  .Select(int.Parse).ToList();
        return [];
    }
}
