using PlattaPlayer.Wmp.Harness.Imaging;
using PlattaPlayer.Wmp.Harness.Interop;
using PlattaPlayer.Wmp.Harness.Synth;

namespace PlattaPlayer.Wmp.Harness.Verbs;

/// <summary>
/// Renders the real Alchemy at a range of pinned seeds and dumps one frame from each, so the effects it
/// can select can be seen side by side.
///
/// This is the point of pinning the seed: Alchemy chooses its warp and overlay effects at random and
/// offers no way to ask for a particular one, so the only way to study an individual effect is to find a
/// seed that happens to select it. A sweep turns "the effects are random" into a browsable, repeatable
/// index — pick the seed whose output shows the effect you are working on, then capture that seed in
/// full and compare frame by frame.
///
/// Usage: sweep-alchemy [--seeds N] [--frame N] [--size WxH]
/// </summary>
internal static class SweepAlchemy
{
    public static int Run(string[] args)
    {
        var seeds = ArgInt(args, "--seeds", 12);
        var frame = ArgInt(args, "--frame", 200);
        var (w, h) = Capture.ArgSize(args, "--size", 640, 480);

        if (!File.Exists(HarnessPaths.SynthFile))
        {
            Console.Error.WriteLine($"sweep-alchemy: {HarnessPaths.SynthFile} not found — run 'synth' first.");
            return 2;
        }
        var audio = AudioFrameSet.Read(HarnessPaths.SynthFile);

        var dir = Path.Combine(HarnessPaths.ReportDir, "alchemy-seed-sweep");
        Directory.CreateDirectory(dir);

        Console.WriteLine($"sweeping {seeds} seed(s), capturing frame {frame} at {w}x{h}");
        Console.WriteLine("NOTE mpvis.DLL keeps module state across instantiations, so seeds after the first");
        Console.WriteLine("     in one process are only nearly-reproducible. Re-capture a chosen seed on its");
        Console.WriteLine("     own with `capture alchemy --seed N` before trusting it frame-for-frame.");
        Console.WriteLine();

        for (uint seed = 0; seed < seeds; seed++)
        {
            var pixels = CaptureFrame(seed, audio, frame, w, h);
            var path = Path.Combine(dir, $"seed{seed:D3}.png");
            PngWriter.WriteBgra(path, pixels, w, h);
            Console.WriteLine($"   seed {seed,3} -> {Path.GetFileName(path)}   (ink {Ink(pixels):P1})");
        }

        Console.WriteLine();
        Console.WriteLine($"wrote {seeds} frame(s) to {dir}");
        return 0;
    }

    private static byte[] CaptureFrame(uint seed, List<AudioFrame> audio, int frame, int w, int h)
    {
        VirtualClock.Reset();
        using var dib = new DibTarget(w, h);
        using var fx = AlchemyFactory.CreateSeeded(seed);
        using var levels = new TimedLevelBuffer();

        var rc = new RECT(0, 0, w, h);
        for (var i = 0; i <= frame && i < audio.Count; i++)
        {
            var f = audio[i];
            f.Frequency0.CopyTo(levels.Frequency0);
            f.Frequency1.CopyTo(levels.Frequency1);
            f.Waveform0.CopyTo(levels.Waveform0);
            f.Waveform1.CopyTo(levels.Waveform1);
            levels.State = f.State;
            levels.TimeStamp = f.TimeStamp;
            unsafe { fx.Render(levels.Pointer, dib.Hdc, ref rc); }
            VirtualClock.AdvanceFrame();
        }
        return dib.Snapshot();
    }

    /// <summary>Fraction of non-black pixels — a rough "how much is going on" figure for the listing.</summary>
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
