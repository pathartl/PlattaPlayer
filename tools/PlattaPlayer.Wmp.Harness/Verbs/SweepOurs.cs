using PlattaPlayer.Visualizations.Wmp;
using PlattaPlayer.Visualizations.Wmp.Alchemy;
using PlattaPlayer.Visualizations.Wmp.Audio;
using PlattaPlayer.Wmp.Harness.Imaging;
using PlattaPlayer.Wmp.Harness.Synth;

namespace PlattaPlayer.Wmp.Harness.Verbs;

/// <summary>
/// The counterpart to <see cref="SweepAlchemy"/> for OUR engine: renders the unpinned Alchemy core at a
/// range of seeds and dumps one frame from each, with the same ink figure and the same layout.
///
/// This exists because the single-seed instruments are too weak to judge Alchemy. `warp-profile` pins one
/// kernel and one renderer under one fixed seed, so it samples ONE parameter draw — change anything that
/// shifts the RNG stream (a differently-ordered re-roll, an extra random call) and its numbers move
/// wholesale without any behavioural regression. Alchemy is a distribution, not a frame, on both sides:
/// the real effect's own ink at frame 150 spans 6.0%-99.6% across seeds. So the only honest comparison is
/// distribution against distribution, run at the same frame and the same size:
/// <code>
///   sweep-alchemy --seeds 24 --frame 150      (theirs)
///   sweep-ours    --seeds 24 --frame 150      (ours)
/// </code>
/// Compare the min / median / max and the shape of the contact sheets, not any individual pair — the two
/// sides cannot pick the same effects, because mpvis links its own CRT and its scheduler cannot be driven
/// from this process.
///
/// Usage: sweep-ours [--seeds N] [--frame N]
/// </summary>
internal static class SweepOurs
{
    public static int Run(string[] args)
    {
        var seeds = ArgInt(args, "--seeds", 12);
        var frame = ArgInt(args, "--frame", 200);
        const int w = 640;
        const int h = 480;

        if (!File.Exists(HarnessPaths.SynthFile))
        {
            Console.Error.WriteLine($"sweep-ours: {HarnessPaths.SynthFile} not found — run 'synth' first.");
            return 2;
        }
        var audio = AudioFrameSet.Read(HarnessPaths.SynthFile);

        var dir = Path.Combine(HarnessPaths.ReportDir, "ours-seed-sweep");
        Directory.CreateDirectory(dir);

        Console.WriteLine($"sweeping {seeds} seed(s) of OUR engine, capturing frame {frame} at {w}x{h}");
        Console.WriteLine();

        var inks = new List<double>();
        for (var seed = 0; seed < seeds; seed++)
        {
            var pixels = RenderFrame(seed, audio, frame, w, h);
            var path = Path.Combine(dir, $"seed{seed:D3}.png");
            PngWriter.WriteBgra(path, pixels, w, h);
            var ink = Ink(pixels);
            inks.Add(ink);
            Console.WriteLine($"   seed {seed,3} -> {Path.GetFileName(path)}   (ink {ink:P1})");
        }

        inks.Sort();
        Console.WriteLine();
        Console.WriteLine($"   ink: {inks[0]:P1} min / {inks[inks.Count / 2]:P1} median / {inks[^1]:P1} max");
        Console.WriteLine("   the real effect, same frame, 24 seeds: 6.0% min / 33.4% median / 99.6% max");
        Console.WriteLine();
        Console.WriteLine($"wrote {seeds} frame(s) to {dir}");
        return 0;
    }

    private static byte[] RenderFrame(int seed, List<AudioFrame> audio, int frame, int w, int h)
    {
        var core = new AlchemyCore(new Random(seed));
        core.Resize(w, h);

        var levels = new TimedLevels();
        for (var i = 0; i <= frame && i < audio.Count; i++)
        {
            var a = audio[i];
            a.Frequency0.CopyTo(levels.Frequency[0], 0);
            a.Frequency1.CopyTo(levels.Frequency[1], 0);
            a.Waveform0.CopyTo(levels.Waveform[0], 0);
            a.Waveform1.CopyTo(levels.Waveform[1], 0);
            levels.State = a.State;
            core.Update(levels, 1000.0 / WmpFrameRate.WindowedIntervalMs);
        }

        var bgra = new byte[w * h * 4];
        Buffer.BlockCopy(core.Pixels, 0, bgra, 0, bgra.Length);
        for (var i = 3; i < bgra.Length; i += 4) bgra[i] = 0xFF;
        return bgra;
    }

    /// <summary>Fraction of non-black pixels — identical to SweepAlchemy.Ink so the two tables compare.</summary>
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
