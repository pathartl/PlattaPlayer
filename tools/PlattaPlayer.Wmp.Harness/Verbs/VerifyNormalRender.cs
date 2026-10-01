using PlattaPlayer.Visualizations.Wmp.Alchemy;
using PlattaPlayer.Visualizations.Wmp.Alchemy.Effects;
using PlattaPlayer.Visualizations.Wmp.Audio;
using PlattaPlayer.Visualizations.Wmp.Framebuffer;
using PlattaPlayer.Wmp.Harness.Imaging;
using PlattaPlayer.Wmp.Harness.Interop;
using PlattaPlayer.Wmp.Harness.Synth;

namespace PlattaPlayer.Wmp.Harness.Verbs;

/// <summary>
/// Tier 3: diffs each renderer's WHOLE frame against the real <c>NormalRender</c>
/// (<see cref="NormalRenderOracle"/>). Both sides are randomized from one rand() script, fed the same
/// synthetic audio, and drawn into their own fields with no warp or feedback, so the strokes simply
/// accumulate. After every frame it compares the draw count and every pixel.
///
/// The first divergent frame is the useful output. Strokes accumulate, so once a frame differs every
/// later one does too. At that frame it writes the real image, ours, and a diff to
/// <c>artifacts/report/normalrender/</c>.
///
/// Usage: verify-normalrender [--frames N] [--sets N] [--seed BASE] [--only SuperStar|WonderWave|AtomBalls]
/// </summary>
internal static class VerifyNormalRender
{
    private const int W = 640;
    private const int H = 480;
    private const int ScriptLength = 1 << 20;

    private static readonly RenderEffectKind[] Kinds =
        [RenderEffectKind.SuperStar, RenderEffectKind.WonderWave, RenderEffectKind.AtomBalls];

    public static int Run(string[] args)
    {
        if (!File.Exists(HarnessPaths.SynthFile))
        {
            Console.Error.WriteLine($"verify-normalrender: {HarnessPaths.SynthFile} not found — run 'synth' first.");
            return 2;
        }

        var frames = ArgInt(args, "--frames", 120);
        var sets = ArgInt(args, "--sets", 3);
        var seedBase = ArgInt(args, "--seed", 5000);
        var only = ArgString(args, "--only");
        var audio = AudioFrameSet.Read(HarnessPaths.SynthFile).Where(a => a.State == 2).ToList();

        Console.WriteLine(MpvisModule.Describe());
        Console.WriteLine(RandRedirect.SelfTest());
        Console.WriteLine();
        Console.WriteLine($"comparing whole-frame NormalRender, {frames} frames x {sets} script(s) per renderer");

        var dir = Path.Combine(HarnessPaths.ReportDir, "normalrender");
        Directory.CreateDirectory(dir);

        var failures = 0;
        var total = 0;
        foreach (var kind in Kinds)
        {
            if (only is not null && !kind.ToString().Equals(only, StringComparison.OrdinalIgnoreCase)) continue;
            total++;
            Console.WriteLine();
            Console.WriteLine($"=== {kind} ===");
            var ok = true;
            for (var set = 0; set < sets; set++)
                if (!RunOne(kind, seedBase + set, frames, audio, dir)) ok = false;
            if (!ok) failures++;
        }

        RandRedirect.Restore();

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "verify-normalrender: every renderer draws exactly what the real one draws."
            : $"verify-normalrender: {failures} of {total} renderer(s) DIVERGE — see above and {dir}.");
        return failures == 0 ? 0 : 1;
    }

    private static bool RunOne(RenderEffectKind kind, int seed, int frames, List<AudioFrame> audio, string dir)
    {
        var script = RandRedirect.MakeScript(seed, ScriptLength);

        // Both sides are CONSTRUCTED from the script too: the renderers' CTColor constructors draw their
        // starting colours, and palettes A and B are not set up again before the first frame reads them.
        RandRedirect.SetScript(script);
        using var real = new NormalRenderOracle(kind, W, H);
        using var levelBuffer = new TimedLevelBuffer();
        var scripted = new ScriptedRandom(script);
        var ours = NewEffect(kind, scripted);
        if (RandRedirect.Position != scripted.Draws)
        {
            Console.WriteLine($"  seed {seed}: constructors draw real {RandRedirect.Position} ours {scripted.Draws}");
            return false;
        }

        real.Randomize();
        ours.FieldWidth = W;
        ours.FieldHeight = H;
        ours.Randomize(scripted);

        var afterRandomize = (real: RandRedirect.Position, ours: scripted.Draws);

        var snapshot = new AudioSnapshot();
        var levels = new TimedLevels();
        var buffer = new PixelBuffer(W, H);
        buffer.Fill(unchecked((int)0xFF000000));
        var draw = new DrawPrimitives { Random = scripted, Palette = new PaletteCycler(new Random(0)) };
        var ctx = new EffectContext { Width = W, Height = H, Audio = snapshot, Random = scripted, Draw = draw };

        for (var f = 0; f < frames; f++)
        {
            var a = audio[f % audio.Count];
            a.Frequency0.CopyTo(levelBuffer.Frequency0);
            a.Frequency1.CopyTo(levelBuffer.Frequency1);
            a.Waveform0.CopyTo(levelBuffer.Waveform0);
            a.Waveform1.CopyTo(levelBuffer.Waveform1);
            levelBuffer.State = a.State;

            a.Frequency0.CopyTo(levels.Frequency[0], 0);
            a.Frequency1.CopyTo(levels.Frequency[1], 0);
            a.Waveform0.CopyTo(levels.Waveform[0], 0);
            a.Waveform1.CopyTo(levels.Waveform[1], 0);
            levels.State = a.State;
            snapshot.Update(levels, new Random(0));

            unsafe
            {
                real.Render(levelBuffer.Pointer, snapshot.BassNow, snapshot.BassDelta, snapshot.Beat, snapshot.BassHit);
            }

            ctx.Frame = f;
            ours.Tick(ctx);
            ours.Draw(buffer, ctx);

            var diff = 0;
            for (var i = 0; i < buffer.Pixels.Length; i++)
                if (((real.Pixels[i] ^ buffer.Pixels[i]) & 0xFFFFFF) != 0) diff++;

            var drawsOk = RandRedirect.Position == scripted.Draws;
            if (diff == 0 && drawsOk) continue;

            var litReal = real.Pixels.Count(p => (p & 0xFFFFFF) != 0);
            var litOurs = buffer.Pixels.Count(p => (p & 0xFFFFFF) != 0);
            Console.WriteLine($"  seed {seed}: DIVERGES at frame {f}: {diff} pixel(s) differ " +
                              $"(lit real {litReal}, ours {litOurs}); rand() draws real {RandRedirect.Position} " +
                              $"ours {scripted.Draws} (after randomize {afterRandomize.real}/{afterRandomize.ours}); " +
                              $"beat {snapshot.Beat} hit {snapshot.BassHit} bass {snapshot.BassNow:0.###}");
            Dump(dir, $"{kind}-{seed}-f{f}", real.Pixels, buffer.Pixels);
            return false;
        }

        Console.WriteLine($"  seed {seed}: {frames} frames exact, {RandRedirect.Position} draws, " +
                          $"{real.Pixels.Count(p => (p & 0xFFFFFF) != 0)} lit pixels");
        return true;
    }

    private static void Dump(string dir, string stem, int[] real, int[] ours)
    {
        var bgraReal = new byte[W * H * 4];
        var bgraOurs = new byte[W * H * 4];
        var bgraDiff = new byte[W * H * 4];
        for (var i = 0; i < W * H; i++)
        {
            Write(bgraReal, i, real[i]);
            Write(bgraOurs, i, ours[i]);
            var r = real[i] & 0xFFFFFF;
            var o = ours[i] & 0xFFFFFF;
            // Red = only the real one lit it, green = only ours, yellow = both but different colour.
            var c = r == o ? 0 : r != 0 && o == 0 ? 0xFF0000 : r == 0 ? 0x00FF00 : 0xFFFF00;
            Write(bgraDiff, i, c);
        }
        PngWriter.WriteBgra(Path.Combine(dir, stem + "-real.png"), bgraReal, W, H);
        PngWriter.WriteBgra(Path.Combine(dir, stem + "-ours.png"), bgraOurs, W, H);
        PngWriter.WriteBgra(Path.Combine(dir, stem + "-diff.png"), bgraDiff, W, H);
    }

    private static void Write(byte[] bgra, int i, int c)
    {
        bgra[i * 4 + 0] = (byte)c;
        bgra[i * 4 + 1] = (byte)(c >> 8);
        bgra[i * 4 + 2] = (byte)(c >> 16);
        bgra[i * 4 + 3] = 0xFF;
    }

    private static AlchemyEffect NewEffect(RenderEffectKind kind, Random init)
    {
        return kind switch
        {
            RenderEffectKind.SuperStar => new SuperStarRender(init),
            RenderEffectKind.WonderWave => new WonderWaveRender(init),
            RenderEffectKind.AtomBalls => new AtomBallsRender(init),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    private static int ArgInt(string[] args, string name, int fallback)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(args[i + 1], out var v)) return v;
        return fallback;
    }

    private static string? ArgString(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }
}
