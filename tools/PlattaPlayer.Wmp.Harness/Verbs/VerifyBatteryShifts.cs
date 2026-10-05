using PlattaPlayer.Visualizations.Wmp.Battery.Shifts;
using PlattaPlayer.Wmp.Harness.Interop;

namespace PlattaPlayer.Wmp.Harness.Verbs;

/// <summary>
/// Diffs each of Battery's 14 warp classes against the real one in wmp.dll, at three levels, for every
/// script set:
/// <list type="number">
/// <item><b>Randomize:</b> the draw count and all eight parameters, bit for bit.</item>
/// <item><b>FormShift:</b> the raw source coordinate for every pixel of the field, with the parameters
/// forced equal. CSwirlShift draws per pixel, so the script keeps running through this pass.</item>
/// <item><b>Setup:</b> the finished gather table after the real Setup has run to completion. This
/// covers the per-axis out-of-range policy and the row walk.</item>
/// </list>
/// Then a <b>transition</b> pass: a table built over a previous one fills 18 intermediate tables, and
/// GetData steps through them, drawing each step's hold time.
///
/// Both sides take one integer script: the DLL through <see cref="RandRedirect"/>, ours through
/// <see cref="ScriptedCrtRand"/>. Vary <c>--seed</c>, not only <c>--sets</c> (see VerifyWarps).
///
/// Usage: verify-battery-shifts [--sets N] [--seed N] [--size WxH] [--class CName] [--managed-math]
/// </summary>
internal static class VerifyBatteryShifts
{
    public static int Run(string[] args)
    {
        var sets = VerifyWarps.ArgInt(args, "--sets", 4);
        var seed = VerifyWarps.ArgInt(args, "--seed", 1000);
        var (w, h) = Capture.ArgSize(args, "--size", 384, 288);
        var only = VerifyWarps.ArgString(args, "--class");

        Console.WriteLine(WmpModule.Describe());
        RandRedirect.ApplyWmp();
        if (args.Contains("--managed-math"))
        {
            MathRedirect.ApplyWmp();
            Console.WriteLine("managed maths: wmp.dll's sin/cos/atan2/sqrt now call .NET's");
        }

        // Construct the real pool under a script too: some render-effect constructors draw.
        RandRedirect.SetScript(RandRedirect.MakeScript(seed - 1, 4096));
        _ = BatteryOracle.RenderData;

        var ours = ShiftTable.CreatePool();
        var failures = 0;
        for (var i = 1; i < BatteryOracle.ShiftPool.Length; i++) // index 0 and 1 are both Linear
        {
            var real = BatteryOracle.Shift(i);
            var mine = ours[i];
            if (only is not null && !string.Equals(only, real.Name, StringComparison.OrdinalIgnoreCase)) continue;
            if (mine.Name != real.Name) throw new InvalidOperationException($"pool {i}: ours {mine.Name} vs {real.Name}");

            var ok = true;
            if (real.Flags != mine.Flags || real.KeepOutOfRange != mine.KeepOutOfRange)
            {
                Console.WriteLine($"  {real.Name}: flags {real.Flags}/{mine.Flags}, keep {real.KeepOutOfRange}/{mine.KeepOutOfRange}");
                ok = false;
            }

            for (var set = 0; set < sets && ok; set++)
                ok = CompareSet(real, mine, w, h, seed + 7919 * set + i);

            if (ok) ok = CompareTransition(real, mine, w, h, seed + 104729 + i);

            Console.WriteLine($"{(ok ? "  ok  " : "  FAIL")} {real.Name}");
            if (!ok) failures++;
        }

        RandRedirect.Restore();
        Console.WriteLine(failures == 0 ? "verify-battery-shifts: ALL EXACT" : $"verify-battery-shifts: {failures} class(es) diverge");
        return failures == 0 ? 0 : 1;
    }

    private static bool CompareSet(BatteryShiftObject real, ShiftTable mine, int w, int h, int scriptSeed)
    {
        var script = RandRedirect.MakeScript(scriptSeed, w * h * 3 + 1024);
        var rand = new ScriptedCrtRand(script);

        real.SetSize(w, h, false);
        mine.SetSize(w, h, false);

        // 1. Randomize.
        RandRedirect.SetScript(script);
        var before = RandRedirect.Calls;
        real.Randomize();
        var realDraws = RandRedirect.Calls - before;
        mine.Randomize(rand);
        if (realDraws != rand.Position)
        {
            Console.WriteLine($"  {real.Name} seed {scriptSeed}: Randomize drew {realDraws}, ours {rand.Position}");
            return false;
        }
        for (var p = 0; p < 8; p++)
        {
            if (BitConverter.DoubleToInt64Bits(real.Param(p)) != BitConverter.DoubleToInt64Bits(mine.P[p]))
            {
                Console.WriteLine($"  {real.Name} seed {scriptSeed}: dbl{p + 1} real {real.Param(p):R} ours {mine.P[p]:R}");
                return false;
            }
        }

        // 2. FormShift over every pixel, continuing the same script.
        var position = RandRedirect.Position;
        var diffs = 0;
        string? first = null;
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            int rx = x, ry = y, mx = x, my = y;
            real.FormShift(ref rx, ref ry);
            mine.FormShift(ref mx, ref my, rand);
            if (rx == mx && ry == my) continue;
            diffs++;
            first ??= $"({x},{y}) -> real ({rx},{ry}) ours ({mx},{my})";
        }
        if (RandRedirect.Position - position != rand.Position - realDraws)
        {
            Console.WriteLine($"  {real.Name} seed {scriptSeed}: FormShift drew {RandRedirect.Position - position}, ours {rand.Position - realDraws}");
            return false;
        }
        if (diffs != 0)
        {
            Console.WriteLine($"  {real.Name} seed {scriptSeed}: FormShift {diffs}/{w * h} differ, first {first}");
            Console.WriteLine($"    params {string.Join(" ", Enumerable.Range(0, 4).Select(i => mine.P[i].ToString("R")))}");
            return false;
        }

        // 3. The real Setup to completion, from a rewound script, against ours.
        RandRedirect.SetScript(script);
        rand.Rewind();
        real.MarkDirty();
        mine.MarkDirty();
        while (!real.IsComplete) real.Setup();
        while (!mine.IsComplete) mine.Setup(rand);
        if (RandRedirect.Position != rand.Position)
        {
            Console.WriteLine($"  {real.Name} seed {scriptSeed}: Setup drew {RandRedirect.Position}, ours {rand.Position}");
            return false;
        }
        var table = real.Table;
        for (var i = 0; i < table.Length; i++)
        {
            if (table[i] == mine.Table![i]) continue;
            Console.WriteLine($"  {real.Name} seed {scriptSeed}: table[{i}] ({i % w},{i / w}) real {table[i]} ours {mine.Table[i]}");
            return false;
        }
        return true;
    }

    /// <summary>Build a table over a previous one (the background path) and step GetData through the
    /// whole transition.</summary>
    private static bool CompareTransition(BatteryShiftObject real, ShiftTable mine, int w, int h, int scriptSeed)
    {
        var script = RandRedirect.MakeScript(scriptSeed, w * h * 4 + 4096);
        var rand = new ScriptedCrtRand(script);

        // The "previous" table is pool slot 0 (a Linear) on each side, finished at the same size.
        var realPrev = BatteryOracle.Shift(0);
        var minePrev = new LinearShift();
        RandRedirect.SetScript(script);
        realPrev.SetSize(w, h, false);
        minePrev.SetSize(w, h, false);
        realPrev.Randomize();
        minePrev.Randomize(rand);
        realPrev.MarkDirty();
        minePrev.MarkDirty();
        while (!realPrev.IsComplete) realPrev.Setup();
        while (!minePrev.IsComplete) minePrev.Setup(rand);

        real.SetSize(0, 0, false);
        mine.SetSize(0, 0, false);
        real.SetSize(w, h, true);
        mine.SetSize(w, h, true);
        real.Randomize();
        mine.Randomize(rand);
        real.MarkDirty();
        mine.MarkDirty();
        real.SetLastShift(realPrev);
        mine.SetLastShift(minePrev);
        while (!real.IsComplete) real.Setup();
        while (!mine.IsComplete) mine.Setup(rand);

        for (var t = 0; t < ShiftTable.TransitionCount; t++)
        {
            var a = real.Transition(t);
            var b = mine.TransitionTable(t)!;
            for (var i = 0; i < a.Length; i++)
            {
                if (a[i] == b[i]) continue;
                Console.WriteLine($"  {real.Name}: transition {t} [{i}] real {a[i]} ours {b[i]}");
                return false;
            }
        }

        // GetData steps: which table is returned each frame, and the hold-time draws.
        for (var frame = 0; frame < 400; frame++)
        {
            real.GetData();
            var mineTable = mine.GetData(rand);
            if (real.TransitionIndex != mine.TransitionIndex || real.TransitionFrames != mine.TransitionFrames ||
                RandRedirect.Position != rand.Position)
            {
                Console.WriteLine($"  {real.Name}: GetData frame {frame}: real idx {real.TransitionIndex} frames {real.TransitionFrames} " +
                                  $"ours idx {mine.TransitionIndex} frames {mine.TransitionFrames}");
                return false;
            }
            if (mine.TransitionIndex >= ShiftTable.TransitionCount && mineTable == mine.Table) break;
        }
        return true;
    }
}
