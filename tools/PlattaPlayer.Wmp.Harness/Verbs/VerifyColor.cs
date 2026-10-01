using PlattaPlayer.Visualizations.Wmp.Alchemy;
using PlattaPlayer.Visualizations.Wmp.Framebuffer;
using PlattaPlayer.Wmp.Harness.Interop;
using PlattaPlayer.Wmp.Harness.Synth;

namespace PlattaPlayer.Wmp.Harness.Verbs;

/// <summary>
/// Diffs our colour helpers against the real <c>CTColor</c> ones.
///
/// Three separate checks, cheapest first, because each isolates a different thing:
/// <list type="number">
/// <item>ROUND — <c>FUN_18000bef4</c> against ours, over a range of values. Every rounded quantity in
/// the effect library goes through it, and it rounds in FLOAT.</item>
/// <item>RANDOM COLOUR — <c>FUN_18000b9f8</c> against ours, driven by a shared rand() script. This
/// checks the channel ORDER, which no amount of looking at output would settle.</item>
/// <item>INTERPOLATE — <c>FUN_18000b518</c> against <see cref="PixelBuffer.LerpArgb"/> over a grid of
/// colour pairs and blend factors.</item>
/// <item>CYCLER — a live <c>CTColor</c> stepped alongside our <see cref="PaletteCycler"/>.</item>
/// </list>
///
/// Usage: verify-color [--samples N]
/// </summary>
internal static class VerifyColor
{
    public static int Run(string[] args)
    {
        var samples = ArgInt(args, "--samples", 4096);

        Console.WriteLine(MpvisModule.Describe());
        Console.WriteLine(RandRedirect.SelfTest());
        Console.WriteLine();

        var failures = 0;
        failures += CheckRound();
        failures += CheckRandomColor();
        failures += CheckInterpolate(samples);
        failures += CheckCycler();
        failures += CheckMultiStep();

        RandRedirect.Restore();

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "verify-color: our colour helpers are identical to the real ones."
            : $"verify-color: {failures} check(s) DIVERGE — see above.");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>The rounding helper, which the whole effect library funnels through.</summary>
    private static int CheckRound()
    {
        var bad = 0;
        string? first = null;
        for (var i = -20000; i <= 20000; i++)
        {
            var v = i / 100f;
            var real = ColorOracle.Round(v);
            var ours = PixelBuffer.MpvisRound(v);
            if (real == ours) continue;
            bad++;
            first ??= $"{v} -> real {real}, ours {ours}";
        }
        Console.WriteLine($"  round        {(bad == 0 ? "ok" : $"{bad} mismatches, first {first}")}");
        return bad == 0 ? 0 : 1;
    }

    /// <summary>
    /// Channel order. The real assembles <c>b3 | b2 &lt;&lt; 16 | b1 &lt;&lt; 8</c> from three draws, so
    /// the FIRST goes to green and the SECOND to red — the opposite of the obvious reading.
    /// </summary>
    private static int CheckRandomColor()
    {
        var script = RandRedirect.MakeScript(77, 3 * 64);
        RandRedirect.SetScript(script);
        RandRedirect.Rewind();

        var bad = 0;
        string? first = null;
        var scripted = new ScriptedRandom(script);
        for (var i = 0; i < 64; i++)
        {
            var real = ColorOracle.RandomColor() & 0xFFFFFFu;
            var ours = (uint)PaletteCycler.RandomColor(scripted) & 0xFFFFFFu;
            if (real == ours) continue;
            bad++;
            first ??= $"real #{real:X6}, ours #{ours:X6}";
        }
        Console.WriteLine($"  randomColor  {(bad == 0 ? "ok" : $"{bad}/64 mismatches, first {first}")}");
        return bad == 0 ? 0 : 1;
    }

    private static int CheckInterpolate(int samples)
    {
        var random = new Random(9);
        var bad = 0;
        string? first = null;
        for (var i = 0; i < samples; i++)
        {
            var a = (uint)random.Next(0x1000000);
            var b = (uint)random.Next(0x1000000);
            var t = (float)random.NextDouble();

            var real = ColorOracle.Interpolate(a, b, t) & 0xFFFFFFu;
            var ours = (uint)PixelBuffer.LerpArgb(unchecked((int)(0xFF000000u | a)),
                                                 unchecked((int)(0xFF000000u | b)), t) & 0xFFFFFFu;
            if (real == ours) continue;
            bad++;
            first ??= $"#{a:X6} -> #{b:X6} @ {t:0.####}: real #{real:X6}, ours #{ours:X6}";
        }
        Console.WriteLine($"  interpolate  {(bad == 0 ? "ok" : $"{bad}/{samples} mismatches, first {first}")}");
        return bad == 0 ? 0 : 1;
    }

    /// <summary>
    /// The cycler as a whole: a live CTColor and our PaletteCycler stepped side by side off one script.
    /// This is the check that catches period and transition-boundary differences, which drift slowly and
    /// are invisible in any single frame.
    /// </summary>
    private static int CheckCycler()
    {
        const int Frames = 600;
        var script = RandRedirect.MakeScript(31, 4096);
        RandRedirect.SetScript(script);
        RandRedirect.Rewind();

        using var real = new ColorCyclerOracle();
        var scripted = new ScriptedRandom(script);
        scripted.Rewind();
        var ours = new PaletteCycler(scripted);

        var bad = 0;
        string? first = null;
        for (var i = 0; i < Frames; i++)
        {
            real.Advance();
            ours.Advance(scripted);

            var r = real.Current & 0xFFFFFFu;
            var o = (uint)ours.Current & 0xFFFFFFu;
            if (r == o) continue;
            bad++;
            first ??= $"frame {i}: real #{r:X6} (from #{real.From:X6} to #{real.To:X6}, " +
                      $"{real.Counter}/{real.Period}), ours #{o:X6}";
        }
        Console.WriteLine($"  cycler       {(bad == 0 ? "ok" : $"{bad}/{Frames} mismatches, first {first}")}");
        return bad == 0 ? 0 : 1;
    }

    /// <summary>
    /// The MULTI-STEP transition, which is what every stroke actually runs on: the renderers start it with
    /// <c>FUN_18000bf70(colour, paletteB, paletteA, 1, steps)</c> and then advance it once per plotted
    /// pixel. This is the colour a stroke is painted in, so if it drifts dull here the whole effect looks
    /// washed out — which is exactly the symptom worth chasing.
    /// </summary>
    private static int CheckMultiStep()
    {
        const uint from = 0x00FF3010;
        const uint to = 0x0010C0FF;
        var bad = 0;
        string? first = null;
        var total = 0;

        foreach (var steps in new[] { 2, 3, 6, 8 })
        foreach (var duration in new[] { 1, 20, 90 })
        {
            // Both sides must draw the SAME randoms: a zero-length restart makes FUN_18000bf70 redraw its
            // duration, so the two implementations diverge immediately unless their streams are aligned.
            var script = RandRedirect.MakeScript(500 + steps * 10 + duration, 4096);
            RandRedirect.SetScript(script);
            RandRedirect.Rewind();

            using var real = new ColorCyclerOracle();
            real.Begin(from, to, duration, steps);

            var scripted = new ScriptedRandom(script);
            scripted.Rewind();
            var ours = new PaletteCycler(scripted);
            ours.Begin(unchecked((int)(0xFF000000u | from)), unchecked((int)(0xFF000000u | to)),
                       duration, steps, scripted);

            for (var i = 0; i < 120; i++)
            {
                real.Advance();
                ours.Advance(scripted);
                total++;
                var r = real.Current & 0xFFFFFF;
                var o = (uint)ours.Current & 0xFFFFFF;
                if (r == o) continue;
                bad++;
                first ??= $"steps={steps} duration={duration} advance {i}: real #{r:X6}, ours #{o:X6}";
            }
        }

        Console.WriteLine($"  multiStep    {(bad == 0 ? "ok" : $"{bad}/{total} mismatches, first {first}")}");
        return bad == 0 ? 0 : 1;
    }

    private static int ArgInt(string[] args, string name, int fallback)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(args[i + 1], out var v)) return v;
        return fallback;
    }
}
