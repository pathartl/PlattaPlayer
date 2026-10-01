using System.Runtime.InteropServices;

namespace PlattaPlayer.Wmp.Harness.Interop;

internal enum ShiftKernelKind
{
    /// <summary>CTShiftLinear — name resource 105 "Linear Shift".</summary>
    Linear,

    /// <summary>CTShiftSnafu — name resource 114.</summary>
    Snafu,

    /// <summary>CTShiftStretch — name resource 106 "Stretch Shift".</summary>
    Stretch,

    /// <summary>CToleranceShiftOScope — name resource 109 "Shift O' Scope".</summary>
    OScope,
}

/// <summary>
/// Constructs a REAL mpvis shift kernel and calls its methods directly, so our port can be diffed against
/// it one function at a time instead of one picture at a time.
///
/// This is the piece the port has been missing. Every comparison so far has been between final composite
/// frames, which conflates the audio analysis, the kernel maths, the renderers, the palette and the
/// scheduler — and since the two sides cannot pick the same effects, even that comparison was only
/// structural. A kernel, though, is a self-contained object with a pure-ish method on it:
/// <c>FormShift(int* x, int* y)</c> maps one destination pixel to its source. Feed both implementations
/// the same parameters and sweep every coordinate in the field, and the result is an exhaustive proof
/// rather than an impression.
///
/// HOW THE OBJECT IS BUILT. The kernels are plain C++ objects with no allocation of their own: mpvis's
/// pool does <c>ctor(malloc(size))</c>, so we can supply the memory ourselves and call the same
/// constructor. <see cref="Dispose"/> deliberately does NOT call the destructor — the destructors end in
/// <c>_o_free(this, size)</c> against mpvis's allocator, and handing it a block from
/// <see cref="NativeMemory"/> would corrupt its heap. Skipping it leaks nothing, because these objects
/// own no external resources.
///
/// EVERY ADDRESS IS VERIFIED, NOT TRUSTED. Construction checks the object's vtable slots against the
/// functions the decompile says belong there, so a wrong constructor address or a different build fails
/// immediately rather than producing plausible-looking wrong numbers.
/// </summary>
internal sealed unsafe class ShiftKernelOracle : IDisposable
{
    // Constructor VA and object size, from FUN_180009eb0's ten `alloc(size); ctor(p)` pairs.
    private static readonly (long Ctor, int Size, long ExpectedFormShift, long ExpectedRandomize)[] Table =
    [
        (0x18000de44, 0x90, 0x18000e280, 0x18000ebe0),  // Linear
        (0x18000dea4, 0x70, 0x18000e500, 0x18000ed70),  // Snafu
        (0x18000dedc, 0xa8, 0x18000e590, 0x18000ee00),  // Stretch
        (0x18000df64, 0x260, 0x18000e6e0, 0x18000ef90), // OScope
    ];

    /// <summary>
    /// CToleranceShiftEffect vtable layout, confirmed by dumping the four kernels' vtables out of .rdata.
    /// Slot 5 (+0x28) is the randomizer FUN_18000db4c invokes when the stage selects a kernel; slot 12
    /// (+0x60) is FormShift, the function FUN_18000d354 calls per pixel.
    /// </summary>
    private const int VtResize = 3;      // +0x18  FUN_18000ae60, shared by all four

    private const int VtRandomize = 5;   // +0x28

    private const int VtFormShift = 12;  // +0x60

    /// <summary>OScope embeds three child kernels; FUN_18000df64 constructs them at these offsets.</summary>
    private static readonly int[] OScopeChildren = [0xA0, 0x148, 0x1D8];

    private readonly void* _object;
    private readonly ShiftKernelKind _kind;

    public ShiftKernelKind Kind => _kind;

    private ShiftKernelOracle(ShiftKernelKind kind, void* obj)
    {
        _kind = kind;
        _object = obj;
    }

    public static ShiftKernelOracle Create(ShiftKernelKind kind, int width, int height)
    {
        var (ctorVa, size, expectedFormShift, expectedRandomize) = Table[(int)kind];

        var obj = NativeMemory.AllocZeroed((nuint)size);
        var ctor = (delegate* unmanaged[Cdecl]<void*, void*>)MpvisModule.At(ctorVa);
        ctor(obj);

        var oracle = new ShiftKernelOracle(kind, obj);
        oracle.VerifyVTable(expectedFormShift, expectedRandomize);
        oracle.Resize(width, height);
        return oracle;
    }

    /// <summary>
    /// Cross-checks the constructed object against the decompile before anyone calls into it. If the
    /// constructor address were wrong we would be holding whatever that function happened to build, and
    /// a per-pixel call into it would be a jump to an arbitrary address.
    /// </summary>
    private void VerifyVTable(long expectedFormShift, long expectedRandomize)
    {
        var vtable = *(long**)_object;
        var formShift = vtable[VtFormShift] - MpvisModule.Handle + MpvisModule.ImageBase;
        var randomize = vtable[VtRandomize] - MpvisModule.Handle + MpvisModule.ImageBase;

        if (formShift != expectedFormShift || randomize != expectedRandomize)
            throw new InvalidOperationException(
                $"{_kind}: constructed object's vtable has FormShift=0x{formShift:X} Randomize=0x{randomize:X}, " +
                $"expected 0x{expectedFormShift:X} / 0x{expectedRandomize:X}. The constructor address or the " +
                "build is wrong — refusing to call into it.");
    }

    /// <summary>
    /// Sets the field size through the shared resize (<c>FUN_18000ae60</c>), which also derives the centre
    /// at +0x10/+0x14 and the half-diagonal at +0x18, then dispatches to the per-class recompute at
    /// vtable +0x40 (Stretch's focal point and maxR, OScope's mode setup). OScope's three children are
    /// resized explicitly — the parent's recompute does not reach them.
    /// </summary>
    private void Resize(int width, int height)
    {
        ResizeOne(_object, width, height);
        if (_kind != ShiftKernelKind.OScope) return;
        foreach (var offset in OScopeChildren)
            ResizeOne((byte*)_object + offset, width, height);
    }

    private static void ResizeOne(void* obj, int width, int height)
    {
        var vtable = *(long**)obj;
        ((delegate* unmanaged[Cdecl]<void*, int, int, void>)vtable[VtResize])(obj, width, height);
    }

    /// <summary>
    /// Re-rolls the kernel's tunables, consuming <c>rand()</c> — so with <see cref="RandRedirect"/> live
    /// and a script installed, the parameters are ours to choose.
    /// </summary>
    public void Randomize()
    {
        var vtable = *(long**)_object;
        ((delegate* unmanaged[Cdecl]<void*, void>)vtable[VtRandomize])(_object);
    }

    /// <summary>The real inverse warp: maps a destination pixel to the source it samples.</summary>
    public (int X, int Y) FormShift(int x, int y)
    {
        var vtable = *(long**)_object;
        var fn = (delegate* unmanaged[Cdecl]<void*, int*, int*, void>)vtable[VtFormShift];
        int sx = x, sy = y;
        fn(_object, &sx, &sy);
        return (sx, sy);
    }

    /// <summary>
    /// The raw object, for functions that take a kernel rather than call one — <c>FUN_18000d354</c>
    /// assembles the warp map by invoking <c>FormShift</c> through the vtable itself and then reading the
    /// kernel's own bounds and out-of-range fields, so it has to be handed the real pointer.
    /// </summary>
    public void* Object => _object;

    public int ReadInt32(int offset) => *(int*)((byte*)_object + offset);

    public double ReadDouble(int offset) => *(double*)((byte*)_object + offset);

    public bool ReadBool(int offset) => *((byte*)_object + offset) != 0;

    /// <summary>
    /// The kernel's tunables at the offsets its randomizer writes and its FormShift reads, named. Used to
    /// separate "our randomizer draws different parameters" from "our maths is wrong", which are very
    /// different bugs and look identical in a picture.
    /// </summary>
    public IEnumerable<(string Name, double Value)> Parameters() => _kind switch
    {
        ShiftKernelKind.Linear =>
        [
            ("XShift", ReadInt32(0x60)), ("YShift", ReadInt32(0x64)),
            ("Falloff", ReadBool(0x68) ? 1 : 0),
            ("FallPctX", ReadDouble(0x70)), ("FallPctY", ReadDouble(0x78)),
            ("FallDir", ReadInt32(0x80)), ("SinShake", ReadInt32(0x84)), ("SinLoops", ReadInt32(0x88)),
        ],
        ShiftKernelKind.Snafu =>
        [
            ("Speed", ReadInt32(0x60)), ("Width", ReadInt32(0x64)), ("Vertical", ReadBool(0x68) ? 1 : 0),
        ],
        ShiftKernelKind.Stretch =>
        [
            ("Rotation", ReadDouble(0x60)), ("MovePct", ReadDouble(0x68)),
            ("FlowPoint", ReadBool(0x70) ? 1 : 0),
            ("PctX", ReadDouble(0x78)), ("PctY", ReadDouble(0x80)),
            ("SinShake", ReadBool(0x88) ? 1 : 0), ("SinLoops", ReadInt32(0x8c)),
            // Derived by the precompute FUN_18000ea40, not by the randomizer.
            ("focalX", ReadInt32(0x90)), ("focalY", ReadInt32(0x94)), ("pull", ReadInt32(0x98)),
            ("maxR", ReadDouble(0xa0)),
        ],
        // FUN_18000ef90: ReflectionMode then ShiftMode, then it randomizes only the ONE child ShiftMode
        // selects (or, for mode 3, a spin factor), and only fills CenterR/LittleR or BoxSize when the
        // reflection mode that uses them came up.
        ShiftKernelKind.OScope =>
        [
            ("ReflectionMode", ReadInt32(0x90)), ("ShiftMode", ReadInt32(0x94)),
            ("SpinFactor", ReadDouble(0x70)),
            ("CenterRadius", ReadInt32(0x88)), ("LittleRadius", ReadInt32(0x8c)),
            ("BoxSize", ReadInt32(0x98)),
        ],
        _ => [],
    };

    public void Dispose() => NativeMemory.Free(_object);
}
