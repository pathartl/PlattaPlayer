using PlattaPlayer.Visualizations.Wmp.Battery;
using PlattaPlayer.Wmp.Harness.Interop;

namespace PlattaPlayer.Wmp.Harness.Verbs;

/// <summary>
/// The end-to-end Battery oracle. It creates the REAL effect through its internal class factory, with
/// rand() scripted from before construction, and runs it alongside <see cref="BatteryCore"/> on the same
/// script and the same TimedLevels. Every frame it compares three things:
/// <list type="number">
/// <item>the 8-bit FIELD, read straight from the real object (CBattery+8 is the CRenderData; its front
/// surface is at +0xb8, and the surface's bits at +0x20);</item>
/// <item>the current PALETTE (CRenderData+0x4dc, 256 × PALETTEENTRY);</item>
/// <item>the PRESENTED pixels the real effect StretchBlts into a 384×288 DIB (1:1), against our field
/// mapped through our display palette. This is the only check that sees the DirectDraw palette upload,
/// including the entry-255 question.</item>
/// </list>
/// It also compares the rand() draw count every frame, which catches any control-flow divergence on the
/// frame it happens.
///
/// Each preset runs in a fresh real effect and a fresh BatteryCore. A run is play frames with a short
/// pause, then (with <c>--stop</c>) the full 300-frame stop fade and the solid fill after it.
///
/// Usage: verify-battery [--preset N | --presets a,b,c] [--frames N] [--seed N] [--stop] [--managed-math]
/// </summary>
internal static unsafe class VerifyBattery
{
    private const int W = BatteryCore.FieldWidth;
    private const int H = BatteryCore.FieldHeight;

    public static int Run(string[] args)
    {
        var frames = VerifyWarps.ArgInt(args, "--frames", 600);
        var seed = VerifyWarps.ArgInt(args, "--seed", 1000);
        var stop = args.Contains("--stop");
        var presets = ParsePresets(args);

        Console.WriteLine(WmpModule.Describe());
        RandRedirect.ApplyWmp();
        if (args.Contains("--managed-math")) MathRedirect.ApplyWmp();

        var failures = 0;
        foreach (var p in presets)
        {
            var result = RunPreset(p, frames, seed + 97 * p, stop);
            Console.WriteLine(result);
            if (!result.StartsWith("  ok", StringComparison.Ordinal)) failures++;
        }

        RandRedirect.Restore();
        Console.WriteLine(failures == 0 ? "verify-battery: ALL EXACT" : $"verify-battery: {failures} preset(s) diverge");
        return failures == 0 ? 0 : 1;
    }

    private static int[] ParsePresets(string[] args)
    {
        var one = VerifyWarps.ArgString(args, "--preset");
        if (one is not null) return [int.Parse(one)];
        var list = VerifyWarps.ArgString(args, "--presets");
        if (list is not null) return list.Split(',').Select(int.Parse).ToArray();
        return Enumerable.Range(0, 26).ToArray();
    }

    private static string RunPreset(int preset, int frames, int scriptSeed, bool stop)
    {
        var script = RandRedirect.MakeScript(scriptSeed, 1 << 22);
        RandRedirect.SetScript(script);
        using var fx = WmpInternalFactory.CreateInstance(WmpInternalFactory.Resolve(WmpGuids.ClsidBattery));
        var battery = fx.RawPointer - 0xd90;
        var rd = battery + 8;
        if (WmpModule.VaOf(*(nint*)battery) != 0x1807a76f0)
            return $"  FAIL preset {preset}: CBattery vtable is 0x{WmpModule.VaOf(*(nint*)battery):X}, the interface offset is wrong";

        var rand = new ScriptedCrtRand(script);
        var ours = new BatteryCore(rand);
        if (RandRedirect.Position != rand.Position)
            return $"  FAIL preset {preset}: construction drew {RandRedirect.Position}, ours {rand.Position}";

        var hr = fx.SetCurrentPreset(preset);
        if (hr < 0) return $"  FAIL preset {preset}: SetCurrentPreset -> 0x{hr:X8}";
        ours.SetCurrentPreset(preset);
        if (RandRedirect.Position != rand.Position)
            return $"  FAIL preset {preset}: SetCurrentPreset drew {RandRedirect.Position}, ours {rand.Position}";

        using var dib = new DibTarget(W, H);
        using var levels = new TimedLevelBuffer();
        var ourLevels = new BatteryLevels();
        var rc = new RECT(0, 0, W, H);
        var audio = new Random(scriptSeed);
        var ourPixels = new int[W * H];

        var total = frames + (stop ? 310 : 0);
        for (var f = 0; f < total; f++)
        {
            Fill(audio, levels, f, frames);
            levels.State = f >= frames ? 0 : f % 97 == 96 ? 1 : 2;
            new Span<byte>(levels.Pointer, 0x1000).CopyTo(ourLevels.Bytes);
            ourLevels.State = levels.State;

            fx.Render(levels.Pointer, dib.Hdc, ref rc);
            var presented = ours.Render(ourLevels);

            var where = $"preset {preset} ({ours.PresetTitle(preset)}) frame {f} state {levels.State}";
            if (RandRedirect.Position != rand.Position)
                return $"  FAIL {where}: rand position real {RandRedirect.Position}, ours {rand.Position}";

            var front = *(nint*)(rd + 0xb8);
            var bits = new ReadOnlySpan<byte>((void*)*(nint*)(front + 0x20), W * H);
            var mine = ours.RenderData.Front.Bits;
            for (var i = 0; i < bits.Length; i++)
                if (bits[i] != mine[i])
                    return $"  FAIL {where}: field ({i % W},{i / W}) real {bits[i]} ours {mine[i]}";

            var pal = new ReadOnlySpan<uint>((void*)(rd + 0x4dc), 256);
            for (var i = 0; i < 256; i++)
                if ((pal[i] & 0xFFFFFF) != (ours.RenderData.Palette.Current[i] & 0xFFFFFF))
                    return $"  FAIL {where}: palette[{i}] real {pal[i]:X6} ours {ours.RenderData.Palette.Current[i]:X6}";

            var px = dib.Pixels;
            if (presented)
            {
                ours.CopyTo(ourPixels);
                for (var i = 0; i < W * H; i++)
                {
                    var real = px[4 * i] | px[4 * i + 1] << 8 | px[4 * i + 2] << 16;
                    var mineRgb = ourPixels[i] & 0xFFFFFF;
                    if (real != mineRgb)
                        return $"  FAIL {where}: presented ({i % W},{i / W}) index {mine[i]} real #{real:X6} ours #{mineRgb:X6}" +
                               $" [display {ours.DisplayPalette[mine[i]]:X8} current {ours.RenderData.Palette.Current[mine[i]]:X8} realpal {pal[mine[i]]:X8} source {ours.RenderData.Palette.Source[mine[i]]:X8}]";
                }
            }
            else
            {
                var c = ours.StopFillColor; // R | G<<8 | B<<16
                var want = (int)((c & 0xFF) << 16 | (c & 0xFF00) | (c >> 16) & 0xFF);
                var real = px[0] | px[1] << 8 | px[2] << 16;
                if (real != want) return $"  FAIL {where}: stop fill real #{real:X6} ours #{want:X6}";
            }
        }

        return $"  ok   preset {preset,2} {ours.PresetTitle(preset),-28} {total} frames, {rand.Position} draws";
    }

    /// <summary>Synthetic audio with real structure: a falling, beating spectrum and a noisy waveform.
    /// Some frames are silent.</summary>
    private static void Fill(Random r, TimedLevelBuffer tl, int f, int playFrames)
    {
        var silent = f % 53 == 52;
        var beat = 1.0 + 0.6 * Math.Sin(f * 0.21);
        for (var i = 0; i < 1024; i++)
        {
            tl.Frequency0[i] = silent ? (byte)0 : (byte)Math.Clamp((int)((230 - i / 4.0) * beat) - r.Next(50), 0, 255);
            tl.Frequency1[i] = silent ? (byte)0 : (byte)Math.Clamp((int)((220 - i / 4.0) * beat) - r.Next(50), 0, 255);
            tl.Waveform0[i] = silent ? (byte)128 : (byte)Math.Clamp(128 + (int)(90 * Math.Sin(i * 0.05 + f) * beat) + r.Next(-20, 21), 0, 255);
            tl.Waveform1[i] = silent ? (byte)128 : (byte)Math.Clamp(128 + (int)(90 * Math.Cos(i * 0.07 + f) * beat) + r.Next(-20, 21), 0, 255);
        }
        tl.TimeStamp = 10_000_000L * f / 60;
    }
}
