using System.Runtime.InteropServices;

namespace PlattaPlayer.Wmp.Harness.Interop;

/// <summary>
/// Calls mpvis's DRAWING primitives directly, so the stroke geometry can be verified the same way the
/// warp kernels were rather than inferred from pictures.
///
/// This works because the primitives are far less entangled than they look. <c>FUN_18000b3cc</c> (the
/// per-point offset) reads nothing but a small parameter block and the TimedLevel buffer, so it is a pure
/// function we can hand our own inputs. <c>FUN_18000b730</c> (plot) is given a PIXEL POINTER by its caller
/// and only consults the surface object for a stride and a height — so a fake surface of two integers and
/// a pointer into our own array is enough to make it draw for us.
///
/// The parameter block layout comes from the disassembly of <c>FUN_18000b3cc</c> (Ghidra declares it
/// <c>void</c> and drops the float return, which is why it was previously mistaken for a bare parametric
/// sine).
/// </summary>
internal sealed unsafe class SplineOffsetOracle : IDisposable
{
    private const long OffsetVa = 0x18000b3cc;

    /// <summary>Size of the parameter block; the pointer at +0x18 makes it 0x20 bytes.</summary>
    private const int BlockSize = 0x20;

    private readonly void* _block;

    public SplineOffsetOracle() => _block = NativeMemory.AllocZeroed(BlockSize);

    /// <summary>Number of points across the spline — <c>p[0]</c>.</summary>
    public int Steps { get => Read(0x00); set => Write(0x00, value); }

    /// <summary>Displacement in pixels at full deflection — <c>p[1]</c>.</summary>
    public int Amplitude { get => Read(0x04); set => Write(0x04, value); }

    /// <summary>Fold the index back on itself past the halfway point — <c>p[2]</c>, a byte.</summary>
    public bool Mirror
    {
        get => *((byte*)_block + 0x08) != 0;
        set => *((byte*)_block + 0x08) = (byte)(value ? 1 : 0);
    }

    /// <summary>0 = waveform ch0, 1 = waveform ch1, 2 = the mean of both, 3 = a parametric sine.</summary>
    public int Source { get => Read(0x0c); set => Write(0x0c, value); }

    /// <summary>Selects how the sample index is derived — <c>p[4]</c>.</summary>
    public int IndexMode { get => Read(0x10); set => Write(0x10, value); }

    /// <summary>Lobe count for the parametric-sine source — <c>p[5]</c>.</summary>
    public int SinLoops { get => Read(0x14); set => Write(0x14, value); }

    /// <summary>The TimedLevel block the waveform is sampled from — <c>p[6]</c>.</summary>
    public void* TimedLevel
    {
        get => *(void**)((byte*)_block + 0x18);
        set => *(void**)((byte*)_block + 0x18) = value;
    }

    /// <summary>The real offset at point <paramref name="index"/>, in pixels.</summary>
    public float At(int index) =>
        ((delegate* unmanaged[Cdecl]<void*, int, float>)MpvisModule.At(OffsetVa))(_block, index);

    private int Read(int offset) => *(int*)((byte*)_block + offset);

    private void Write(int offset, int value) => *(int*)((byte*)_block + offset) = value;

    public void Dispose() => NativeMemory.Free(_block);
}
