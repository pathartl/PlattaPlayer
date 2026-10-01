using PlattaPlayer.Visualizations.Wmp;
using PlattaPlayer.Visualizations.Wmp.Alchemy;
using PlattaPlayer.Visualizations.Wmp.Audio;
using PlattaPlayer.Wmp.Harness.Imaging;
using PlattaPlayer.Wmp.Harness.Synth;

namespace PlattaPlayer.Wmp.Harness.Verbs;

/// <summary>
/// Measures how each of our warp kernels affects the feedback field over time, holding one renderer
/// fixed, and reports INK COVERAGE rather than frame similarity.
///
/// Frame-similarity against the real effect is the wrong instrument here: the reference picks different
/// effects than we do, so the score largely rewards drawing less, and it has twice suggested a
/// structurally correct change was a regression.
///
/// READ THIS BEFORE TREATING A NUMBER AS A DEFECT. This verb used to quote "the real renderers settle at
/// 17.2% / 9.7% / 2.9%" as the target, and that comparison is invalid twice over. Those figures come from
/// `isolate-renderers`, which zeroes the other pool weights so exactly one renderer can be accepted — an
/// artificial configuration the shipped effect never runs. Measured properly, `sweep-alchemy --seeds 24
/// --frame 150` puts the REAL Alchemy's own ink coverage at 6.0%-99.6% (median ~33%, four of 24 seeds
/// fully saturated). A pinned kernel of ours reading 40% is inside the real distribution, not a bug.
///
/// What this verb is still good for is RELATIVE movement: run it before and after a change to see which
/// kernel a change actually affected, and whether one has gone somewhere it has never been.
///
/// Usage: warp-profile [--frames N] [--draw N]
/// </summary>
internal static class WarpProfile
{
    /// <summary>
    /// Ink coverage of the REAL effect at frame 150 across 24 seeds (`sweep-alchemy`), which is the only
    /// directly comparable reference: min, median, max.
    /// </summary>
    private static readonly double[] ReferenceInk = [0.060, 0.334, 0.996];

    /// <summary>
    /// The four warp kernel types, in EffectRegistry type order. BassBounceZoom is no longer here — it is
    /// a category-7 effect in its own slot, not a member of the shift stage's kernel pool.
    /// </summary>
    private static readonly string[] WarpNames =
        ["LinearShift", "SnafuShift", "StretchShift", "OScopeShift"];

    public static int Run(string[] args)
    {
        var frames = ArgInt(args, "--frames", 150);
        var draw = ArgInt(args, "--draw", 0);

        if (!File.Exists(HarnessPaths.SynthFile))
        {
            Console.Error.WriteLine($"warp-profile: {HarnessPaths.SynthFile} not found — run 'synth' first.");
            return 2;
        }
        var audio = AudioFrameSet.Read(HarnessPaths.SynthFile);
        var dir = Path.Combine(HarnessPaths.ReportDir, "alchemy-warps");
        Directory.CreateDirectory(dir);

        Console.WriteLine($"profiling {WarpNames.Length} warp kernels over {frames} frames, renderer pinned to {draw}");
        Console.WriteLine($"reference: the REAL effect's own ink over 24 seeds is " +
                          $"{ReferenceInk[0]:P1} min / {ReferenceInk[1]:P1} median / {ReferenceInk[2]:P1} max");
        Console.WriteLine("           (use this for relative movement between runs, not as a target)");
        Console.WriteLine();
        Console.WriteLine("   warp              ink@30   ink@60   ink@90  ink@120  ink@150   verdict");

        for (var w = 0; w < WarpNames.Length; w++)
        {
            var (samples, final) = Profile(w, draw, audio, frames);
            PngWriter.WriteBgra(Path.Combine(dir, $"warp{w}-{WarpNames[w]}.png"), final, 640, 480);

            // The real effect reaches 99.6% on 4 of 24 seeds, so only a PEGGED field (saturated by frame
            // 30 and flat forever, which no real seed does) is evidence of a transport bug.
            var last = samples[^1];
            var verdict = samples[0] > 0.95 && last > 0.95 ? "PEGGED" : "in real range";
            Console.WriteLine($"   {WarpNames[w],-16} {string.Join("  ", samples.Select(s => s.ToString("P1").PadLeft(7)))}   {verdict}");
        }

        Console.WriteLine();
        Console.WriteLine($"wrote final frames to {dir}");
        return 0;
    }

    private static (double[] Samples, byte[] Final) Profile(
        int warp, int draw, List<AudioFrame> audio, int frames)
    {
        var core = new AlchemyCore(new Random(12345));
        core.Resize(640, 480);
        core.Pin(warp, draw);

        var levels = new TimedLevels();
        var samples = new List<double>();
        var scratch = new byte[640 * 480 * 4];

        for (var i = 0; i < frames; i++)
        {
            var a = audio[Math.Min(i + 30, audio.Count - 1)]; // skip the leading silence
            a.Frequency0.CopyTo(levels.Frequency[0], 0);
            a.Frequency1.CopyTo(levels.Frequency[1], 0);
            a.Waveform0.CopyTo(levels.Waveform[0], 0);
            a.Waveform1.CopyTo(levels.Waveform[1], 0);
            levels.State = 2;

            core.Update(levels, 1000.0 / WmpFrameRate.WindowedIntervalMs);

            if ((i + 1) % 30 == 0 && samples.Count < 5) samples.Add(Ink(core.Pixels));
        }

        Buffer.BlockCopy(core.Pixels, 0, scratch, 0, scratch.Length);
        for (var i = 3; i < scratch.Length; i += 4) scratch[i] = 0xFF;
        while (samples.Count < 5) samples.Add(samples.Count > 0 ? samples[^1] : 0);
        return (samples.ToArray(), scratch);
    }

    private static double Ink(int[] pixels)
    {
        long lit = 0;
        foreach (var p in pixels)
            if ((p & 0xFF) > 8 || ((p >> 8) & 0xFF) > 8 || ((p >> 16) & 0xFF) > 8) lit++;
        return (double)lit / pixels.Length;
    }

    private static int ArgInt(string[] args, string name, int fallback)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(args[i + 1], out var v)) return v;
        return fallback;
    }
}
