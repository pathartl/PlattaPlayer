using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PlattaPlayer.Wmp.Harness.Interop;

/// <summary>
/// Puts mpvis.DLL's <c>rand()</c> under our control, which is what makes the real Alchemy a
/// controllable oracle rather than only a reproducible one.
///
/// THE PROBLEM IT SOLVES. Alchemy chooses everything at random: which shift kernel the warp stage runs,
/// which renderers the slots accept, and every tunable inside them. It exposes no way to ask for a
/// particular one, and mpvis links its own CRT so its stream cannot be seeded from this process (the
/// <c>randprobe</c> verb demonstrates that). <see cref="AlchemySeedPatch"/> made a run REPEATABLE by
/// pinning the constructor's seed, but repeatable is not the same as selectable — you still get whatever
/// that seed happens to pick, which is why isolating a single effect meant sweeping seeds and hunting
/// through the pictures.
///
/// THE MECHANISM. Every one of the 19 <c>rand()</c> call sites compiles to
/// <c>call qword ptr [rip + …]</c> through a single import slot — <c>_o_rand</c>, imported from
/// api-ms-win-crt-private-l1-1-0.dll. So this is not a code patch at all: it is one pointer, redirected
/// with the same <see cref="ImportPatch"/> used for <c>timeGetTime</c>, and restored just as easily.
/// With it in place a scripted integer sequence drives every random decision the DLL makes, so a kernel
/// or a renderer can be asked for BY NAME and given exact parameters.
///
/// SAFETY. Only the image mapped into this dev-only harness is touched, never the file on disk; the
/// original pointer is restored on <see cref="Restore"/>. The callback is trivial and cannot throw, so it
/// cannot unwind into native frames.
/// </summary>
internal static unsafe class RandRedirect
{
    /// <summary>The CRT forwarder DLL mpvis imports <c>_o_rand</c> from.</summary>
    private const string CrtModule = "api-ms-win-crt-private-l1-1-0.dll";

    private const string CrtExport = "_o_rand";

    /// <summary>C's <c>RAND_MAX</c>. mpvis divides by this to make its 0..1 fractions.</summary>
    public const int RandMax = 32767;

    private static int[] _script = [];
    private static int _index;
    private static long _calls;
    private static bool _applied;
    private static bool _appliedWmp;

    /// <summary>Total calls since <see cref="Apply"/> — a cheap check that the redirect is live.</summary>
    public static long Calls => _calls;

    /// <summary>How far into the script the next draw will come from.</summary>
    public static int Position => _index;

    public static bool IsApplied => _applied;

    /// <summary>
    /// The replacement <c>rand()</c>. Returns successive script values, wrapping if the DLL asks for more
    /// than were supplied — wrapping rather than throwing because this runs on a native stack where an
    /// exception would be undefined behaviour.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Rand()
    {
        _calls++;
        if (_script.Length == 0) return 0;
        var value = _script[_index % _script.Length];
        _index++;
        return value;
    }

    /// <summary>Redirects mpvis.DLL's rand() (Alchemy).</summary>
    public static void Apply()
    {
        if (_applied) return;
        MpvisModule.AssertKnownBuild();
        Redirect(MpvisModule.Handle, "mpvis.DLL");
        _applied = true;
    }

    /// <summary>
    /// Redirects wmp.dll's rand() (Battery, Bars and Waves). wmp.dll imports <c>_o_rand</c> from the same
    /// CRT forwarder as mpvis, so the mechanism is identical; only the image differs. Independent of
    /// <see cref="Apply"/>: both may be live at once and share one script.
    /// </summary>
    public static void ApplyWmp()
    {
        if (_appliedWmp) return;
        WmpModule.AssertKnownBuild();
        Redirect(WmpModule.Handle, "wmp.dll");
        _appliedWmp = true;
    }

    private static void Redirect(nint module, string name)
    {
        var replacement = (nint)(delegate* unmanaged[Cdecl]<int>)&Rand;
        if (!ImportPatch.Redirect(module, CrtModule, CrtExport, replacement))
            throw new InvalidOperationException(
                $"{name} does not import {CrtExport} from {CrtModule}. Imports present: " +
                ImportPatch.Describe(module, CrtModule));
    }

    public static void Restore()
    {
        if (!_applied && !_appliedWmp) return;
        ImportPatch.RestoreAll();
        _applied = false;
        _appliedWmp = false;
    }

    /// <summary>Installs a script and rewinds to its start.</summary>
    public static void SetScript(int[] script)
    {
        _script = script;
        _index = 0;
    }

    /// <summary>Rewinds to the start of the current script, so two runs see identical draws.</summary>
    public static void Rewind() => _index = 0;

    /// <summary>
    /// A script of <paramref name="count"/> values in <c>0..RAND_MAX</c>, which is the range real
    /// <c>rand()</c> returns and therefore the range every consumer's arithmetic is written against.
    /// </summary>
    public static int[] MakeScript(int seed, int count)
    {
        var random = new Random(seed);
        var script = new int[count];
        for (var i = 0; i < count; i++) script[i] = random.Next(RandMax + 1);
        return script;
    }

    /// <summary>
    /// Proves the redirected slot really is <c>rand()</c>, rather than trusting the import name.
    ///
    /// The test drives a known consumer and checks its arithmetic: <c>FUN_18000ebe0</c> (CTShiftLinear's
    /// randomizer) sets XShift to <c>rand() % 7 - 3</c> and YShift to the next draw's, so feeding two
    /// chosen values must produce exactly those two shifts. A constant script would not do — the
    /// randomizer loops while both shifts are zero, so a value with <c>v % 7 == 3</c> would hang it —
    /// which is itself a useful reminder that these functions are not pure.
    /// </summary>
    public static string SelfTest()
    {
        Apply();

        int[] script = [11, 25, 0, 0, 0, 0, 0, 0];
        SetScript(script);

        using var kernel = ShiftKernelOracle.Create(ShiftKernelKind.Linear, 640, 480);
        Rewind();
        var before = _calls;
        kernel.Randomize();

        var expectedX = script[0] % 7 - 3;
        var expectedY = script[1] % 7 - 3;
        var actualX = kernel.ReadInt32(0x60);
        var actualY = kernel.ReadInt32(0x64);

        if (actualX != expectedX || actualY != expectedY)
            throw new InvalidOperationException(
                $"rand() redirect self-test FAILED: CTShiftLinear's randomizer produced shifts " +
                $"({actualX}, {actualY}) where the script implies ({expectedX}, {expectedY}). The slot " +
                "being redirected is not rand(), or the field offsets are wrong. Refusing to trust it.");

        return $"rand() redirect verified: {_calls - before} draws consumed by CTShiftLinear's randomizer, " +
               $"shifts ({actualX}, {actualY}) match the script";
    }
}
