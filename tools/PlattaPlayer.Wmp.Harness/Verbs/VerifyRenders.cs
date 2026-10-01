using System.Reflection;
using PlattaPlayer.Visualizations.Wmp.Alchemy;
using PlattaPlayer.Visualizations.Wmp.Alchemy.Effects;
using PlattaPlayer.Wmp.Harness.Interop;
using PlattaPlayer.Wmp.Harness.Synth;

namespace PlattaPlayer.Wmp.Harness.Verbs;

/// <summary>
/// Diffs our renderers' RANDOMIZERS against the real ones, the same way <see cref="VerifyWarps"/> does for
/// the shift kernels: one rand() script drives both sides, and the report compares draw counts and then
/// tunables.
///
/// It stops short of the drawing itself on purpose. <c>NormalRender</c> needs a fully built render data
/// with live surfaces, and it draws through primitives — the spline, the chord, the plot — that are their
/// own unverified reconstruction, so a pixel diff before those are checked would only say that something
/// somewhere differs. The randomizers are self-contained, and they are precisely where the shift kernels
/// turned out to be wrong: two of four had correct maths driven by parameters read out of the wrong draws.
///
/// A DRAW-COUNT mismatch is the most valuable line in the output. Several of these randomizers consume a
/// VARIABLE number of values — the ring redraws only when its division count lands on 5 or 6, the balls
/// take a second radius only one time in fifteen, and the rose's ScalePct is a minimum of three draws —
/// so a count that tracks the original across many seeds is strong evidence the control flow matches too.
///
/// Usage: verify-renders [--sets N]
/// </summary>
internal static class VerifyRenders
{
    private static readonly RenderEffectKind[] AllKinds =
    [
        RenderEffectKind.SuperStar, RenderEffectKind.WonderWave,
        RenderEffectKind.AtomBalls, RenderEffectKind.BassBounce,
    ];

    private const int ScriptLength = 256;

    public static int Run(string[] args)
    {
        var sets = ArgInt(args, "--sets", 4);
        const int w = 640;
        const int h = 480;

        Console.WriteLine(MpvisModule.Describe());
        Console.WriteLine(RandRedirect.SelfTest());
        Console.WriteLine();
        Console.WriteLine($"comparing renderer randomizers, {sets} parameter set(s) each");

        var failures = 0;
        foreach (var kind in AllKinds)
        {
            Console.WriteLine();
            Console.WriteLine($"=== {kind} ===");
            var ok = true;

            for (var set = 0; set < sets; set++)
            {
                var script = RandRedirect.MakeScript(2000 + set, ScriptLength);
                RandRedirect.SetScript(script);

                using var native = RenderEffectOracle.Create(kind, w, h);
                RandRedirect.Rewind();
                native.Randomize();
                var nativeDraws = RandRedirect.Position;

                var ours = NewEffect(kind);
                var scripted = new ScriptedRandom(script);
                ours.Randomize(scripted);
                var ourDraws = scripted.Draws;

                var drawsMatch = nativeDraws == ourDraws;
                Console.WriteLine($"  set {set}: rand() draws  real {nativeDraws}  ours {ourDraws}" +
                                  (drawsMatch ? "" : "   <-- MISMATCH"));
                if (!drawsMatch) ok = false;
                if (!ReportParameters(native, ours)) ok = false;
            }

            if (!ok) failures++;
        }

        RandRedirect.Restore();

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "verify-renders: all renderer randomizers agree with the real implementation."
            : $"verify-renders: {failures} of {AllKinds.Length} renderer(s) DIVERGE — see above.");
        return failures == 0 ? 0 : 1;
    }

    private static bool ReportParameters(RenderEffectOracle native, AlchemyEffect ours)
    {
        var mine = ours.GetType()
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .ToDictionary(f => Normalize(f.Name), f => ToDouble(f.GetValue(ours)));

        var ok = true;
        foreach (var (name, real) in native.Parameters())
        {
            var have = mine.TryGetValue(Normalize(name), out var our);
            var agrees = have && Math.Abs(our - real) < 1e-9;
            if (!agrees) ok = false;
            Console.WriteLine($"           {name,-14} real {real,12:0.######}   " +
                              $"ours {(have ? our.ToString("0.######") : "(not modelled)"),-14} " +
                              (agrees ? "ok" : "<-- differs"));
        }
        return ok;
    }

    private static string Normalize(string name)
    {
        var n = name.TrimStart('_').ToLowerInvariant();
        return n switch
        {
            "period" => "period",
            "bounceframe" => "bounceframe",
            "radius1" or "ball1" => "ball1radius",
            "radius2" or "ball2" => "ball2radius",
            _ => n,
        };
    }

    private static double ToDouble(object? value) => value switch
    {
        null => double.NaN,
        bool b => b ? 1 : 0,
        int i => i,
        double d => d,
        float f => f,
        _ => double.NaN,
    };

    private static AlchemyEffect NewEffect(RenderEffectKind kind)
    {
        var seed = new Random(0);
        return kind switch
        {
            RenderEffectKind.SuperStar => new SuperStarRender(seed),
            RenderEffectKind.WonderWave => new WonderWaveRender(seed),
            RenderEffectKind.AtomBalls => new AtomBallsRender(seed),
            RenderEffectKind.BassBounce => new BassBounceZoom(),
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
}
