using PlattaPlayer.Visualizations.Wmp.Alchemy;
using PlattaPlayer.Visualizations.Wmp.Alchemy.Effects;
using PlattaPlayer.Wmp.Harness.Interop;
using PlattaPlayer.Wmp.Harness.Synth;

namespace PlattaPlayer.Wmp.Harness.Verbs;

/// <summary>
/// Diffs <see cref="EffectScheduler"/> against the REAL <c>CToleranceEffects::Render</c> frame by frame
/// (<see cref="SchedulerOracle"/>). One rand() script drives both sides. After every frame it compares the
/// draw count, the cycle countdown, every step's countdown and held effects, every pool entry's
/// <c>+0x24</c> taken flag, and the fade envelope of each held effect.
///
/// Both sides use the same pool. The four renderers are real on the DLL side and ours on the managed
/// side; verify-renders has already proved their randomizers consume identical draws. The shift stage
/// and the blurs are BassBounce stand-ins on both sides (see <see cref="SchedulerOracle"/>).
///
/// Two pause windows (<c>renderData+0x11</c>, the SPACE key) are included so the countdown gate is
/// exercised too. The default length crosses a render-cycle re-roll (1500..7500 frames).
///
/// Usage: verify-scheduler [--sets N] [--frames N] [--seed BASE]
/// </summary>
internal static class VerifyScheduler
{
    private const int ScriptLength = 1 << 18;

    public static int Run(string[] args)
    {
        var sets = ArgInt(args, "--sets", 4);
        var frames = ArgInt(args, "--frames", 8000);
        var seedBase = ArgInt(args, "--seed", 3000);
        const int w = 640;
        const int h = 480;

        Console.WriteLine(MpvisModule.Describe());
        Console.WriteLine(RandRedirect.SelfTest());
        Console.WriteLine();
        Console.WriteLine($"comparing the scheduler over {frames} frames, {sets} script(s) from seed {seedBase}");

        var failures = 0;
        for (var set = 0; set < sets; set++)
        {
            var script = RandRedirect.MakeScript(seedBase + set, ScriptLength);

            using var real = new SchedulerOracle(w, h);
            RandRedirect.SetScript(script);

            var scripted = new ScriptedRandom(script);
            var ours = new EffectScheduler(OurPool(), scripted);
            ours.Resize(w, h);

            var rerolls = new int[SchedulerOracle.Steps.Length];
            var accepts = new int[SchedulerOracle.Pool.Length];
            var previous = new int[SchedulerOracle.Steps.Length];
            string? divergence = null;
            var frame = 0;

            for (; frame < frames && divergence is null; frame++)
            {
                var paused = frame is >= 700 and < 760 || frame is >= 2900 and < 2903;
                real.Frame(paused);
                ours.Paused = paused;
                ours.Run(static _ => { });

                divergence = Compare(real, ours, scripted);

                for (var s = 0; s < rerolls.Length; s++)
                {
                    var countdown = real.StepCountdown(s);
                    if (countdown > previous[s] || frame == 0)
                    {
                        rerolls[s]++;
                        foreach (var e in real.StepActive(s)) accepts[e]++;
                    }
                    previous[s] = countdown;
                }
            }

            Console.WriteLine();
            if (divergence is null)
            {
                Console.WriteLine($"  set {set}: {frames} frames identical, {RandRedirect.Position} draws " +
                                  $"(cycle countdown now {real.CycleCountdown})");
            }
            else
            {
                failures++;
                Console.WriteLine($"  set {set}: DIVERGES at frame {frame - 1}");
                Console.WriteLine(divergence);
            }

            Console.WriteLine($"           re-rolls per step: {string.Join(" ", rerolls)}");
            Console.WriteLine("           accepts: " + string.Join(", ",
                SchedulerOracle.Pool.Select((p, i) => $"{p.Name} {accepts[i]}")));
        }

        RandRedirect.Restore();

        Console.WriteLine();
        Console.WriteLine("(* = BassBounce stand-in: the shift stage and the blurs are not constructed)");
        Console.WriteLine(failures == 0
            ? "verify-scheduler: the scheduler agrees with the real implementation on every frame."
            : $"verify-scheduler: {failures} of {sets} script(s) DIVERGE — see above.");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>The managed mirror of <see cref="SchedulerOracle.Pool"/>.</summary>
    private static List<EffectScheduler.PoolEntry> OurPool()
    {
        var init = new Random(0);
        return [.. SchedulerOracle.Pool.Select(p => new EffectScheduler.PoolEntry(p.Kind switch
        {
            RenderEffectKind.SuperStar => new SuperStarRender(init),
            RenderEffectKind.WonderWave => new WonderWaveRender(init),
            RenderEffectKind.AtomBalls => new AtomBallsRender(init),
            _ => new BassBounceZoom(),
        }, p.Category, p.Weight))];
    }

    private static string? Compare(SchedulerOracle real, EffectScheduler ours, ScriptedRandom scripted)
    {
        var lines = new List<string>();

        if (RandRedirect.Position != scripted.Draws)
            lines.Add($"    rand() draws    real {RandRedirect.Position}  ours {scripted.Draws}");
        if (real.CycleCountdown != ours.CycleCountdown)
            lines.Add($"    cycle countdown real {real.CycleCountdown}  ours {ours.CycleCountdown}");

        for (var s = 0; s < SchedulerOracle.Steps.Length; s++)
        {
            var step = ours.Steps[s];
            var realActive = real.StepActive(s);
            var ourActive = step.Active.Select(e => IndexOf(ours, e)).ToArray();
            if (real.StepCountdown(s) != step.Countdown || !realActive.SequenceEqual(ourActive))
                lines.Add($"    step {s}: countdown real {real.StepCountdown(s)} ours {step.Countdown}; " +
                          $"holds real [{string.Join(",", realActive)}] ours [{string.Join(",", ourActive)}]");

            foreach (var index in realActive.Intersect(ourActive))
            {
                var r = real.Envelope(index);
                var effect = ours.Pool[index].Effect;
                var o = (effect.Phase, effect.PhaseCounter, effect.PhaseDuration);
                if (r != o)
                    lines.Add($"    entry {index} envelope (phase, counter, duration) real {r} ours {o}");
            }
        }

        for (var e = 0; e < SchedulerOracle.Pool.Length; e++)
            if (real.Taken(e) != ours.Pool[e].Taken)
                lines.Add($"    entry {e} taken real {real.Taken(e)} ours {ours.Pool[e].Taken}");

        return lines.Count == 0 ? null : string.Join(Environment.NewLine, lines);
    }

    private static int IndexOf(EffectScheduler scheduler, EffectScheduler.PoolEntry entry)
    {
        for (var i = 0; i < scheduler.Pool.Count; i++)
            if (ReferenceEquals(scheduler.Pool[i], entry)) return i;
        return -1;
    }

    private static int ArgInt(string[] args, string name, int fallback)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(args[i + 1], out var v)) return v;
        return fallback;
    }
}
