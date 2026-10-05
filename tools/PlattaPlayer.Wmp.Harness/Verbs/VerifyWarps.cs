using System.Reflection;
using PlattaPlayer.Visualizations.Wmp.Alchemy;
using PlattaPlayer.Visualizations.Wmp.Alchemy.Effects;
using PlattaPlayer.Wmp.Harness.Interop;
using PlattaPlayer.Wmp.Harness.Synth;

namespace PlattaPlayer.Wmp.Harness.Verbs;

/// <summary>
/// Diffs each of our warp kernels against the REAL one, function to function, over every pixel in the
/// field.
///
/// This replaces guessing with proof. Until now Alchemy could only be judged by looking at finished
/// frames, which mixes the audio analysis, the kernel maths, the renderers, the palette and the scheduler
/// together — and because the two sides cannot select the same effects, even that was only impressionistic.
/// A shift kernel, though, is a self-contained object with one function on it, <c>FormShift(int*, int*)</c>,
/// mapping a destination pixel to the source it samples. Given the same parameters, the two
/// implementations must agree on all 307,200 coordinates or one of them is wrong.
///
/// Both sides are driven from ONE integer script — the DLL through <see cref="RandRedirect"/> and our
/// effect through <see cref="ScriptedRandom"/> — so the comparison happens at three separate levels, and
/// the report says which one broke:
/// <list type="number">
/// <item>DRAW COUNT — do the two randomizers consume the same number of <c>rand()</c> values?</item>
/// <item>PARAMETERS — do they produce the same tunables? (A randomizer that reads its fields in a
/// different order consumes the same count and still looks plausible, but every value is wrong.)</item>
/// <item>GEOMETRY — with parameters forced to match, does the warp itself agree?</item>
/// </list>
///
/// <c>--managed-math</c> additionally points the DLL's sin/cos/atan2/sqrt at .NET's
/// (<see cref="MathRedirect"/>). Use it to classify a small residual: if a handful of off-by-one pixels
/// disappear under managed maths, they were the last bit of a double falling either side of a truncation
/// boundary, not a mistake in the formula.
///
/// <b>Vary <c>--seed</c>, not just <c>--sets</c>.</b> This verb reported 4/4 and then 24/24 kernels exact
/// while <c>LinearShift</c> still rounded its sine shear in double where the original rounds in single —
/// a divergence that only appears when the sine lands on a half-integer, which depends on the PARAMETERS
/// and so on the script seed, not on how many consecutive seeds are tried. <c>verify-map</c> found it on
/// its first run purely because it starts from a different base. More sets from the same base explore the
/// same neighbourhood; a different base is what covers new ground.
///
/// Usage: verify-warps [--sets N] [--seed N] [--size WxH] [--kernel linear|snafu|stretch|oscope]
///                     [--managed-math]
/// </summary>
internal static class VerifyWarps
{
    private static readonly ShiftKernelKind[] AllKinds =
        [ShiftKernelKind.Linear, ShiftKernelKind.Snafu, ShiftKernelKind.Stretch, ShiftKernelKind.OScope];

    /// <summary>Generous headroom: the busiest randomizer (OScope) makes about a dozen draws.</summary>
    private const int ScriptLength = 256;

    public static int Run(string[] args)
    {
        var sets = ArgInt(args, "--sets", 4);
        var seed = ArgInt(args, "--seed", 1000);
        var (w, h) = Capture.ArgSize(args, "--size", 640, 480);
        var only = ArgString(args, "--kernel");

        Console.WriteLine(MpvisModule.Describe());
        Console.WriteLine(RandRedirect.SelfTest());
        if (args.Any(a => string.Equals(a, "--managed-math", StringComparison.OrdinalIgnoreCase)))
        {
            MathRedirect.Apply();
            Console.WriteLine("managed maths: the DLL's sin/cos/atan2/sqrt now call .NET's, so any " +
                              "remaining difference is logic rather than a last-bit rounding");
        }
        Console.WriteLine();
        Console.WriteLine($"comparing FormShift over every pixel of {w}x{h} ({w * h:N0} coordinates), " +
                          $"{sets} parameter set(s) per kernel from seed base {seed}");

        var kinds = only is null
            ? AllKinds
            : AllKinds.Where(k => k.ToString().Equals(only, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (kinds.Length == 0)
        {
            Console.Error.WriteLine($"verify-warps: unknown kernel '{only}'.");
            return 2;
        }

        var failures = 0;
        foreach (var kind in kinds)
            if (!VerifyKernel(kind, sets, w, h, seed)) failures++;

        MathRedirect.Restore();
        RandRedirect.Restore();

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "verify-warps: all kernels agree with the real implementation."
            : $"verify-warps: {failures} of {kinds.Length} kernel(s) DIVERGE — see above.");
        return failures == 0 ? 0 : 1;
    }

    private static bool VerifyKernel(ShiftKernelKind kind, int sets, int w, int h, int seed)
    {
        Console.WriteLine();
        Console.WriteLine($"=== {kind} ===");

        var ok = true;
        for (var set = 0; set < sets; set++)
        {
            var script = RandRedirect.MakeScript(seed + set, ScriptLength);
            RandRedirect.SetScript(script);

            // The OScope constructor randomizes, so build first and only then rewind, letting both sides
            // start their comparable randomize call at script[0].
            using var native = ShiftKernelOracle.Create(kind, w, h);
            RandRedirect.Rewind();
            native.Randomize();
            var nativeDraws = RandRedirect.Position;

            var ours = NewEffect(kind);
            var scripted = new ScriptedRandom(script);
            ours.Randomize(scripted);
            var ourDraws = scripted.Draws;

            var ctx = new EffectContext { Width = w, Height = h };
            ours.Tick(ctx);

            Console.WriteLine($"  set {set}: rand() draws  real {nativeDraws}  ours {ourDraws}" +
                              (nativeDraws == ourDraws ? "" : "   <-- MISMATCH"));
            ReportParameters(native, ours);

            var result = Sweep(native, ours, ctx, w, h);
            Console.WriteLine($"           coordinates identical: {result.Matches:N0}/{w * h:N0} " +
                              $"({(double)result.Matches / (w * h):P2})");
            if (result.Matches != (long)w * h)
            {
                Console.WriteLine($"           first divergence at ({result.FirstX},{result.FirstY}): " +
                                  $"real ({result.RealX},{result.RealY})  ours ({result.OurX},{result.OurY})");
                Console.WriteLine($"           max |dx| {result.MaxDx}   max |dy| {result.MaxDy}");
                ok = false;
            }
            if (nativeDraws != ourDraws) ok = false;
        }
        return ok;
    }

    /// <summary>
    /// Prints the real object's tunables beside ours, matched on name. Our fields are read reflectively:
    /// this is a dev-only harness and widening the production API purely so a test can peek at it would
    /// be the wrong trade.
    ///
    /// Shared with <c>verify-map</c>, which needs exactly this when a chained kernel disagrees — the
    /// question there is always "different parameters, or the same parameters and different maths", and
    /// nothing else separates them.
    /// </summary>
    internal static void ReportParameters(ShiftKernelOracle native, AlchemyEffect ours)
    {
        var mine = ours.GetType()
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .ToDictionary(f => Normalize(f.Name), f => ToDouble(f.GetValue(ours)));

        foreach (var (name, real) in native.Parameters())
        {
            var key = Normalize(name);
            var have = mine.TryGetValue(key, out var our);
            var agrees = have && Math.Abs(our - real) < 1e-9;
            var oursText = have ? our.ToString("0.######") : "(not modelled)";
            Console.WriteLine($"           {name,-14} real {real,12:0.######}   ours {oursText,-14} " +
                              (agrees ? "ok" : "<-- differs"));
        }
    }

    private static string Normalize(string name) => name.TrimStart('_').ToLowerInvariant();

    private static double ToDouble(object? value) => value switch
    {
        null => double.NaN,
        bool b => b ? 1 : 0,
        int i => i,
        double d => d,
        float f => f,
        _ => double.NaN,
    };

    private readonly record struct SweepResult(
        long Matches, int FirstX, int FirstY, int RealX, int RealY, int OurX, int OurY, int MaxDx, int MaxDy);

    private static SweepResult Sweep(ShiftKernelOracle native, AlchemyEffect ours, EffectContext ctx, int w, int h)
    {
        long matches = 0;
        int firstX = -1, firstY = -1, realX = 0, realY = 0, ourX = 0, ourY = 0, maxDx = 0, maxDy = 0;

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var (rx, ry) = native.FormShift(x, y);

                int ox = x, oy = y;
                ours.Transform(ctx, ref ox, ref oy);

                if (rx == ox && ry == oy) { matches++; continue; }

                var dx = Math.Abs(rx - ox);
                var dy = Math.Abs(ry - oy);
                if (dx > maxDx) maxDx = dx;
                if (dy > maxDy) maxDy = dy;
                if (firstX < 0)
                {
                    firstX = x; firstY = y;
                    realX = rx; realY = ry;
                    ourX = ox; ourY = oy;
                }
            }
        }
        return new SweepResult(matches, firstX, firstY, realX, realY, ourX, ourY, maxDx, maxDy);
    }

    private static AlchemyEffect NewEffect(ShiftKernelKind kind) => kind switch
    {
        ShiftKernelKind.Linear => new LinearShift(),
        ShiftKernelKind.Snafu => new SnafuShift(),
        ShiftKernelKind.Stretch => new StretchShift(),
        ShiftKernelKind.OScope => new OScopeShift(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    internal static int ArgInt(string[] args, string name, int fallback)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(args[i + 1], out var v)) return v;
        return fallback;
    }

    internal static string? ArgString(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }
}
