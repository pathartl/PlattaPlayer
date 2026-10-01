using System.Diagnostics;
using PlattaPlayer.Visualizations.Wmp.Alchemy;
using PlattaPlayer.Visualizations.Wmp.Audio;
using PlattaPlayer.Wmp.Harness.Synth;

namespace PlattaPlayer.Wmp.Harness.Verbs;

/// <summary>
/// Times <see cref="AlchemyCore.Update"/> on the fixed 640x480 field over the looped synthetic audio, to
/// check the CPU engine keeps up with WMP's ~16 ms frame. Build the harness in Release for meaningful
/// numbers.
///
/// Usage: time-alchemy [--frames N] [--seed N]
/// </summary>
internal static class TimeAlchemy
{
    public static int Run(string[] args)
    {
        if (!File.Exists(HarnessPaths.SynthFile))
        {
            Console.Error.WriteLine($"time-alchemy: {HarnessPaths.SynthFile} not found — run 'synth' first.");
            return 2;
        }

        var frames = ArgInt(args, "--frames", 3000);
        var seed = ArgInt(args, "--seed", 1);
        var audio = AudioFrameSet.Read(HarnessPaths.SynthFile).Where(a => a.State == 2).ToList();

        var core = new AlchemyCore(new Random(seed));
        core.Resize(640, 480);
        var levels = new TimedLevels();
        var times = new List<double>(frames);
        var sw = new Stopwatch();

        for (var f = 0; f < frames; f++)
        {
            var a = audio[f % audio.Count];
            a.Frequency0.CopyTo(levels.Frequency[0], 0);
            a.Frequency1.CopyTo(levels.Frequency[1], 0);
            a.Waveform0.CopyTo(levels.Waveform[0], 0);
            a.Waveform1.CopyTo(levels.Waveform[1], 0);
            levels.State = a.State;

            sw.Restart();
            core.Update(levels, 62.5);
            sw.Stop();
            if (f >= 30) times.Add(sw.Elapsed.TotalMilliseconds); // skip JIT warm-up
        }

        times.Sort();
        double P(double q) => times[Math.Min(times.Count - 1, (int)(q * times.Count))];
        Console.WriteLine($"AlchemyCore.Update at 640x480 over {times.Count} frames: " +
                          $"mean {times.Average():0.00} ms, median {P(0.5):0.00}, p95 {P(0.95):0.00}, " +
                          $"p99 {P(0.99):0.00}, max {times[^1]:0.00}  (budget {1000.0 / 62.5:0.0} ms)");
        return 0;
    }

    private static int ArgInt(string[] args, string name, int fallback)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(args[i + 1], out var v)) return v;
        return fallback;
    }
}
