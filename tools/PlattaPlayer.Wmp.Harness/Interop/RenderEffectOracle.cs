using System.Runtime.InteropServices;

namespace PlattaPlayer.Wmp.Harness.Interop;

internal enum RenderEffectKind
{
    /// <summary>Name resource 107, ctor FUN_18000f8ec, NormalRender FUN_18000fe50 — the spinning ring.</summary>
    SuperStar,

    /// <summary>Name resource 108, ctor FUN_18000f960, NormalRender FUN_180010120 — the chord rose.</summary>
    WonderWave,

    /// <summary>Name resource 113, ctor FUN_18000fa0c, NormalRender FUN_180010380.</summary>
    AtomBalls,

    /// <summary>Name resource 115, ctor FUN_1800111bc, NormalRender FUN_180011200.</summary>
    BassBounce,
}

/// <summary>
/// Constructs a REAL mpvis render effect and drives its randomizer, so our renderers' tunables can be
/// diffed against the originals the same way the shift kernels were.
///
/// Deliberately stops short of <c>NormalRender</c> (vtable +0x38). That needs a fully built
/// <c>CToleranceRenderData</c> with live surfaces, and the drawing itself goes through primitives —
/// the spline, the chord and the plot — that are their own unverified reconstruction. Comparing pixels
/// before those are checked would only tell us that something, somewhere, differs. The randomizers, by
/// contrast, are self-contained, and they are exactly where the shift kernels turned out to be broken:
/// two of the four had the right maths and the wrong draw order, which no picture could ever have shown.
///
/// THIS CLASS IS ALSO HOW THE RENDERER IDENTITIES WERE SETTLED. The name resource at +0x34 and the
/// NormalRender slot at +0x38 together say which class is which, and they disagreed with the port: the
/// object named "SuperStar" renders <c>FUN_18000fe50</c> (a ring), not the chord rose it had been matched
/// to by eye.
/// </summary>
internal sealed unsafe class RenderEffectOracle : IDisposable
{
    // ctor VA, object size, expected NormalRender (vtable +0x38), expected name resource id (+0x34).
    private static readonly (long Ctor, int Size, long NormalRender, int NameId)[] Table =
    [
        (0x18000f8ec, 0x1a8, 0x18000fe50, 107), // SuperStar
        (0x18000f960, 0x178, 0x180010120, 108), // WonderWave
        (0x18000fa0c, 0x218, 0x180010380, 113), // AtomBalls
        (0x1800111bc, 0x070, 0x180011200, 115), // BassBounce
    ];

    private const int VtResize = 3;      // +0x18
    private const int VtRandomize = 5;   // +0x28
    private const int VtNormalRender = 7;// +0x38

    private readonly void* _object;
    private readonly RenderEffectKind _kind;

    private RenderEffectOracle(RenderEffectKind kind, void* obj)
    {
        _kind = kind;
        _object = obj;
    }

    public static RenderEffectOracle Create(RenderEffectKind kind, int width, int height)
    {
        var (ctorVa, size, expectedRender, expectedName) = Table[(int)kind];

        var obj = NativeMemory.AllocZeroed((nuint)size);
        ((delegate* unmanaged[Cdecl]<void*, void*>)MpvisModule.At(ctorVa))(obj);

        var oracle = new RenderEffectOracle(kind, obj);

        var vtable = *(long**)obj;
        var render = vtable[VtNormalRender] - MpvisModule.Handle + MpvisModule.ImageBase;
        var nameId = oracle.ReadInt32(0x34);
        if (render != expectedRender || nameId != expectedName)
            throw new InvalidOperationException(
                $"{kind}: constructed object renders 0x{render:X} with name id {nameId}, expected " +
                $"0x{expectedRender:X} / {expectedName}. Wrong constructor address or wrong build.");

        ((delegate* unmanaged[Cdecl]<void*, int, int, void>)vtable[VtResize])(obj, width, height);
        return oracle;
    }

    public void Randomize()
    {
        var vtable = *(long**)_object;
        ((delegate* unmanaged[Cdecl]<void*, void>)vtable[VtRandomize])(_object);
    }

    /// <summary>The native object, for oracles that wire it into larger real structures.</summary>
    public void* Object => _object;

    public int ReadInt32(int offset) => *(int*)((byte*)_object + offset);

    public double ReadDouble(int offset) => *(double*)((byte*)_object + offset);

    public bool ReadBool(int offset) => *((byte*)_object + offset) != 0;

    /// <summary>Tunables at the offsets each randomizer writes, named.</summary>
    public IEnumerable<(string Name, double Value)> Parameters() => _kind switch
    {
        // FUN_180010760 — the ring. Three draws, or four when Divisions lands on 5 or 6.
        RenderEffectKind.SuperStar =>
        [
            ("Scale", ReadDouble(0x98)), ("Divisions", ReadInt32(0xa8)), ("MaxSpin", ReadDouble(0xa0)),
        ],
        // FUN_180010860 — the chord rose. Fourteen draws: note ScalePct comes from FUN_18000a29c
        // (min of THREE draws from 0..2), so it alone consumes three.
        RenderEffectKind.WonderWave =>
        [
            ("RenderMode", ReadInt32(0x50)), ("SinLoops", ReadInt32(0x54)), ("ScalePct", ReadInt32(0x58)),
            ("Subdivisions", ReadInt32(0x5c)), ("Spin", ReadDouble(0x60)), ("SpinDelta", ReadDouble(0x68)),
            ("SpinMode", ReadInt32(0x70)), ("Mirrored", ReadBool(0x74) ? 1 : 0),
            ("CrossLine", ReadBool(0x75) ? 1 : 0), ("CrossMode", ReadBool(0x76) ? 1 : 0),
            ("BassFlex", ReadInt32(0x78)), ("Phase", ReadDouble(0x128)),
        ],
        // FUN_180010a30. Ball2 is independent only 1 time in 15, otherwise it copies Ball1.
        RenderEffectKind.AtomBalls =>
        [
            ("PosX", ReadDouble(0x50)), ("PosY", ReadDouble(0x58)),
            ("VelX", ReadDouble(0x60)), ("VelY", ReadDouble(0x68)),
            ("Ball1Radius", ReadDouble(0xe0)), ("Ball2Radius", ReadDouble(0xe8)),
            ("Damp", ReadDouble(0x168)),
        ],
        // FUN_180011440
        RenderEffectKind.BassBounce =>
        [
            ("Hover", ReadDouble(0x50)), ("BounceFrame", ReadDouble(0x58)), ("Period", ReadInt32(0x60)),
        ],
        _ => [],
    };

    public void Dispose() => NativeMemory.Free(_object);
}
