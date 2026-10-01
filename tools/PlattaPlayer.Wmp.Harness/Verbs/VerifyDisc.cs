using PlattaPlayer.Visualizations.Wmp.Alchemy;
using PlattaPlayer.Visualizations.Wmp.Framebuffer;
using PlattaPlayer.Wmp.Harness.Interop;

namespace PlattaPlayer.Wmp.Harness.Verbs;

/// <summary>
/// Drives the REAL soft disc (<c>FUN_18000fbc4</c>) and diffs it against
/// <see cref="DrawPrimitives.Disc"/> pixel for pixel.
///
/// This one had never been checked in any form. The atom balls are nothing but two of these, so whatever
/// the disc does IS the effect — and the version it replaces shared not one term with the original.
///
/// Discs are drawn over a gradient for the same reason plots are: the falloff is a BLEND, so over a flat
/// field a wrong weight and a right one can land on the same colour.
///
/// Usage: verify-disc [--verbose] [--characterise]
/// </summary>
internal static unsafe class VerifyDisc
{
    private const int W = 96;
    private const int H = 96;

    private const uint Fill = 0x00FF8040;

    private const uint Rim = 0x0020C0FF;

    public static int Run(string[] args)
    {
        var verbose = args.Any(a => string.Equals(a, "--verbose", StringComparison.OrdinalIgnoreCase));
        var characterise = args.Any(a => string.Equals(a, "--characterise", StringComparison.OrdinalIgnoreCase));

        Console.WriteLine(MpvisModule.Describe());

        using var oracle = new DiscOracle(W, H) { Fill = Fill, Rim = Rim };

        if (characterise)
        {
            Characterise(oracle);
            return 0;
        }

        Console.WriteLine($"real FUN_18000fbc4 vs DrawPrimitives.Disc — {W}x{H} over a gradient, " +
                          $"fill #{Fill:X6} rim #{Rim:X6}");
        Console.WriteLine();

        var ours = new PixelBuffer(W, H);

        var checks = 0;
        var failures = 0;

        foreach (var invert in (bool[])[false, true])
        foreach (var alpha in (float[])[0f, 0.1f, 0.4f, 0.75f, 1f])
        foreach (var (cx, cy, r, name) in Cases())
        {
            checks++;
            oracle.Invert = invert;
            Paint(oracle.Pixels);
            oracle.Draw(cx, cy, r, alpha);

            Paint(ours.Pixels);
            DrawPrimitives.Disc(ours, cx, cy, r, alpha,
                                unchecked((int)Fill), unchecked((int)Rim), invert);

            var (diff, first) = Compare(oracle.Pixels, ours.Pixels);
            if (diff == 0)
            {
                if (verbose) Console.WriteLine($"  invert {invert,-5} alpha {alpha:0.00}  {name,-22} ok");
                continue;
            }

            failures++;
            Console.WriteLine($"  invert {invert,-5} alpha {alpha:0.00}  {name,-22} DIFF {diff,5} px");
            if (first >= 0)
                Console.WriteLine($"      first ({first % W},{first / W}): " +
                                  $"real #{oracle.Pixels[first] & 0xFFFFFF:X6} ours #{ours.Pixels[first] & 0xFFFFFF:X6}");
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? $"verify-disc: {checks}/{checks} cases exact — core falloff, rim band and invert all match."
            : $"verify-disc: {failures} of {checks} cases DIVERGE.");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Radii from degenerate to large, and centres placed so the square scan runs off each edge and each
    /// corner — the clip is an UNSIGNED compare against the object's own width and height, which is a
    /// different test from the surface bounds and is easy to get subtly wrong.
    /// </summary>
    private static IEnumerable<(int Cx, int Cy, int R, string Name)> Cases()
    {
        yield return (48, 48, 1, "r=1 centre");
        yield return (48, 48, 2, "r=2 centre");
        yield return (48, 48, 5, "r=5 centre");
        yield return (48, 48, 9, "r=9 centre");
        yield return (48, 48, 16, "r=16 centre");
        yield return (48, 48, 30, "r=30 centre");
        yield return (48, 48, 45, "r=45 fills frame");
        yield return (48, 48, 80, "r=80 overruns");
        yield return (0, 48, 20, "clipped left");
        yield return (W - 1, 48, 20, "clipped right");
        yield return (48, 0, 20, "clipped top");
        yield return (48, H - 1, 20, "clipped bottom");
        yield return (0, 0, 25, "clipped corner");
        yield return (-15, -15, 25, "centre off-surface");
        yield return (W + 10, H + 10, 25, "centre past far corner");
    }

    /// <summary>
    /// Dumps the real disc's profile along its horizontal centre line, in the terms the function actually
    /// works in. Reading this is how the shape was understood rather than guessed: <c>d2</c> is not a
    /// normalised radius, and the two passes overlap on a band that straddles the rim.
    /// </summary>
    private static void Characterise(DiscOracle oracle)
    {
        const int r = 16;
        const int cx = 48;
        const int cy = 48;
        const float alpha = 1f;

        oracle.Invert = false;
        Array.Fill(oracle.Pixels, 0);
        oracle.Draw(cx, cy, r, alpha);

        Console.WriteLine($"real FUN_18000fbc4 profile: r={r}, alpha={alpha}, fill #{Fill:X6}, rim #{Rim:X6}, " +
                          "drawn over black");
        Console.WriteLine();
        Console.WriteLine("    dx     d2      core?  rim?   pixel");
        for (var dx = -r - 2; dx <= r + 2; dx++)
        {
            var d2 = ((double)(r * r) - dx * dx) / r;
            var px = oracle.Pixels[cy * W + cx + dx] & 0xFFFFFF;
            Console.WriteLine($"  {dx,4}  {d2,8:0.000}   {(d2 >= 1.0 ? "yes" : " no")}    " +
                              $"{(d2 > -MpvisMath.DiscRimLimit && d2 < MpvisMath.DiscRimLimit ? "yes" : " no")}   #{px:X6}");
        }
    }

    private static void Paint(int[] pixels)
    {
        for (var y = 0; y < H; y++)
        for (var x = 0; x < W; x++)
            pixels[y * W + x] = ((x * 3 + 17) & 0xFF) << 16 | ((y * 5 + 41) & 0xFF) << 8 | ((x * 2 + y * 7) & 0xFF);
    }

    /// <summary>RGB only — the original renders to BGRX and never writes the top byte.</summary>
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
