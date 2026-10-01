using PlattaPlayer.Wmp.Harness.Imaging;
using PlattaPlayer.Wmp.Harness.Interop;
using PlattaPlayer.Wmp.Harness.Synth;

namespace PlattaPlayer.Wmp.Harness.Verbs;

/// <summary>
/// Renders each of the real Alchemy's renderers ON ITS OWN, so ours can be fixed one at a time against
/// a picture of what it is supposed to look like.
///
/// Until now Alchemy could only be compared in aggregate: it picks its effects at random, so our output
/// and the reference were never showing the same thing and the similarity score mostly measured how
/// much ink each side happened to lay down. Forcing a single known renderer removes that confound
/// entirely — see <see cref="AlchemyIntrospect.IsolateRenderer"/> for how.
///
/// Usage: isolate-renderers [--frames N] [--seed N]
/// </summary>
internal static class IsolateRenderers
{
    public static int Run(string[] args)
    {
        var frames = ArgInt(args, "--frames", 120);
        var seed = (uint)ArgInt(args, "--seed", 1);

        if (!File.Exists(HarnessPaths.SynthFile))
        {
            Console.Error.WriteLine($"isolate-renderers: {HarnessPaths.SynthFile} not found — run 'synth' first.");
            return 2;
        }
        var audio = AudioFrameSet.Read(HarnessPaths.SynthFile);

        // Discover the pool once so we know which entries are renderers.
        PoolInfo[] pool;
        using (var probe = AlchemyFactory.CreateSeeded(seed))
        {
            WarmUp(probe, audio, 4);
            var introspect = new AlchemyIntrospect(probe.RawPointer);
            if (!introspect.LooksValid)
            {
                Console.Error.WriteLine("scheduler offsets do not look right; refusing to poke it.");
                return 1;
            }
            pool = introspect.ReadPool()
                .Select(e => new PoolInfo(e.Index, e.Category, e.Weight))
                .ToArray();
        }

        var dir = Path.Combine(HarnessPaths.ReportDir, "alchemy-renderers");
        Directory.CreateDirectory(dir);

        Console.WriteLine($"isolating each pooled effect for {frames} frames at 640x480");
        Console.WriteLine();

        foreach (var entry in pool)
        {
            var pixels = RenderIsolated(entry.Index, seed, audio, frames);
            var path = Path.Combine(dir, $"pool{entry.Index}-cat{entry.Category}.png");
            PngWriter.WriteBgra(path, pixels, 640, 480);
            Console.WriteLine($"   pool {entry.Index}  category {entry.Category}  weight {entry.Weight:F2}  " +
                              $"ink {Ink(pixels):P1}  -> {Path.GetFileName(path)}");
        }

        Console.WriteLine();
        Console.WriteLine($"wrote {pool.Length} isolated renders to {dir}");
        Console.WriteLine("Category 4 entries are the renderers; category 3 is the warp stage and 7 the bass bounce.");
        return 0;
    }

    private readonly record struct PoolInfo(int Index, int Category, float Weight);

    private static byte[] RenderIsolated(int poolIndex, uint seed, List<AudioFrame> audio, int frames)
    {
        VirtualClock.Reset();
        using var dib = new DibTarget(640, 480);
        using var fx = AlchemyFactory.CreateSeeded(seed);
        using var levels = new TimedLevelBuffer();

        // One advance so the scheduler exists in a settled state, then pin it.
        WarmUp(fx, audio, 2);
        var introspect = new AlchemyIntrospect(fx.RawPointer);
        introspect.IsolateRenderer(poolIndex);

        var rc = new RECT(0, 0, 640, 480);
        for (var i = 0; i < frames && i < audio.Count; i++)
        {
            var f = audio[i];
            f.Frequency0.CopyTo(levels.Frequency0);
            f.Frequency1.CopyTo(levels.Frequency1);
            f.Waveform0.CopyTo(levels.Waveform0);
            f.Waveform1.CopyTo(levels.Waveform1);
            // Force every frame fresh so the field actually advances for the whole run.
            levels.State = 2;
            levels.TimeStamp = i * 10_000_000L / 60;
            unsafe { fx.Render(levels.Pointer, dib.Hdc, ref rc); }
            VirtualClock.AdvanceFrame();
        }
        return dib.Snapshot();
    }

    private static void WarmUp(WmpEffect fx, List<AudioFrame> audio, int frames)
    {
        using var dib = new DibTarget(640, 480);
        using var levels = new TimedLevelBuffer();
        var rc = new RECT(0, 0, 640, 480);
        for (var i = 0; i < frames && i < audio.Count; i++)
        {
            var f = audio[Math.Min(60, audio.Count - 1)];
            f.Frequency0.CopyTo(levels.Frequency0);
            f.Frequency1.CopyTo(levels.Frequency1);
            levels.State = 2;
            levels.TimeStamp = i * 10_000_000L / 60;
            unsafe { fx.Render(levels.Pointer, dib.Hdc, ref rc); }
        }
    }

    private static double Ink(byte[] bgra)
    {
        long lit = 0;
        for (var i = 0; i < bgra.Length; i += 4)
            if (bgra[i] > 8 || bgra[i + 1] > 8 || bgra[i + 2] > 8) lit++;
        return (double)lit / (bgra.Length / 4);
    }

    private static int ArgInt(string[] args, string name, int fallback)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(args[i + 1], out var v)) return v;
        return fallback;
    }
}
