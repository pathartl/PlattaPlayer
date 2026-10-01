using PlattaPlayer.Visualizations.Wmp;
using PlattaPlayer.Visualizations.Wmp.Alchemy;
using PlattaPlayer.Visualizations.Wmp.Audio;
using PlattaPlayer.Visualizations.Wmp.BarsAndWaves;
using PlattaPlayer.Wmp.Harness.Imaging;
using PlattaPlayer.Wmp.Harness.Synth;

namespace PlattaPlayer.Wmp.Harness.Verbs;

/// <summary>
/// Drives OUR engines over the same synthetic sequence the ground-truth capture used, headlessly — no
/// Avalonia, no GPU, no window. This works only because the engines take raw TimedLevel bytes rather
/// than floats, so the two sides share an identical input and any difference in the output frames is a
/// difference in the renderers themselves.
/// </summary>
internal static class RenderOurs
{
    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("render-ours: expected an effect (barswaves).");
            return 2;
        }

        var effect = args[0].ToLowerInvariant();
        var preset = ArgInt(args, "--preset", 0);
        var (w, h) = Capture.ArgSize(args, "--size", 640, 480);
        var freshState = ArgInt(args, "--fresh-state", 2);

        if (!File.Exists(HarnessPaths.SynthFile))
        {
            Console.Error.WriteLine($"render-ours: {HarnessPaths.SynthFile} not found — run 'synth' first.");
            return 2;
        }
        var audio = AudioFrameSet.Read(HarnessPaths.SynthFile);

        var set = effect switch
        {
            "barswaves" => RenderBarsAndWaves(audio, preset, w, h, freshState),
            "alchemy" => RenderAlchemy(audio, ArgInt(args, "--seed", 12345)),
            _ => throw new ArgumentException($"Unknown effect '{effect}'."),
        };

        var path = HarnessPaths.FrameSetFile(HarnessPaths.OursDir, effect, preset, w, h, freshState);
        set.Write(path);
        Console.WriteLine($"   wrote {path}");
        Console.WriteLine($"   hash {set.ContentHash()}   frames that changed: {set.DistinctConsecutiveFrames()}/{set.Frames.Count - 1}");
        return 0;
    }

    private static ImageFrameSet RenderBarsAndWaves(
        List<AudioFrame> audio, int preset, int w, int h, int freshState)
    {
        var engine = new BarsAndWavesEngine();
        engine.SetPreset((BarsAndWavesPreset)preset);
        engine.Resize(w, h);

        var frame = new TimedLevelFrame();
        var set = new ImageFrameSet(w, h);
        var scratch = new byte[w * h * 4];

        foreach (var a in audio)
        {
            a.Frequency0.CopyTo(frame.Frequency0, 0);
            a.Frequency1.CopyTo(frame.Frequency1, 0);
            a.Waveform0.CopyTo(frame.Waveform0, 0);
            a.Waveform1.CopyTo(frame.Waveform1, 0);
            frame.State = a.State == 2 ? freshState : a.State;
            frame.TimeStamp = a.TimeStamp;
            // The harness never calls MediaInfo on the real object, so its channel count stays 0 and it
            // reads channel 0 only. Match that, or our stereo max would diverge from the ground truth.
            frame.ChannelCount = 0;

            engine.Render(frame);
            set.Add(ToBgra(engine.FrameBuffer, scratch));
        }
        return set;
    }

    /// <summary>
    /// Drives the CPU Alchemy core. Note this path has never run in the app — the shipping plugin uses
    /// the GPU engine — so it is expected to surface real bugs.
    ///
    /// Alchemy cannot be diffed frame-for-frame against the real effect: mpvis.DLL links its own CRT, so
    /// its randomised effect scheduler cannot be seeded from this process (see the `randprobe` verb) and
    /// two runs of the real object pick different effects. The value here is structural — rendering the
    /// same audio through both and comparing what the field DOES.
    /// </summary>
    private static ImageFrameSet RenderAlchemy(List<AudioFrame> audio, int seed)
    {
        const int w = 640;
        const int h = 480;

        var core = new AlchemyCore(new Random(seed));
        core.Resize(w, h);

        var levels = new TimedLevels();
        var set = new ImageFrameSet(w, h);
        var scratch = new byte[w * h * 4];

        foreach (var a in audio)
        {
            a.Frequency0.CopyTo(levels.Frequency[0], 0);
            a.Frequency1.CopyTo(levels.Frequency[1], 0);
            a.Waveform0.CopyTo(levels.Waveform[0], 0);
            a.Waveform1.CopyTo(levels.Waveform[1], 0);
            levels.State = a.State;

            core.Update(levels, 1000.0 / WmpFrameRate.WindowedIntervalMs);
            set.Add(ToBgra(core.Pixels, scratch));
        }
        return set;
    }

    private static byte[] ToBgra(int[] pixels, byte[] scratch)
    {
        Buffer.BlockCopy(pixels, 0, scratch, 0, scratch.Length);
        var copy = new byte[scratch.Length];
        scratch.CopyTo(copy, 0);
        for (var i = 3; i < copy.Length; i += 4) copy[i] = 0xFF;
        return copy;
    }

    private static int ArgInt(string[] args, string name, int fallback)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(args[i + 1], out var v)) return v;
        return fallback;
    }
}
