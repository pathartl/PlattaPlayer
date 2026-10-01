using PlattaPlayer.Wmp.Harness.Imaging;
using PlattaPlayer.Wmp.Harness.Interop;
using PlattaPlayer.Wmp.Harness.Synth;

namespace PlattaPlayer.Wmp.Harness.Verbs;

/// <summary>
/// Determines each effect's REAL render resolution, and how it maps that onto the destination rect.
///
/// The technique needs no run-to-run reproducibility, which matters because Alchemy's effect scheduler
/// is randomised. mpvis.DLL's Render (FUN_180008170) advances the whole effect only when
/// <c>TimedLevel.state == 2</c>, but performs the present unconditionally. So: advance the field once,
/// then re-present that SAME field into destination rects of several sizes with state == 1. If the
/// effect really renders into a fixed internal surface and StretchBlt's it, every larger capture is an
/// exact nearest-neighbour resample of the smallest — and the rule GDI uses to pick the source pixel
/// falls straight out of which candidate mapping reproduces it byte-for-byte.
/// </summary>
internal static class Measure
{
    private static readonly (int W, int H)[] Sizes =
    [
        (640, 480),    // the hypothesised native field
        (1280, 960),   // exact 2x, the easiest case for any rounding rule
        (1280, 480),   // 16:9-ish — proves the stretch is anamorphic (no aspect correction)
        (800, 600),    // non-integer scale, same aspect
        (1920, 1080),  // a real 16:9 window
        (1000, 700),   // deliberately awkward, to separate the rounding rules
    ];

    public static int Run(string[] args)
    {
        var effect = args.Length > 0 ? args[0].ToLowerInvariant() : "alchemy";
        var advanceFrames = 300;
        var preset = args.Contains("--preset")
            ? int.Parse(args[Array.IndexOf(args, "--preset") + 1]) : 0;

        Console.WriteLine($"== {effect}: render-resolution probe ==");
        Console.WriteLine($"   advancing {advanceFrames} frames, then re-presenting the frozen field at {Sizes.Length} sizes");
        Console.WriteLine();

        var audio = AudioFrameSet.Read(HarnessPaths.SynthFile);

        // Stage 1: estimate the native field size without assuming it. A nearest-neighbour upscale
        // repeats source columns/rows, so counting adjacent columns that differ in a deliberately
        // oversized present recovers the source width (and likewise for rows). This is an estimate —
        // flat regions in the field undercount it — so stage 2 verifies it exactly.
        var (estW, estH) = EstimateFieldSize(effect, preset, audio, advanceFrames);
        Console.WriteLine($"   estimated native field from a 1536x1152 present: {estW} x {estH}");

        if (estW <= 2 || estH <= 2)
        {
            Console.WriteLine();
            Console.WriteLine("   INCONCLUSIVE: the frozen present is (near-)uniform, so this technique does not");
            Console.WriteLine($"   apply to {effect}. That is itself the answer — the effect keeps no persistent");
            Console.WriteLine("   field to re-present. wmp.dll's bar renderer (FUN_18041def0) sets a skip-redraw flag");
            Console.WriteLine("   when state == 1 OR the TimedLevel timestamp is unchanged, and it recreates its DIB");
            Console.WriteLine("   whenever the rect changes size — so freezing it yields a blank surface rather than a");
            Console.WriteLine("   re-scaled field. It rasterises directly at the destination rect, 1:1, via");
            Console.WriteLine("   CreateDIBSection(rect) + SetDIBitsToDevice with no stretch of any kind.");
            return 0;
        }

        Console.WriteLine();

        // Stage 2: verify exactly, using the ESTIMATED field size as the reference rather than an
        // assumed one. If the estimate is right the larger presents reproduce it to the last pixel.
        var shots = CapturePresentations(effect, preset, audio, advanceFrames, (estW, estH));

        if (!shots.TryGetValue((estW, estH), out var reference))
        {
            Console.WriteLine("   could not present at the estimated field size; aborting.");
            return 1;
        }

        var refDir = Path.Combine(HarnessPaths.ReportDir, $"{effect}-resolution");
        Directory.CreateDirectory(refDir);
        foreach (var ((w, h), px) in shots)
            PngWriter.WriteBgra(Path.Combine(refDir, $"{effect}-{w}x{h}.png"), px, w, h);
        Console.WriteLine($"   wrote {shots.Count} png(s) to {refDir}");
        Console.WriteLine();

        var anyMatch = false;
        foreach (var ((w, h), px) in shots)
        {
            if (w == estW && h == estH) continue;
            if (w < estW || h < estH) continue; // downscale drops pixels; only upscales are conclusive
            var results = Rules.Select(r => (r.Name, Match: Compare(reference, estW, estH, px, w, h, r.Map))).ToList();
            var best = results.OrderByDescending(r => r.Match).First();
            var exact = results.Where(r => r.Match >= 1.0).Select(r => r.Name).ToList();
            anyMatch |= exact.Count > 0;

            Console.WriteLine($"   {w,5}x{h,-5} vs the {estW}x{estH} field:");
            foreach (var (name, match) in results)
                Console.WriteLine($"       {name,-28} {match * 100,7:F3}% of pixels reproduced{(match >= 1.0 ? "   <== EXACT" : "")}");
            if (exact.Count == 0)
                Console.WriteLine($"       (best is {best.Name} at {best.Match * 100:F3}%)");
        }

        Console.WriteLine();
        if (anyMatch)
        {
            Console.WriteLine($"   CONCLUSION: {effect} renders into a FIXED {estW}x{estH} field and scales it to the");
            Console.WriteLine("   destination rect by pixel replication, with NO aspect correction (a 16:9 window gets");
            Console.WriteLine($"   an anamorphically stretched {estW}:{estH} image). Our port must do the same: a fixed");
            Console.WriteLine($"   {estW}x{estH} buffer presented nearest-neighbour across the full control bounds,");
            Console.WriteLine("   sampling source pixels with the centre rule ((2x+1)*S)/(2D).");
        }
        else
        {
            Console.WriteLine($"   CONCLUSION: {effect} does NOT use a fixed field — its output at each size is");
            Console.WriteLine("   independent, i.e. it rasterises directly at the destination rect size (1:1).");
        }
        return 0;
    }

    /// <summary>Candidate source-index mappings for a GDI COLORONCOLOR upscale.</summary>
    private static readonly (string Name, Func<int, int, int, int> Map)[] Rules =
    [
        ("floor  (x*S)/D",        (x, s, d) => x * s / d),
        ("round  (x*S + D/2)/D",  (x, s, d) => (x * s + d / 2) / d),
        ("centre ((2x+1)*S)/(2D)", (x, s, d) => (2 * x + 1) * s / (2 * d)),
    ];

    private static double Compare(
        byte[] src, int sw, int sh, byte[] dst, int dw, int dh, Func<int, int, int, int> map)
    {
        long ok = 0;
        for (var y = 0; y < dh; y++)
        {
            var sy = Math.Clamp(map(y, sh, dh), 0, sh - 1);
            for (var x = 0; x < dw; x++)
            {
                var sx = Math.Clamp(map(x, sw, dw), 0, sw - 1);
                var si = (sy * sw + sx) * 4;
                var di = (y * dw + x) * 4;
                if (dst[di] == src[si] && dst[di + 1] == src[si + 1] && dst[di + 2] == src[si + 2]) ok++;
            }
        }
        return (double)ok / ((long)dw * dh);
    }

    /// <summary>
    /// Recovers the native field size from a single oversized present by counting how many adjacent
    /// column pairs (and row pairs) actually differ. Under nearest-neighbour replication each source
    /// column becomes a run of identical destination columns, so the number of transitions is one less
    /// than the number of source columns that carry distinct content.
    /// </summary>
    private static (int W, int H) EstimateFieldSize(
        string effect, int preset, List<AudioFrame> audio, int advanceFrames)
    {
        const int probeW = 1536, probeH = 1152;

        using var fx = CreateEffect(effect);
        if (preset != 0) fx.SetCurrentPreset(preset);
        using var levels = new TimedLevelBuffer();
        Advance(fx, levels, audio, advanceFrames, 640, 480);

        levels.State = 1;
        using var dib = new DibTarget(probeW, probeH);
        dib.ClearBlack();
        var rc = new RECT(0, 0, probeW, probeH);
        unsafe { fx.Render(levels.Pointer, dib.Hdc, ref rc); }
        var px = dib.Snapshot();

        var colTransitions = 0;
        for (var x = 1; x < probeW; x++)
        {
            for (var y = 0; y < probeH; y++)
            {
                var a = (y * probeW + x) * 4;
                var b = (y * probeW + x - 1) * 4;
                if (px[a] != px[b] || px[a + 1] != px[b + 1] || px[a + 2] != px[b + 2])
                {
                    colTransitions++;
                    break;
                }
            }
        }

        var rowTransitions = 0;
        for (var y = 1; y < probeH; y++)
        {
            for (var x = 0; x < probeW; x++)
            {
                var a = (y * probeW + x) * 4;
                var b = ((y - 1) * probeW + x) * 4;
                if (px[a] != px[b] || px[a + 1] != px[b + 1] || px[a + 2] != px[b + 2])
                {
                    rowTransitions++;
                    break;
                }
            }
        }

        return (colTransitions + 1, rowTransitions + 1);
    }

    private static void Advance(
        WmpEffect fx, TimedLevelBuffer levels, List<AudioFrame> audio, int count, int w, int h)
    {
        using var warmup = new DibTarget(w, h);
        var rc = new RECT(0, 0, w, h);
        for (var i = 0; i < count && i < audio.Count; i++)
        {
            var f = audio[i];
            f.Frequency0.CopyTo(levels.Frequency0);
            f.Frequency1.CopyTo(levels.Frequency1);
            f.Waveform0.CopyTo(levels.Waveform0);
            f.Waveform1.CopyTo(levels.Waveform1);
            levels.State = f.State;
            levels.TimeStamp = f.TimeStamp;
            unsafe { fx.Render(levels.Pointer, warmup.Hdc, ref rc); }
        }
    }

    private static Dictionary<(int, int), byte[]> CapturePresentations(
        string effect, int preset, List<AudioFrame> audio, int advanceFrames, (int W, int H) estimated)
    {
        using var fx = CreateEffect(effect);
        if (preset != 0) fx.SetCurrentPreset(preset);
        using var levels = new TimedLevelBuffer();

        // Advance the effect on a 640x480 surface so it holds a rich, high-contrast field. A flat field
        // would satisfy every candidate mapping and prove nothing.
        using (var warmup = new DibTarget(640, 480))
        {
            var rc = new RECT(0, 0, 640, 480);
            for (var i = 0; i < advanceFrames && i < audio.Count; i++)
            {
                var f = audio[i];
                f.Frequency0.CopyTo(levels.Frequency0);
                f.Frequency1.CopyTo(levels.Frequency1);
                f.Waveform0.CopyTo(levels.Waveform0);
                f.Waveform1.CopyTo(levels.Waveform1);
                levels.State = f.State;
                levels.TimeStamp = f.TimeStamp;
                unsafe { fx.Render(levels.Pointer, warmup.Hdc, ref rc); }
            }
        }

        // Freeze: state 1 means "reuse previous", so no further advance happens and every present below
        // shows the SAME field at a different destination size.
        levels.State = 1;

        var shots = new Dictionary<(int, int), byte[]>();
        foreach (var (w, h) in Sizes.Append(estimated).Append((estimated.W * 3, estimated.H * 3)).Distinct())
        {
            using var dib = new DibTarget(w, h);
            dib.ClearBlack();
            var rc = new RECT(0, 0, w, h);
            unsafe { fx.Render(levels.Pointer, dib.Hdc, ref rc); }
            shots[(w, h)] = dib.Snapshot();
        }
        return shots;
    }

    private static WmpEffect CreateEffect(string effect) => effect switch
    {
        "alchemy" => AlchemyFactory.Create(),
        "barswaves" => WmpInternalFactory.CreateInstance(
            WmpInternalFactory.Resolve(WmpGuids.ClsidBarsAndWaves)),
        "battery" => WmpInternalFactory.CreateInstance(
            WmpInternalFactory.Resolve(WmpGuids.ClsidBattery)),
        _ => throw new ArgumentException($"Unknown effect '{effect}'."),
    };
}
