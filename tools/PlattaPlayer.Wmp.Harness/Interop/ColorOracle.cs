using System.Runtime.InteropServices;

namespace PlattaPlayer.Wmp.Harness.Interop;

/// <summary>
/// The real <c>CTColor</c> helpers, called directly.
///
/// Colour is worth verifying on its own because it is pure and cheap to check, and because a mistake in
/// it is systematic rather than local — every stroke every effect draws is tinted by it, so the whole
/// visualization ends up the wrong hue with nothing to point at. One such bug has already been found and
/// fixed here by eye (channels were clamped to 100..255, which averaged everything toward pale grey);
/// this makes the rest provable instead.
/// </summary>
internal static unsafe class ColorOracle
{
    private const long RandomColorVa = 0x18000b9f8;
    private const long InterpolateVa = 0x18000b518;
    private const long RoundVa = 0x18000bef4;

    /// <summary>
    /// <c>FUN_18000b9f8</c>: three <c>rand()</c> draws, each truncated to a BYTE, assembled as
    /// <c>b3 | b2 &lt;&lt; 16 | b1 &lt;&lt; 8</c>. Note the channel order — the first draw becomes GREEN,
    /// the second RED, the third BLUE.
    /// </summary>
    public static uint RandomColor() =>
        ((delegate* unmanaged[Cdecl]<uint>)MpvisModule.At(RandomColorVa))();

    /// <summary>
    /// <c>FUN_18000b518</c>: per channel <c>from + round((to - from) * t)</c>, with <paramref name="t"/> a
    /// FLOAT and the sum wrapping to a byte rather than clamping.
    /// </summary>
    public static uint Interpolate(uint from, uint to, float t) =>
        ((delegate* unmanaged[Cdecl]<uint, uint, float, uint>)MpvisModule.At(InterpolateVa))(from, to, t);

    /// <summary>
    /// <c>FUN_18000bef4</c>: round half away from zero, computed in FLOAT — <c>(int)(v &gt; 0 ? v + 0.5f
    /// : v - 0.5f)</c>. Every rounded quantity in the effect library goes through this, not through a
    /// double-precision round.
    /// </summary>
    public static int Round(float value) =>
        ((delegate* unmanaged[Cdecl]<float, int>)MpvisModule.At(RoundVa))(value);
}

/// <summary>
/// A live <c>CTColor</c> — the palette cycler that eases between random colours and picks a new target
/// when its period runs out. Constructed with the real constructor because, unlike the shift kernels,
/// its initial state is drawn from <c>rand()</c> and its fields interlock.
///
/// Layout (from <c>FUN_18000aed8</c> / <c>FUN_18000afd4</c> / <c>FUN_18000bf70</c>), as <c>uint[]</c>:
/// <c>[0]</c> current, <c>[1]</c> from, <c>[2]</c> to, <c>[3]</c> counter, <c>[4]</c> period,
/// <c>[5]</c> multi-step flag, <c>[6]</c> step count, <c>[7]</c> enabled, <c>[8]</c> period base (100).
/// </summary>
internal sealed unsafe class ColorCyclerOracle : IDisposable
{
    private const long CtorVa = 0x18000aed8;
    private const long AdvanceVa = 0x18000afd4;
    private const int Size = 0x24;

    private readonly void* _object;

    public ColorCyclerOracle()
    {
        _object = NativeMemory.AllocZeroed(Size);
        ((delegate* unmanaged[Cdecl]<void*, void*>)MpvisModule.At(CtorVa))(_object);
    }

    /// <summary><c>FUN_18000afd4</c>: one frame of the transition.</summary>
    public void Advance() => ((delegate* unmanaged[Cdecl]<void*, void>)MpvisModule.At(AdvanceVa))(_object);

    /// <summary>
    /// <c>FUN_18000bf70</c>: start an explicit transition. With <paramref name="steps"/> above one this
    /// selects the PING-PONG form the stroke renderers use — the period is divided by the step count and
    /// each expiry swaps the endpoints instead of drawing a new random target.
    /// </summary>
    public void Begin(uint from, uint to, int duration, int steps) =>
        ((delegate* unmanaged[Cdecl]<void*, uint, uint, int, int, void>)MpvisModule.At(BeginVa))(
            _object, from, to, duration, steps);

    private const long BeginVa = 0x18000bf70;

    public uint Current => Read(0);

    public uint From => Read(1);

    public uint To => Read(2);

    public int Counter => (int)Read(3);

    public int Period => (int)Read(4);

    private uint Read(int index) => ((uint*)_object)[index];

    public void Dispose() => NativeMemory.Free(_object);
}
