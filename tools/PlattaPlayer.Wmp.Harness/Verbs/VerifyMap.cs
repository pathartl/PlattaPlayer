using PlattaPlayer.Visualizations.Wmp.Alchemy;
using PlattaPlayer.Visualizations.Wmp.Alchemy.Effects;
using PlattaPlayer.Wmp.Harness.Interop;
using PlattaPlayer.Wmp.Harness.Synth;

namespace PlattaPlayer.Wmp.Harness.Verbs;

/// <summary>
/// Diffs <see cref="WarpMap.Build"/> against the REAL <c>FUN_18000d354</c>.
///
/// <c>verify-warps</c> already proves each kernel's <c>FormShift</c> exact over every pixel, so this is
/// deliberately NOT about the maths. It is about the assembly around it, which is where a plausible
/// reading and the truth diverge quietly:
/// <list type="bullet">
/// <item>the second kernel is applied to the FIRST one's output, not independently to (x,y);</item>
/// <item>its out-of-range fallback restores to the first kernel's output, not to the incoming pixel;</item>
/// <item>but the final blend averages against the ORIGINAL x and y;</item>
/// <item>and each kernel's fallback is its own — a cleared flag at +0x52 sends escapees to a fixed
/// coordinate, which for every kernel in this build is the black pixel at the origin.</item>
/// </list>
/// Get any of those the wrong way round and the map is still a perfectly reasonable-looking warp.
///
/// Both sides are driven from ONE rand() script, as <c>verify-warps</c> does, so the two kernels really do
/// hold the same parameters. The verb also reads each real kernel's +0x52/+0x54/+0x58 back and checks our
/// <c>OutOfRangeToOrigin</c> against it, since a wrong policy shows up only on escaping pixels.
///
/// Usage: verify-map [--sets N] [--size WxH] [--verbose]
/// </summary>
internal static class VerifyMap
{
    private static readonly ShiftKernelKind[] AllKinds =
        [ShiftKernelKind.Linear, ShiftKernelKind.Snafu, ShiftKernelKind.Stretch, ShiftKernelKind.OScope];

    private const int ScriptLength = 256;

    public static int Run(string[] args)
    {
        var sets = ArgInt(args, "--sets", 2);
        var (w, h) = Capture.ArgSize(args, "--size", 160, 120);
        var verbose = args.Any(a => string.Equals(a, "--verbose", StringComparison.OrdinalIgnoreCase));

        Console.WriteLine(MpvisModule.Describe());
        Console.WriteLine(RandRedirect.SelfTest());
        Console.WriteLine();
        Console.WriteLine($"comparing warp-map assembly over {w}x{h} ({w * h:N0} entries), {sets} set(s) per pairing");
        Console.WriteLine();

        var checks = 0;
        var failures = 0;

        // Every kernel alone, then every kernel chained through a second — the stage picks its ~1-in-5
        // second kernel from the same uniform pool, so any pairing is reachable.
        var pairings = AllKinds.Select(a => (A: a, B: (ShiftKernelKind?)null))
            .Concat(from a in AllKinds from b in AllKinds select (A: a, B: (ShiftKernelKind?)b));

        foreach (var (kindA, kindB) in pairings)
        for (var set = 0; set < sets; set++)
        {
            checks++;
            var label = kindB is null ? $"{kindA}" : $"{kindA} + {kindB}";

            var script = RandRedirect.MakeScript(7000 + set, ScriptLength);
            RandRedirect.SetScript(script);

            // Construct first (the OScope constructor randomizes), then rewind so both sides start their
            // comparable randomize calls at script[0] and consume it in the same order.
            using var nativeA = ShiftKernelOracle.Create(kindA, w, h);
            using var nativeB = kindB is null ? null : ShiftKernelOracle.Create(kindB.Value, w, h);
            RandRedirect.Rewind();
            nativeA.Randomize();
            nativeB?.Randomize();

            var scripted = new ScriptedRandom(script);
            var ourA = NewEffect(kindA);
            ourA.Randomize(scripted);
            AlchemyEffect? ourB = null;
            if (kindB is not null)
            {
                ourB = NewEffect(kindB.Value);
                ourB.Randomize(scripted);
            }

            var ctx = new EffectContext { Width = w, Height = h };
            ourA.Tick(ctx);
            ourB?.Tick(ctx);

            var policyProblem = CheckPolicy(nativeA, ourA, kindA) ?? (ourB is null || nativeB is null
                ? null
                : CheckPolicy(nativeB, ourB, kindB!.Value));

            using var oracle = new WarpMapOracle(w, h);
            oracle.Build(nativeA, nativeB);

            var ours = new WarpMap();
            ours.Resize(w, h);
            ours.Build(ctx, ourA, ourB);

            var (diff, first) = Compare(oracle.Map, ours.Map);

            if (diff == 0 && policyProblem is null)
            {
                if (verbose) Console.WriteLine($"  set {set} {label,-22} ok");
                continue;
            }

            failures++;
            Console.WriteLine($"  set {set} {label,-22} DIFF {diff,8}/{w * h}");
            if (policyProblem is not null) Console.WriteLine($"      {policyProblem}");
            if (first >= 0) Trace(first, w, h, oracle, ours, nativeA, nativeB, ourA, ourB, ctx);
        }

        RandRedirect.Restore();

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? $"verify-map: {checks}/{checks} cases exact — chaining, fallbacks, blend and row table all match."
            : $"verify-map: {failures} of {checks} cases DIVERGE.");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Replays the first differing destination one stage at a time, calling the REAL kernels' FormShift
    /// directly beside ours. That is what distinguishes "the chaining is wired wrong" from "a kernel
    /// disagrees at this particular coordinate" — the two produce the same final index and nothing else
    /// tells them apart.
    /// </summary>
    private static void Trace(int index, int w, int h, WarpMapOracle oracle, WarpMap ours,
                              ShiftKernelOracle nativeA, ShiftKernelOracle? nativeB,
                              AlchemyEffect ourA, AlchemyEffect? ourB, EffectContext ctx)
    {
        var x = index % w;
        var y = index / w;
        Console.WriteLine($"      dest ({x},{y}): real index {oracle.Map[index]} " +
                          $"({oracle.Map[index] % w},{oracle.Map[index] / w})  " +
                          $"ours {ours.Map[index]} ({ours.Map[index] % w},{ours.Map[index] / w})");

        var (rax, ray) = nativeA.FormShift(x, y);
        int oax = x, oay = y;
        ourA.Transform(ctx, ref oax, ref oay);
        Console.WriteLine($"        stage A raw   real ({rax},{ray})   ours ({oax},{oay})");

        var (aToOrigin, _, _) = WarpMapOracle.Policy(nativeA);
        var rrx = Resolve(rax, x, w, aToOrigin);
        var rry = Resolve(ray, y, h, aToOrigin);
        Console.WriteLine($"        stage A fixed real ({rrx},{rry})   toOrigin={aToOrigin}");

        if (nativeB is null || ourB is null) return;

        var (rbx, rby) = nativeB.FormShift(rrx, rry);
        int obx = rrx, oby = rry;
        ourB.Transform(ctx, ref obx, ref oby);
        Console.WriteLine($"        stage B raw   real ({rbx},{rby})   ours ({obx},{oby})");

        var (bToOrigin, _, _) = WarpMapOracle.Policy(nativeB);
        Console.WriteLine($"        stage B fixed real ({Resolve(rbx, rrx, w, bToOrigin)}," +
                          $"{Resolve(rby, rry, h, bToOrigin)})   toOrigin={bToOrigin}");

        if (rbx == obx && rby == oby) return;
        Console.WriteLine("        stage B parameters:");
        VerifyWarps.ReportParameters(nativeB, ourB);
    }

    private static int Resolve(int value, int incoming, int limit, bool toOrigin)
        => (uint)value < (uint)limit ? value : toOrigin ? 0 : incoming;

    /// <summary>
    /// Compares our modelled out-of-range policy against the flag the real constructor actually set. This
    /// only affects pixels whose source escapes the field, so on a kernel that happens not to escape it
    /// would pass the map comparison while still being wrong.
    /// </summary>
    private static string? CheckPolicy(ShiftKernelOracle native, AlchemyEffect ours, ShiftKernelKind kind)
    {
        var (toOrigin, fixedX, fixedY) = WarpMapOracle.Policy(native);
        if (toOrigin == ours.OutOfRangeToOrigin && (!toOrigin || (fixedX == 0 && fixedY == 0))) return null;
        return $"{kind} out-of-range policy: real toOrigin={toOrigin} fixed=({fixedX},{fixedY}), " +
               $"ours toOrigin={ours.OutOfRangeToOrigin}";
    }

    private static (int Diff, int First) Compare(int[] real, int[] ours)
    {
        var diff = 0;
        var first = -1;
        for (var i = 0; i < real.Length; i++)
        {
            if (real[i] == ours[i]) continue;
            diff++;
            if (first < 0) first = i;
        }
        return (diff, first);
    }

    private static AlchemyEffect NewEffect(ShiftKernelKind kind) => kind switch
    {
        ShiftKernelKind.Linear => new LinearShift(),
        ShiftKernelKind.Snafu => new SnafuShift(),
        ShiftKernelKind.Stretch => new StretchShift(),
        ShiftKernelKind.OScope => new OScopeShift(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static int ArgInt(string[] args, string name, int fallback)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(args[i + 1], out var v)) return v;
        return fallback;
    }
}
