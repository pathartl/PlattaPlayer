using System.Runtime.InteropServices;

namespace PlattaPlayer.Wmp.Harness.Interop;

/// <summary>
/// A frame-driven stand-in for <c>timeGetTime</c>, installed into mpvis.DLL's import table so the real
/// Alchemy can be stepped deterministically.
///
/// Pinning the RNG seed alone was not enough to make Alchemy reproducible: it schedules its effects
/// against the WALL CLOCK (mpvis calls <c>timeGetTime</c> from five places), so two runs at slightly
/// different speeds switch effects at different points. Feeding it a clock that advances by exactly one
/// frame interval per rendered frame removes the last source of run-to-run variation and, as a bonus,
/// lets a capture be stepped at any nominal frame rate.
///
/// This is also a genuine finding about the effect: its lifetimes are in MILLISECONDS, not frames.
/// </summary>
internal static class VirtualClock
{
    private static uint _milliseconds;

    /// <summary>Milliseconds advanced per rendered frame — WMP's own visualization tick.</summary>
    public const uint FrameIntervalMs = 16;

    public static uint Milliseconds => _milliseconds;

    /// <summary>Rewinds to a fixed origin. Call before each capture run so both runs see the same clock.</summary>
    public static void Reset() => _milliseconds = 0;

    public static void AdvanceFrame() => _milliseconds += FrameIntervalMs;

    /// <summary>
    /// The replacement <c>timeGetTime</c>. Must be <c>[UnmanagedCallersOnly]</c> so the address can be
    /// dropped straight into an import slot and called by native code.
    /// </summary>
    [UnmanagedCallersOnly]
    public static uint TimeGetTime() => _milliseconds;

    public static unsafe nint FunctionPointer => (nint)(delegate* unmanaged<uint>)&TimeGetTime;
}
