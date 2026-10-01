using System.Runtime.InteropServices;

namespace PlattaPlayer.Wmp.Harness.Interop;

/// <summary>
/// Draws with the REAL soft disc (<c>FUN_18000fbc4</c>) into a buffer we own.
///
/// The disc is the entire visual content of the atom balls and had never been verified at all — our
/// version was a reconstruction that happened to look like a soft dot. It is an easy function to oracle
/// because it reads very little: the object's own width/height and three colour fields, plus the render
/// data purely to reach a surface. So the render data can be two pointers' worth of zeroed memory with
/// the surface hung at +0x48, and the surface itself carries only a stride and a pixel pointer.
///
/// The object still has to be REAL, though — it is built by the atom balls' own constructor so that any
/// field the disc reads but we have not thought about holds whatever the original would have put there.
/// </summary>
internal sealed unsafe class DiscOracle : IDisposable
{
    private const long DiscVa = 0x18000fbc4;

    /// <summary>The atom-balls renderer (name id 113).</summary>
    private const long AtomBallsCtorVa = 0x18000fa0c;

    private const int AtomBallsSize = 0x218;

    // Object fields the disc reads. Width and height are the CLIP bounds; the pixel index comes from the
    // surface's own stride, so a test that sets them differently would be measuring two things at once.
    private const int ObjWidth = 0x08;

    private const int ObjHeight = 0x0c;

    private const int ObjFill = 0xf0;

    private const int ObjRim = 0x114;

    private const int ObjInvert = 0x138;

    // Stub render data: the disc dereferences +0x48 and nothing else.
    private const int RenderDataSize = 0x80;

    private const int RenderDataSurface = 0x48;

    private const int SurfaceSize = 0x100;

    private const int SurfaceWidth = 0x30;

    private const int SurfaceHeight = 0x34;

    private const int SurfaceBits = 0xa8;

    private readonly void* _obj;
    private readonly void* _surface;
    private readonly void* _renderData;
    private readonly int[] _pixels;
    private GCHandle _pixelPin;

    public int Width { get; }

    public int Height { get; }

    public DiscOracle(int width, int height)
    {
        Width = width;
        Height = height;
        _pixels = new int[width * height];
        _pixelPin = GCHandle.Alloc(_pixels, GCHandleType.Pinned);

        _obj = NativeMemory.AllocZeroed(AtomBallsSize);
        ((delegate* unmanaged[Cdecl]<void*, void*>)MpvisModule.At(AtomBallsCtorVa))(_obj);

        _surface = NativeMemory.AllocZeroed(SurfaceSize);
        *(int*)((byte*)_surface + SurfaceWidth) = width;
        *(int*)((byte*)_surface + SurfaceHeight) = height;
        *(void**)((byte*)_surface + SurfaceBits) = (void*)_pixelPin.AddrOfPinnedObject();

        _renderData = NativeMemory.AllocZeroed(RenderDataSize);
        *(void**)((byte*)_renderData + RenderDataSurface) = _surface;

        *(int*)((byte*)_obj + ObjWidth) = width;
        *(int*)((byte*)_obj + ObjHeight) = height;
    }

    public int[] Pixels => _pixels;

    /// <summary>Core colour, at the object's +0xf0. Complemented when <see cref="Invert"/> is set.</summary>
    public uint Fill
    {
        get => *(uint*)((byte*)_obj + ObjFill);
        set => *(uint*)((byte*)_obj + ObjFill) = value;
    }

    /// <summary>Rim-band colour, at +0x114. NOT affected by the invert flag.</summary>
    public uint Rim
    {
        get => *(uint*)((byte*)_obj + ObjRim);
        set => *(uint*)((byte*)_obj + ObjRim) = value;
    }

    /// <summary>+0x138, toggled by every bass hit — it one's-complements the fill.</summary>
    public bool Invert
    {
        get => *((byte*)_obj + ObjInvert) != 0;
        set => *((byte*)_obj + ObjInvert) = (byte)(value ? 1 : 0);
    }

    /// <summary>
    /// Runs the real disc. The radius is an INT — the caller computes it as
    /// <c>(int)(currentRadius * bass) + 1</c> — and the alpha is the live bass, or a flat 0.4 on a bass hit.
    /// </summary>
    public void Draw(int cx, int cy, int radius, float alpha)
    {
        var fn = (delegate* unmanaged[Cdecl]<void*, int, int, int, float, void*, void>)MpvisModule.At(DiscVa);
        fn(_obj, cx, cy, radius, alpha, _renderData);
    }

    public void Dispose()
    {
        if (_pixelPin.IsAllocated) _pixelPin.Free();
        NativeMemory.Free(_renderData);
        NativeMemory.Free(_surface);
        NativeMemory.Free(_obj);
    }
}
