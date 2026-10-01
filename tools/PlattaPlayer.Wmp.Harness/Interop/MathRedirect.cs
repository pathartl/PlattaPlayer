using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PlattaPlayer.Wmp.Harness.Interop;

/// <summary>
/// Points mpvis.DLL's transcendental maths at .NET's, so a comparison against our port measures LOGIC and
/// not floating-point.
///
/// WHY THIS IS WORTH DOING. Once a kernel's parameters match exactly, any residual difference in its
/// output is one of two completely different things: a real mistake in the formula, or the last bit of a
/// double landing either side of a truncation boundary. They are indistinguishable in the result — a
/// handful of pixels off by one looks the same either way — and they call for opposite responses. Chasing
/// an ULP is wasted effort; leaving a genuine formula bug in place is not.
///
/// Redirecting <c>sin</c>, <c>cos</c>, <c>atan2</c> and <c>sqrt</c> to <see cref="Math"/> removes the
/// first possibility entirely: both implementations then evaluate the same functions with the same
/// results, so whatever is left over is ours. It uses the same single-pointer import redirect as
/// <see cref="RandRedirect"/> — mpvis calls all four through the CRT forwarder table.
///
/// This is a MEASURING mode, not the default. A verification run should normally use the DLL's own maths,
/// because that is what the real effect actually computes; turn this on to classify a residual.
/// </summary>
internal static unsafe class MathRedirect
{
    private const string CrtModule = "api-ms-win-crt-private-l1-1-0.dll";

    private static bool _applied;

    public static bool IsApplied => _applied;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static double Sin(double x) => Math.Sin(x);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static double Cos(double x) => Math.Cos(x);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static double Atan2(double y, double x) => Math.Atan2(y, x);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static double Sqrt(double x) => Math.Sqrt(x);

    public static void Apply()
    {
        if (_applied) return;
        MpvisModule.AssertKnownBuild();

        Redirect("_o_sin", (nint)(delegate* unmanaged[Cdecl]<double, double>)&Sin);
        Redirect("_o_cos", (nint)(delegate* unmanaged[Cdecl]<double, double>)&Cos);
        Redirect("_o_atan2", (nint)(delegate* unmanaged[Cdecl]<double, double, double>)&Atan2);
        Redirect("_o_sqrt", (nint)(delegate* unmanaged[Cdecl]<double, double>)&Sqrt);

        _applied = true;
    }

    private static void Redirect(string export, nint replacement)
    {
        if (!ImportPatch.Redirect(MpvisModule.Handle, CrtModule, export, replacement))
            throw new InvalidOperationException($"mpvis.DLL does not import {export} from {CrtModule}.");
    }

    /// <summary>
    /// Restores every import this process redirected. Note <see cref="ImportPatch"/> keeps ONE list, so
    /// this also restores <see cref="RandRedirect"/> — both are reset together at the end of a run.
    /// </summary>
    public static void Restore()
    {
        if (!_applied) return;
        ImportPatch.RestoreAll();
        _applied = false;
    }
}
