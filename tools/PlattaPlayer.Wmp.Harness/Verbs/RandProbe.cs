using PlattaPlayer.Wmp.Harness.Imaging;
using PlattaPlayer.Wmp.Harness.Interop;
using PlattaPlayer.Wmp.Harness.Synth;

namespace PlattaPlayer.Wmp.Harness.Verbs;

/// <summary>
/// Works out WHY a capture is not reproducible, and whether it can be made so.
///
/// wmp.dll's bar renderer adds <c>rand()*20/32767 - 10</c> px of jitter to every bar whenever
/// TimedLevel.state == 2. If that rand() is the same per-thread ucrtbase stream this process can seed,
/// captures become reproducible and our renderer can reproduce the jitter exactly by running the same
/// LCG. If it is not, the jitter is uncontrollable and Bars must be compared structurally instead.
///
/// The verb answers three separate questions rather than guessing:
///   1. Does a Render call consume values from the ucrtbase stream we can see?
///   2. With a fixed seed, do two capture runs agree?
///   3. If not, WHICH frames disagree — and do they line up with state == 2?
/// </summary>
internal static class RandProbe
{
    public static int Run(string[] args)
    {
        var effect = args.Length > 0 ? args[0].ToLowerInvariant() : "barswaves";

        Console.WriteLine($"== 1. Does {effect}'s Render consume from ucrtbase's per-thread rand() stream? ==");
        ProbeStreamSharing(effect);

        Console.WriteLine();
        Console.WriteLine("== 2/3. Where do two seeded capture runs diverge? ==");
        return ProbeDivergence(effect);
    }

    private static WmpEffect CreateEffect(string effect)
    {
        switch (effect)
        {
            case "alchemy":
                return AlchemyFactory.Create();
            case "battery":
                return WmpInternalFactory.CreateInstance(WmpInternalFactory.Resolve(WmpGuids.ClsidBattery));
            default:
                var fx = WmpInternalFactory.CreateInstance(
                    WmpInternalFactory.Resolve(WmpGuids.ClsidBarsAndWaves));
                fx.SetCurrentPreset(0);
                return fx;
        }
    }

    private static void ProbeStreamSharing(string effect)
    {
        NativeMethods.UcrtSrand(1);
        var baseline = new[] { NativeMethods.UcrtRand(), NativeMethods.UcrtRand(), NativeMethods.UcrtRand() };
        Console.WriteLine($"  srand(1) then rand() x3 -> {string.Join(", ", baseline)}");

        using var dib = new DibTarget(640, 480);
        using var fx = CreateEffect(effect);
        using var levels = new TimedLevelBuffer();
        for (var b = 0; b < 64; b++) { levels.Frequency0[b] = 200; levels.Frequency1[b] = 200; }
        levels.State = 2;

        NativeMethods.UcrtSrand(1);
        var rc = new RECT(0, 0, 640, 480);
        unsafe { fx.Render(levels.Pointer, dib.Hdc, ref rc); }
        var after = NativeMethods.UcrtRand();
        Console.WriteLine($"  srand(1), one Render(state=2), then rand() -> {after}");

        if (after != baseline[0])
            Console.WriteLine("  [ok] Render advanced the stream we can see — the seed IS shared, so this\n" +
                              "       effect's randomness can be pinned from here.");
        else
            Console.WriteLine("  [!!] Render did NOT touch our stream — this module links its own CRT copy.\n" +
                              "       Its randomness cannot be controlled from this process.");
    }

    private static int ProbeDivergence(string effect)
    {
        var audio = AudioFrameSet.Read(HarnessPaths.SynthFile);
        var a = CaptureSeeded(audio, effect);
        var b = CaptureSeeded(audio, effect);

        var differing = new List<int>();
        for (var i = 0; i < a.Frames.Count; i++)
            if (!a.Frames[i].AsSpan().SequenceEqual(b.Frames[i])) differing.Add(i);

        Console.WriteLine($"  {differing.Count}/{a.Frames.Count} frames differ between two seeded runs");

        if (differing.Count == 0)
        {
            Console.WriteLine("  [ok] seeded captures are reproducible.");
            return 0;
        }

        var byState = differing.GroupBy(i => audio[i].State)
                               .OrderBy(g => g.Key)
                               .Select(g => $"state {g.Key}: {g.Count()}");
        Console.WriteLine($"  differing frames by TimedLevel state -> {string.Join(", ", byState)}");
        Console.WriteLine($"  first differing frame: {differing[0]} (state {audio[differing[0]].State})");

        var stateCounts = audio.GroupBy(f => f.State).OrderBy(g => g.Key)
                               .Select(g => $"state {g.Key}: {g.Count()}");
        Console.WriteLine($"  sequence composition            -> {string.Join(", ", stateCounts)}");

        // If every differing frame is a state==2 frame and every state==2 frame differs, the jitter is
        // the sole cause and everything else about the renderer is deterministic.
        var allDifferingAreState2 = differing.All(i => audio[i].State == 2);
        var allState2Differ = audio.Select((f, i) => (f, i)).Where(t => t.f.State == 2)
                                   .All(t => differing.Contains(t.i));
        Console.WriteLine();
        Console.WriteLine(allDifferingAreState2 && allState2Differ
            ? "  Diagnosis: divergence is EXACTLY the state==2 frames — i.e. purely the per-bar jitter.\n" +
              "  Everything else (geometry, colour, falloff, peaks, trails) is deterministic, so the\n" +
              "  state 0/1 frames can still be gated bit-exactly and state 2 frames structurally."
            : allDifferingAreState2
                ? "  Diagnosis: only state==2 frames diverge, but not all of them — consistent with jitter\n" +
                  "  that happens to land on the same pixels sometimes."
                : "  Diagnosis: frames with state != 2 also diverge. Something beyond the jitter is\n" +
                  "  non-deterministic; investigate before trusting any gate.");
        return 0;
    }

    private static ImageFrameSet CaptureSeeded(List<AudioFrame> audio, string effect)
    {
        using var dib = new DibTarget(640, 480);
        using var fx = CreateEffect(effect);
        using var levels = new TimedLevelBuffer();
        // Seed AFTER construction: mpvis.DLL's constructor calls srand() with a time-derived value, so
        // seeding earlier would simply be overwritten.
        NativeMethods.UcrtSrand(1);

        var set = new ImageFrameSet(640, 480);
        var rc = new RECT(0, 0, 640, 480);
        foreach (var frame in audio)
        {
            frame.Frequency0.CopyTo(levels.Frequency0);
            frame.Frequency1.CopyTo(levels.Frequency1);
            frame.Waveform0.CopyTo(levels.Waveform0);
            frame.Waveform1.CopyTo(levels.Waveform1);
            levels.State = frame.State;
            levels.TimeStamp = frame.TimeStamp;
            unsafe { fx.Render(levels.Pointer, dib.Hdc, ref rc); }
            set.Add(dib.Snapshot());
        }
        return set;
    }
}
