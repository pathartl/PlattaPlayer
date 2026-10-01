using System.Runtime.InteropServices;

namespace PlattaPlayer.Wmp.Harness.Interop;

/// <summary>
/// Runs a REAL renderer's whole <c>NormalRender</c> (vtable +0x38) frame after frame, into a surface we
/// own. This is the Tier 3 oracle: every primitive underneath is already exact, so what this measures is
/// the COMPOSITION — which strokes a renderer draws each frame, where, in what colour, and what it
/// advances.
///
/// The render data is a stub that carries exactly what the three renderers read:
/// <list type="bullet">
/// <item><c>+0x08</c> the TimedLevel (the ring reads bass and treble bins straight from it, and every
/// spline samples its waveform);</item>
/// <item><c>+0x1c</c> bass hit, <c>+0x1d</c> beat, <c>+0x28</c> bass, <c>+0x30</c> bass delta — the values
/// <c>SetLevels</c> produces, supplied from our own analysis, which verify-audio proves identical;</item>
/// <item><c>+0x3c</c>/<c>+0x40</c> the field size and <c>+0x48</c> the surface.</item>
/// </list>
/// The surface is the same three-field stub the primitive oracles use: <c>+0x30</c> width,
/// <c>+0x34</c> height and <c>+0xa8</c> bits.
/// </summary>
internal sealed unsafe class NormalRenderOracle : IDisposable
{
    private const int VtNormalRender = 7;

    private readonly RenderEffectOracle _effect;
    private readonly byte* _renderData;
    private readonly byte* _surface;
    private readonly int[] _pixels;
    private GCHandle _pin;

    public NormalRenderOracle(RenderEffectKind kind, int width, int height)
    {
        Width = width;
        Height = height;
        _effect = RenderEffectOracle.Create(kind, width, height);

        _pixels = new int[width * height];
        _pin = GCHandle.Alloc(_pixels, GCHandleType.Pinned);

        _surface = (byte*)NativeMemory.AllocZeroed(0x100);
        *(int*)(_surface + 0x30) = width;
        *(int*)(_surface + 0x34) = height;
        *(void**)(_surface + 0xa8) = (void*)_pin.AddrOfPinnedObject();

        _renderData = (byte*)NativeMemory.AllocZeroed(0x400);
        *(int*)(_renderData + 0x3c) = width;
        *(int*)(_renderData + 0x40) = height;
        *(void**)(_renderData + 0x48) = _surface;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>The surface, BGRX (the X byte is never written by mpvis).</summary>
    public int[] Pixels => _pixels;

    public RenderEffectOracle Effect => _effect;

    public void Randomize() => _effect.Randomize();

    /// <summary>One frame of the real NormalRender with the given audio state.</summary>
    public void Render(void* timedLevel, double bass, double delta, bool beat, bool bassHit)
    {
        *(void**)(_renderData + 0x08) = timedLevel;
        _renderData[0x1c] = bassHit ? (byte)1 : (byte)0;
        _renderData[0x1d] = beat ? (byte)1 : (byte)0;
        *(double*)(_renderData + 0x28) = bass;
        *(double*)(_renderData + 0x30) = delta;

        var obj = _effect.Object;
        var vtable = *(long**)obj;
        ((delegate* unmanaged[Cdecl]<void*, void*, void>)vtable[VtNormalRender])(obj, _renderData);
    }

    public void Dispose()
    {
        _effect.Dispose();
        NativeMemory.Free(_renderData);
        NativeMemory.Free(_surface);
        if (_pin.IsAllocated) _pin.Free();
    }
}
