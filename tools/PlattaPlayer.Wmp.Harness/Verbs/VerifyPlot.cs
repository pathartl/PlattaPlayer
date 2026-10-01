using PlattaPlayer.Visualizations.Wmp.Alchemy;
using PlattaPlayer.Visualizations.Wmp.Framebuffer;
using PlattaPlayer.Wmp.Harness.Interop;

namespace PlattaPlayer.Wmp.Harness.Verbs;

/// <summary>
/// Drives the REAL plot (<c>FUN_18000b730</c>) and its five-tap blur (<c>FUN_18000b650</c>) one pixel at a
/// time, and diffs the whole surface against <see cref="DrawPrimitives.Plot"/>.
///
/// Everything about the footprint was implemented from READING the decompile and never checked, which is
/// the same position the shift kernels were in when their randomizers turned out to be drawing parameters
/// in the wrong order. The footprint dominates how much ink the effect lays down, so a wrong neighbour
/// weight or a wrong blur tap changes the whole character of the output while still looking plausible.
///
/// Two things make the test meaningful rather than decorative:
/// <list type="bullet">
/// <item>The background is a GRADIENT, not a flat colour. Every one of the five blur taps then holds a
/// different value, so reading the wrong neighbour — or blurring in the wrong order, which matters
/// because <c>FUN_18000b650</c> writes in place and the next tap sees the result — produces a different
/// answer. Against a flat field all of those mistakes agree.</item>
/// <item>It plots RUNS of adjacent points as well as isolated ones, because that is the only way the
/// in-place blur's order dependence shows up at all.</item>
/// </list>
///
/// Usage: verify-plot [--verbose]
/// </summary>
internal static unsafe class VerifyPlot
{
    private const int W = 40;
    private const int H = 40;

    private const uint Colour = 0x00FF8040;

    private const int LineDivisor = 0x88 + 0x24;

    /// <summary>The colour field itself, so the palette-advance probe can read it back.</summary>
    private const int LineColour = 0x88 + 0x0c;

    public static int Run(string[] args)
    {
        var verbose = args.Any(a => string.Equals(a, "--verbose", StringComparison.OrdinalIgnoreCase));

        Console.WriteLine(MpvisModule.Describe());
        Console.WriteLine($"real FUN_18000b730 + FUN_18000b650 vs DrawPrimitives.Plot — {W}x{H} over a gradient");
        Console.WriteLine();

        using var oracle = new SplineOracle(W, H) { Colour = Colour };
        if (oracle.Peek(LineDivisor) == 0) oracle.Poke(LineDivisor, 1);

        var ours = new PixelBuffer(W, H);
        var draw = new DrawPrimitives();

        HoldProbe(oracle);

        var checks = 0;
        var failures = 0;

        foreach (var mode in (int[])[0, 1, 2, 3])
        foreach (var alpha in (float[])[0f, 0.1f, 0.3f, 0.5f, 0.9f, 1f])
        {
            oracle.PlotMode = mode;
            oracle.NeighbourAlpha = alpha;
            draw.NeighbourAlpha = alpha;

            foreach (var (points, name) in Cases())
            {
                checks++;
                Paint(oracle.Pixels);
                foreach (var (x, y) in points) oracle.Plot(x, y);

                Paint(ours.Pixels);
                foreach (var (x, y) in points) draw.Plot(ours, x, y, unchecked((int)Colour), mode);

                var (diff, first) = Compare(oracle.Pixels, ours.Pixels);
                if (diff == 0)
                {
                    if (verbose) Console.WriteLine($"  mode {mode} alpha {alpha:0.0}  {name,-22} ok");
                    continue;
                }

                failures++;
                Console.WriteLine($"  mode {mode} alpha {alpha:0.0}  {name,-22} DIFF {diff,4} px");
                if (first >= 0)
                    Console.WriteLine($"      first ({first % W},{first / W}): " +
                                      $"real #{oracle.Pixels[first] & 0xFFFFFF:X6} ours #{ours.Pixels[first] & 0xFFFFFF:X6}");
            }
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? $"verify-plot: {checks}/{checks} cases exact — footprint, neighbour weights and blur all match."
            : $"verify-plot: {failures} of {checks} cases DIVERGE.");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Does the hold byte actually freeze the colour? <c>verify-spline</c> could not compare colours
    /// because the real stroke came out painted in a walking colour despite hold being set — but
    /// <c>FUN_18000b730</c> plainly gates its <c>AdvanceColorTransition</c> call on that byte. Isolating a
    /// single plot separates the two possibilities: either the byte does nothing, or the advance during a
    /// spline comes from somewhere else (the spline's own per-point vtable callback).
    /// </summary>
    private static void HoldProbe(SplineOracle oracle)
    {
        oracle.PlotMode = 1;

        oracle.Hold = true;
        oracle.Colour = Colour;
        Paint(oracle.Pixels);
        for (var i = 0; i < 20; i++) oracle.Plot(10 + i, 20);
        var held = oracle.Peek(LineColour) & 0xFFFFFF;

        oracle.Hold = false;
        oracle.Colour = Colour;
        Paint(oracle.Pixels);
        for (var i = 0; i < 20; i++) oracle.Plot(10 + i, 20);
        var free = oracle.Peek(LineColour) & 0xFFFFFF;

        oracle.Hold = true;
        oracle.Colour = Colour;

        Console.WriteLine($"  hold probe: 20 plots from #{Colour:X6} — hold set leaves #{held:X6}, " +
                          $"hold clear leaves #{free:X6}");
        Console.WriteLine(held == Colour
            ? "              the hold byte DOES suppress the advance, so a walking colour during a spline"
            : "              the hold byte does NOT suppress the advance");
        Console.WriteLine(held == Colour
            ? "              must come from the spline's own per-point callback, not from the plot."
            : "              — the field moves even with the byte set.");
        Console.WriteLine();
    }

    /// <summary>
    /// Isolated points, points on and just inside the two-pixel margin, and runs of adjacent points.
    /// The margin cases matter because the original's guard is <c>x &lt; 2 || x &gt;= W - 2</c>, which is
    /// also what makes writing the 3x3 block unchecked safe — an off-by-one there would corrupt memory in
    /// the original and silently clip in ours.
    /// </summary>
    private static IEnumerable<((int X, int Y)[] Points, string Name)> Cases()
    {
        yield return ([(20, 20)], "isolated centre");
        yield return ([(2, 2)], "top-left corner in");
        yield return ([(1, 20)], "left margin (no-op)");
        yield return ([(20, 1)], "top margin (no-op)");
        yield return ([(W - 3, 20)], "right edge in");
        yield return ([(W - 2, 20)], "right margin (no-op)");
        yield return ([(20, H - 3)], "bottom edge in");
        yield return ([(20, H - 2)], "bottom margin (no-op)");
        yield return ([(W - 3, H - 3)], "bottom-right corner in");

        yield return ([(20, 20), (20, 20)], "same point twice");
        yield return ([(20, 20), (21, 20)], "adjacent pair");

        yield return ([.. Enumerable.Range(0, 16).Select(i => (10 + i, 20))], "horizontal run");
        yield return ([.. Enumerable.Range(0, 16).Select(i => (20, 10 + i))], "vertical run");
        yield return ([.. Enumerable.Range(0, 16).Select(i => (10 + i, 10 + i))], "diagonal run");
        yield return ([.. Enumerable.Range(0, 16).Select(i => (2 + i, 2))], "run along the margin");
    }

    /// <summary>
    /// A gradient with all three channels varying on different periods, so no two of the nine pixels the
    /// blur touches share a value.
    /// </summary>
    private static void Paint(int[] pixels)
    {
        for (var y = 0; y < H; y++)
        for (var x = 0; x < W; x++)
            pixels[y * W + x] = ((x * 7 + 13) & 0xFF) << 16 | ((y * 11 + 29) & 0xFF) << 8 | ((x * 5 + y * 3) & 0xFF);
    }

    /// <summary>
    /// RGB only: <c>FUN_18000b650</c> reassembles a pixel with no alpha bits, and the real colour field
    /// carries none — the original renders to BGRX where the top byte is ignored.
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
