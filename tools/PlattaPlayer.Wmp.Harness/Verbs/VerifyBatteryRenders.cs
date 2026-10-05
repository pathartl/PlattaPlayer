using System.Runtime.InteropServices;
using PlattaPlayer.Visualizations.Wmp.Battery;
using PlattaPlayer.Visualizations.Wmp.Battery.Effects;
using PlattaPlayer.Wmp.Harness.Interop;

namespace PlattaPlayer.Wmp.Harness.Verbs;

/// <summary>
/// Diffs Battery's 10 render effects against the real objects in wmp.dll. The real objects come from a
/// real CRenderData pool. Ours come from <see cref="RenderEffect.CreatePool"/>, built from the same
/// construction script, so CJDar and CGalaxy start from identical constructor draws.
///
/// For every script set: Randomize (draw count and all eight parameters, bit for bit), then a run of
/// frames. Each frame gets a fresh synthetic TimedLevel and both sides render into persistent fields that
/// started as the same random image. Each frame compares the WHOLE field and the per-frame draw count.
/// Effects keep internal state (phases, particle rings, balls), so a frame-by-frame match over a run
/// also proves that state evolves identically.
///
/// Usage: verify-battery-renders [--sets N] [--frames N] [--seed N] [--class CName] [--managed-math]
/// </summary>
internal static unsafe class VerifyBatteryRenders
{
    public static int Run(string[] args)
    {
        var sets = VerifyWarps.ArgInt(args, "--sets", 4);
        var frames = VerifyWarps.ArgInt(args, "--frames", 120);
        var seed = VerifyWarps.ArgInt(args, "--seed", 1000);
        var only = VerifyWarps.ArgString(args, "--class");
        const int w = 384, h = 288;

        Console.WriteLine(WmpModule.Describe());
        RandRedirect.ApplyWmp();
        if (args.Contains("--managed-math")) MathRedirect.ApplyWmp();

        // Construct both pools from one script. The real constructor runs the shift pool too, which draws
        // nothing, then CJDar (6) and CGalaxy (2).
        var ctorScript = RandRedirect.MakeScript(seed - 1, 4096);
        RandRedirect.SetScript(ctorScript);
        _ = BatteryOracle.RenderData;
        var ctorDraws = RandRedirect.Position;
        var ctorRand = new ScriptedCrtRand(ctorScript);
        var ours = RenderEffect.CreatePool(ctorRand);
        if (ctorDraws != ctorRand.Position)
        {
            Console.WriteLine($"  FAIL construction: real drew {ctorDraws}, ours {ctorRand.Position}");
            return 1;
        }
        Console.WriteLine($"  ok   construction: {ctorDraws} draws");

        var tlBytes = new byte[0x1010];
        var tlPin = GCHandle.Alloc(tlBytes, GCHandleType.Pinned);
        var realBits = new byte[w * h];
        var realPin = GCHandle.Alloc(realBits, GCHandleType.Pinned);
        var surf = (nint)NativeMemory.AllocZeroed(0x28);
        *(int*)(surf + 0x08) = w;
        *(int*)(surf + 0x0c) = h;
        *(nint*)(surf + 0x20) = realPin.AddrOfPinnedObject();
        var rd = (nint)NativeMemory.AllocZeroed(0x100);
        *(int*)(rd + 0x5c) = w;
        *(int*)(rd + 0x60) = h;
        *(nint*)(rd + 0xb8) = surf;

        var ourRd = new BatteryRenderData(new CrtRand(), w, h);
        var levels = new BatteryLevels();

        var failures = 0;
        for (var e = 0; e < BatteryOracle.RenderPool.Length; e++)
        {
            var real = BatteryOracle.Render(e);
            var mine = ours[e];
            if (only is not null && !string.Equals(only, real.Name, StringComparison.OrdinalIgnoreCase)) continue;
            if (mine.Name != real.Name) throw new InvalidOperationException($"pool {e}: ours {mine.Name} vs {real.Name}");

            var ok = real.Flags == mine.Flags;
            if (!ok) Console.WriteLine($"  {real.Name}: flags real {real.Flags} ours {mine.Flags}");

            var bg = new Random(seed + e);
            bg.NextBytes(realBits);
            realBits.CopyTo(ourRd.Front.Bits, 0);

            long written = 0;
            var prev = (byte[])realBits.Clone();
            // Pass 0 renders BEFORE any Randomize, from constructor state. A preset loads parameters
            // with SetParams and never calls Randomize, so constructor fields (CJDar's and CGalaxy's ball
            // velocities, phases, the DotPlane camera) are live there.
            for (var set = -1; set < sets && ok; set++)
            {
                var script = RandRedirect.MakeScript(seed + 7919 * set + 31 * e, 1 << 16);
                var rand = new ScriptedCrtRand(script);
                RandRedirect.SetScript(script);
                if (set >= 0)
                {
                    real.Randomize();
                    mine.Randomize(rand);
                }
                else
                {
                    // Give the fresh objects plausible parameters through SetParams, as a preset would.
                    var preset = PresetParams(real.Name);
                    real.SetParams(preset);
                    mine.SetParams(preset);
                }
                if (RandRedirect.Position != rand.Position)
                {
                    Console.WriteLine($"  {real.Name} set {set}: Randomize drew {RandRedirect.Position}, ours {rand.Position}");
                    ok = false;
                    break;
                }
                for (var p = 0; p < 8 && ok; p++)
                {
                    if (BitConverter.DoubleToInt64Bits(real.Param(p)) == BitConverter.DoubleToInt64Bits(mine.P[p])) continue;
                    Console.WriteLine($"  {real.Name} set {set}: dbl{p + 1} real {real.Param(p):R} ours {mine.P[p]:R}");
                    ok = false;
                }

                var audio = new Random(seed * 3 + set * 101 + e);
                for (var f = 0; f < frames && ok; f++)
                {
                    FillLevels(audio, tlBytes, f);
                    levels.Bytes.AsSpan().Clear();
                    tlBytes.AsSpan(0, 0x1000).CopyTo(levels.Bytes);
                    levels.State = BitConverter.ToInt32(tlBytes, 0x1000);

                    var before = RandRedirect.Position;
                    var ourBefore = rand.Position;
                    real.Render(tlPin.AddrOfPinnedObject(), rd);
                    mine.Render(levels, ourRd, rand);
                    if (RandRedirect.Position - before != rand.Position - ourBefore)
                    {
                        Console.WriteLine($"  {real.Name} set {set} frame {f}: drew {RandRedirect.Position - before}, ours {rand.Position - ourBefore}");
                        ok = false;
                        break;
                    }
                    if (mine is DotPlane dp && !MatrixMatches(real, dp, set, f)) { ok = false; break; }
                    for (var i = 0; i < realBits.Length; i++)
                    {
                        if (realBits[i] != prev[i]) { written++; prev[i] = realBits[i]; }
                        if (realBits[i] == ourRd.Front.Bits[i]) continue;
                        Console.WriteLine($"  {real.Name} set {set} frame {f}: pixel ({i % w},{i / w}) real {realBits[i]} ours {ourRd.Front.Bits[i]}");
                        Console.WriteLine($"    params {string.Join(" ", Enumerable.Range(0, 8).Select(k => mine.P[k].ToString("R")))}");
                        ok = false;
                        break;
                    }
                }
            }

            // Coverage: an effect that drew nothing would pass trivially.
            Console.WriteLine($"{(ok ? "  ok  " : "  FAIL")} {real.Name,-16} {written,9} pixel writes changed the field");
            if (!ok) failures++;
        }

        RandRedirect.Restore();
        Console.WriteLine(failures == 0 ? "verify-battery-renders: ALL EXACT" : $"verify-battery-renders: {failures} effect(s) diverge");
        return failures == 0 ? 0 : 1;
    }

    private static bool MatrixMatches(BatteryRenderObject real, DotPlane mine, int set, int frame)
    {
        var m = mine.CombinedMatrix;
        for (var k = 0; k < 16; k++)
        {
            var r = *(float*)(real.Pointer + 0xc67c + 4 * k);
            if (BitConverter.SingleToInt32Bits(r) == BitConverter.SingleToInt32Bits(m[k])) continue;
            Console.WriteLine($"  CDotPlane set {set} frame {frame}: combined[{k}] real {r:R} ours {m[k]:R}");
            return false;
        }
        return true;
    }

    /// <summary>Preset-style parameters (taken from the shipped registry recipes where the class is used).</summary>
    private static double[] PresetParams(string name) => name switch
    {
        "CEdgeTrace" => [50, 0, 0, 0, 0, 0, 0, 0],
        "CCosEdgeGradiant" => [0.05, 0, 0, 0, 0, 0, 0, 0],
        "CWaveEdge" => [1, 0, 0, 0, 0, 0, 0, 0],
        "CSpectrumEdge" => [3, 12, 9, 0, 0, 0, 0, 0],
        "CCircleWaveform" => [1, 2, 0.4, 0, 0, 0, 0, 0],
        "CDotPlane" => [37, 3, 0, 384.000015258789, 0, 0, 0, 0],
        "CJDar" => [50, 2, 0, 16, 1.0, 1, 50, 0],
        "CGalaxy" => [3, 20, 0.07, 230, 0, 1, 3e-7, 0.15],
        "CJiggyScribble" => [40, 150, 700, 7, 0.2, 0, 9, 0],
        _ => new double[8],
    };

    /// <summary>
    /// A synthetic TimedLevel. Most frames are random bytes with a falling spectrum and a centred
    /// waveform, like real audio. Every 17th frame is silent, which takes CJDar's <c>sum == 0</c> early
    /// exit. Every 29th frame is "paused" (state 1), which freezes CDotPlane's particles.
    /// </summary>
    private static void FillLevels(Random r, byte[] tl, int frame)
    {
        var silent = frame % 17 == 16;
        for (var c = 0; c < 2; c++)
        {
            for (var i = 0; i < 1024; i++)
            {
                tl[c * 0x400 + i] = silent ? (byte)0 : (byte)Math.Clamp(255 - i / 5 - r.Next(60), 0, 255);
                tl[0x800 + c * 0x400 + i] = silent ? (byte)128 : (byte)Math.Clamp(128 + r.Next(-100, 101), 0, 255);
            }
        }
        BitConverter.TryWriteBytes(tl.AsSpan(0x1000), frame % 29 == 28 ? 1 : 2);
    }
}
