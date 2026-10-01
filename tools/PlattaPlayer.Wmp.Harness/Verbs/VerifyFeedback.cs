using PlattaPlayer.Visualizations.Wmp.Alchemy;
using PlattaPlayer.Wmp.Harness.Interop;

namespace PlattaPlayer.Wmp.Harness.Verbs;

/// <summary>
/// Diffs <see cref="FeedbackPass.GatherAndDecay"/> against the REAL <c>FUN_18000d940</c>.
///
/// This is the pass that runs over every pixel of every frame, so it is where a small systematic error
/// turns into "the whole field is too bright". Our version has always been believed correct from reading
/// the decompile; nothing had ever checked it.
///
/// The test drives the two sides through the same maps and compares BOTH output buffers — the blurred
/// frame and the gathered scratch — because a gather bug and a blur bug produce very similar-looking
/// frames and there is no reason to make them hard to tell apart.
///
/// Usage: verify-feedback [--verbose]
/// </summary>
internal static class VerifyFeedback
{
    private const uint EdgeColour = 0x00102030;

    public static int Run(string[] args)
    {
        var verbose = args.Any(a => string.Equals(a, "--verbose", StringComparison.OrdinalIgnoreCase));

        Console.WriteLine(MpvisModule.Describe());
        Console.WriteLine($"real FUN_18000d940 vs FeedbackPass.GatherAndDecay, edge colour #{EdgeColour:X6}");
        Console.WriteLine();

        var checks = 0;
        var failures = 0;

        // 640x480 is the shipped field; the small sizes make a failure legible, and 30x25 is deliberately
        // NOT a multiple of four — see the divisibility note below.
        foreach (var (w, h) in ((int W, int H)[])[(32, 24), (16, 16), (64, 48), (640, 480), (30, 25)])
        foreach (var (name, build) in Maps())
        {
            checks++;
            using var oracle = new FeedbackOracle(w, h) { EdgeColour = EdgeColour };
            build(oracle.Map, w, h);
            Paint(oracle.Frame, w, h);
            Array.Fill(oracle.Scratch, unchecked((int)0xDEADBEEF));

            var frame = new int[w * h];
            var scratch = new int[w * h];
            var map = (int[])oracle.Map.Clone();
            Paint(frame, w, h);
            Array.Fill(scratch, unchecked((int)0xDEADBEEF));

            oracle.Run();
            FeedbackPass.GatherAndDecay(frame, scratch, map, w, h, unchecked((int)EdgeColour));

            var (frameDiff, frameFirst) = Compare(oracle.Frame, frame);
            var (scratchDiff, scratchFirst) = Compare(oracle.Scratch, scratch);

            // On a pixel count that is not a multiple of four the real function bails after its first
            // surface swap, so the surfaces ARE left crossed and nothing else happens. That is the
            // expected outcome there, not a failure.
            var bails = ((w * h) & 3) != 0;
            var swapOk = oracle.CurrentIsFrame != bails;

            var label = $"{w}x{h} {name}";
            if (frameDiff == 0 && scratchDiff == 0 && swapOk)
            {
                if (verbose)
                    Console.WriteLine($"  {label,-28} ok{(bails ? "   (count not a multiple of 4: both sides no-op)" : "")}");
                continue;
            }

            failures++;
            Console.WriteLine($"  {label,-28} frame DIFF {frameDiff,7}   scratch DIFF {scratchDiff,7}" +
                              (swapOk ? "" : "   <-- surface swap unexpected"));
            Report("frame", oracle.Frame, frame, frameFirst, w);
            Report("scratch", oracle.Scratch, scratch, scratchFirst, w);
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? $"verify-feedback: {checks}/{checks} cases exact — gather, blur and edge rows all match."
            : $"verify-feedback: {failures} of {checks} cases DIVERGE.");
        return failures == 0 ? 0 : 1;
    }

    private static void Report(string which, int[] real, int[] ours, int first, int w)
    {
        if (first < 0) return;
        Console.WriteLine($"      {which} first ({first % w},{first / w}): " +
                          $"real #{real[first] & 0xFFFFFF:X6} ours #{ours[first] & 0xFFFFFF:X6}");
    }

    /// <summary>
    /// Maps chosen to exercise the paths that differ. The identity map isolates the blur; the shifts move
    /// content across row boundaries, which is where the blur's flat byte walk and a proper 2D loop part
    /// company; the collapse and the scatter make every destination sample somewhere unrelated, so a
    /// gather that reads its own partially-written output would show immediately.
    /// </summary>
    private static IEnumerable<(string Name, Action<int[], int, int> Build)> Maps()
    {
        yield return ("identity", (m, w, h) =>
        {
            for (var i = 0; i < m.Length; i++) m[i] = i;
        });

        yield return ("shift x+1", (m, w, h) =>
        {
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                m[y * w + x] = y * w + Math.Min(w - 1, x + 1);
        });

        yield return ("shift y+1", (m, w, h) =>
        {
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                m[y * w + x] = Math.Min(h - 1, y + 1) * w + x;
        });

        yield return ("collapse to 0", (m, w, h) => Array.Clear(m));

        yield return ("scatter", (m, w, h) =>
        {
            var rng = new Random(20260814);
            for (var i = 0; i < m.Length; i++) m[i] = rng.Next(m.Length);
        });
    }

    /// <summary>
    /// A gradient in all three channels on different periods, so the four blur taps never coincide and a
    /// wrong neighbour cannot land on the right answer.
    /// </summary>
    private static void Paint(int[] pixels, int w, int h)
    {
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
            pixels[y * w + x] = unchecked((int)0xFF000000)
                              | ((x * 7 + y * 3) & 0xFF) << 16
                              | ((y * 11 + 5) & 0xFF) << 8
                              | ((x * 13 + y * 29) & 0xFF);
    }

    /// <summary>
    /// RGB only. The real blur writes THREE bytes per pixel and leaves the fourth untouched — the original
    /// renders to BGRX where it is padding — so the top byte of a blurred pixel still holds whatever the
    /// previous frame left there. Ours writes an opaque alpha because our buffer is composited by Avalonia.
    /// </summary>
    private static (int Diff, int First) Compare(int[] real, int[] ours)
    {
        var diff = 0;
        var first = -1;
        for (var i = 0; i < real.Length; i++)
        {
            if ((real[i] & 0xFFFFFF) == (ours[i] & 0xFFFFFF)) continue;
            diff++;
            if (first < 0) first = i;
        }
        return (diff, first);
    }
}
