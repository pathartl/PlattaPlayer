using PlattaPlayer.Visualizations.Wmp.Alchemy;
using PlattaPlayer.Visualizations.Wmp.Framebuffer;
using PlattaPlayer.Wmp.Harness.Interop;

namespace PlattaPlayer.Wmp.Harness.Verbs;

/// <summary>
/// Drives the REAL clipped line (<c>FUN_18000b0a0</c>, which tail-calls the Bresenham walk
/// <c>FUN_18000b270</c>) and diffs it against <see cref="DrawPrimitives.Line"/> pixel for pixel.
///
/// This is the layer directly under the spline, and it is the one piece of the stroke path that was still
/// a reimplementation rather than a port — which makes it the prime suspect for <c>verify-spline</c>'s
/// residual disagreement, where ours lays down about 3% more pixels than the original.
///
/// Two properties of the test matter more than the count of cases. Lines are drawn over a NON-BLACK
/// background, because the faint modes blend with what is already there and a black field hides the
/// difference between "wrote nothing" and "wrote something dim". And the matrix includes segments with
/// one or both endpoints outside the clip box: clipping moves where the walk STARTS, so a clipped line's
/// interior pixels are not a subset of the unclipped line's, and a clipper that is merely approximately
/// right shifts the whole run by a pixel.
///
/// Usage: verify-line [--verbose]
/// </summary>
internal static unsafe class VerifyLine
{
    private const int W = 96;
    private const int H = 96;

    /// <summary>Background the strokes are drawn over, so blends and no-ops are distinguishable.</summary>
    private const uint Background = 0x00203040;

    private const uint Colour = 0x00FF8040;

    /// <summary>
    /// <c>CTLineRender+0x24</c>, a divisor the walk uses for its per-line colour bookkeeping
    /// (<c>+0x1c = major / +0x24</c>). Nothing we call sets it, and a zero would fault inside the DLL, so
    /// the oracle checks it before drawing.
    /// </summary>
    private const int LineDivisor = 0x88 + 0x24;

    private static readonly (int X0, int Y0, int X1, int Y1, string Name)[] Cases =
    [
        // The eight octants, plus the axis-aligned and exactly-diagonal boundary cases.
        (20, 48, 76, 48, "horizontal +"),
        (76, 48, 20, 48, "horizontal -"),
        (48, 20, 48, 76, "vertical +"),
        (48, 76, 48, 20, "vertical -"),
        (20, 20, 76, 76, "diagonal 45"),
        (76, 20, 20, 76, "diagonal -45"),
        (20, 76, 76, 20, "diagonal up"),
        (76, 76, 20, 20, "diagonal up rev"),
        (20, 40, 76, 55, "shallow +x"),
        (76, 55, 20, 40, "shallow -x"),
        (40, 20, 55, 76, "steep +y"),
        (55, 76, 40, 20, "steep -y"),
        (20, 30, 76, 74, "octant 1"),
        (30, 20, 74, 76, "octant 2"),

        // Degenerate: the original draws NOTHING for a zero-length segment, and exactly one pixel for a
        // single-step one (the start, never the end).
        (48, 48, 48, 48, "zero length"),
        (48, 48, 49, 48, "one step x"),
        (48, 48, 48, 49, "one step y"),
        (48, 48, 49, 49, "one step xy"),

        // Clipped: one endpoint out, both out but crossing, both out and missing entirely.
        (-40, 48, 60, 48, "clip left"),
        (140, 48, 40, 48, "clip right"),
        (48, -40, 48, 60, "clip top"),
        (48, 140, 48, 40, "clip bottom"),
        (-30, -30, 130, 130, "clip both diagonal"),
        (-30, 130, 130, -30, "clip both anti"),
        (-50, 10, 50, -60, "clip corner miss"),
        (-10, 48, -5, 48, "wholly left"),
        (48, 200, 60, 240, "wholly below"),
        (-20, 40, 120, 52, "clip shallow both"),
        (40, -20, 52, 120, "clip steep both"),
    ];

    public static int Run(string[] args)
    {
        var verbose = args.Any(a => string.Equals(a, "--verbose", StringComparison.OrdinalIgnoreCase));

        Console.WriteLine(MpvisModule.Describe());
        Console.WriteLine($"real FUN_18000b0a0 vs DrawPrimitives.Line — {W}x{H}, background #{Background:X6}, " +
                          $"colour #{Colour:X6}, palette pinned");
        Console.WriteLine();

        using var oracle = new SplineOracle(W, H) { Colour = Colour, NeighbourAlpha = 0.5f };
        if (oracle.Peek(LineDivisor) == 0)
        {
            Console.WriteLine("  note: CTLineRender+0x24 came out zero; setting it to 1 so the walk cannot fault");
            oracle.Poke(LineDivisor, 1);
        }

        var ours = new PixelBuffer(W, H);
        var draw = new DrawPrimitives { NeighbourAlpha = 0.5f };

        var checks = 0;
        var failures = 0;

        foreach (var margin in (int[])[0, 1, 9])
        foreach (var mode in (int[])[0, 1])
        {
            Console.WriteLine($"  margin {margin}, plot mode {mode}");
            oracle.ClipMargin = margin;
            oracle.PlotMode = mode;
            draw.ClipMargin = margin;

            foreach (var (x0, y0, x1, y1, name) in Cases)
            {
                checks++;
                oracle.Fill(Background);
                oracle.Line(x0, y0, x1, y1);

                ours.Fill(unchecked((int)Background));
                draw.Line(ours, x0, y0, x1, y1, unchecked((int)Colour), mode);

                var (diff, first) = Compare(oracle.Pixels, ours.Pixels);
                var litReal = CountChanged(oracle.Pixels);
                var litOurs = CountChanged(ours.Pixels);

                if (diff == 0)
                {
                    if (verbose)
                        Console.WriteLine($"      {name,-20} ok    touched {litReal,4}");
                    continue;
                }

                failures++;
                Console.WriteLine($"      {name,-20} DIFF  {diff,5} px   touched real {litReal,4} ours {litOurs,4}");
                if (first >= 0)
                    Console.WriteLine($"          first ({first % W},{first / W}): " +
                                      $"real #{oracle.Pixels[first] & 0xFFFFFF:X6} ours #{ours.Pixels[first] & 0xFFFFFF:X6}");
            }
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? $"verify-line: {checks}/{checks} cases exact — our line draws the same pixels as the original's."
            : $"verify-line: {failures} of {checks} cases DIVERGE.");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Compares RGB only. <c>FUN_18000b650</c> reassembles a pixel as <c>(r&lt;&lt;16)|(g&lt;&lt;8)|b</c>
    /// with no alpha bits at all, and the real colour field carries none either — the original works on
    /// BGRX surfaces where the top byte is ignored. Masking keeps that from reading as a failure.
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

    private static int CountChanged(int[] pixels)
    {
        var n = 0;
        foreach (var p in pixels) if ((p & 0xFFFFFF) != Background) n++;
        return n;
    }
}
