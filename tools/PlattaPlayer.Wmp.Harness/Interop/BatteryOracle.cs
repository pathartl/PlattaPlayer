using System.Runtime.InteropServices;
using PlattaPlayer.Visualizations.Wmp.Battery;

namespace PlattaPlayer.Wmp.Harness.Interop;

/// <summary>
/// Calls Battery's internal objects in the installed wmp.dll directly. It is the Battery counterpart of
/// <see cref="ShiftKernelOracle"/>.
///
/// The objects come from a REAL <c>CRenderData</c>, built by its own constructor (<c>0x18040f470</c>) in
/// memory we own. That constructor creates the whole effect pool exactly as Battery does: 15 shifts
/// (CLinearShift twice), then 10 render effects, then CRandomEffect. So every object has a genuine vtable,
/// size and field defaults, with no hand-built structs to get wrong. Each object's primary vtable is
/// checked against the one the symbol port recorded for its class before anything is called.
///
/// Never call a destructor or Release: they free through wmp.dll's allocator. The render data and its
/// pool are simply leaked; this is a short-lived dev tool.
///
/// Layout (x64), from <c>Code\battery\03_shifts.md</c>:
/// <list type="bullet">
/// <item>CRenderData shift pool: dynamic array at +0x40, items at +0x48, count at +0x50.</item>
/// <item>CRenderData render pool: dynamic array at +0x28, items at +0x30, count at +0x38.</item>
/// <item>Shift object: dbl1..dbl8 at +0x08; flags +0xe0; W/H/cx/cy +0xe8..; table +0xf8;
/// transitions +0x100; out-of-range policy +0x1a0.</item>
/// <item>Primary vtable slots: 0 SetRandom, 1 SetParams, 2 MarkDirty, 3 Randomize, 5 SetSize,
/// 6 Setup, 7 Complete, 8 FormShift.</item>
/// </list>
/// </summary>
internal static unsafe class BatteryOracle
{
    private const long RenderDataCtor = 0x18040f470;

    /// <summary>Generous: x86 CRenderData spans ~0xcf0 bytes inside the 0xff0 CBattery.</summary>
    private const int RenderDataSize = 0x1000;

    /// <summary>The pool order the constructor builds, with each class's primary vtable (x64 VA).</summary>
    public static readonly (string Name, long Vtable)[] ShiftPool =
    [
        ("CLinearShift", 0x1807a7b68), ("CLinearShift", 0x1807a7b68), ("CThingusShift", 0x1807a7c58),
        ("CZoomShift", 0x1807a7ca0), ("CRingSpinShift", 0x1807a7bc8), ("CStretchShift", 0x1807a7c10),
        ("CTileShift", 0x1807a7d78), ("CTrigShift", 0x1807a7dc0), ("CSinShimmerShift", 0x1807a7ce8),
        ("CEdgeFalloffShift", 0x1807a7d30), ("CStarburstShift", 0x1807a7e98), ("CSwirlShift", 0x1807a7ee0),
        ("CTrigStretchShift", 0x1807a7e08), ("CTwirlocity", 0x1807a7e50), ("CShiitake", 0x1807a80f0),
    ];

    private static nint _renderData;

    /// <summary>A real CRenderData, constructed once. Run with rand redirected if construction draws
    /// matter (CJDar's constructor draws).</summary>
    public static nint RenderData
    {
        get
        {
            if (_renderData != 0) return _renderData;
            WmpModule.AssertKnownBuild();
            var p = (nint)NativeMemory.AllocZeroed(RenderDataSize);
            ((delegate* unmanaged<nint, nint>)WmpModule.At(RenderDataCtor))(p);
            _renderData = p;
            return p;
        }
    }

    /// <summary>The render pool order, with each class's Render (vtable slot 4) as the identity check.</summary>
    public static readonly (string Name, long Render)[] RenderPool =
    [
        ("CEdgeTrace", 0x1804178d0), ("CEdgeGradiant", 0x1804177e0), ("CCosEdgeGradiant", 0x1804176f0),
        ("CWaveEdge", 0x180418a30), ("CSpectrumEdge", 0x180418860), ("CCircleWaveform", 0x1804172d0),
        ("CDotPlane", 0x1804193a0), ("CJDar", 0x180418020), ("CGalaxy", 0x180417c70),
        ("CJiggyScribble", 0x180418470),
    ];

    /// <summary>The render effect at pool position <paramref name="index"/>, identity-checked by its
    /// Render slot.</summary>
    public static BatteryRenderObject Render(int index)
    {
        var rd = RenderData;
        var count = *(int*)(rd + 0x38);
        if (count != RenderPool.Length)
            throw new InvalidOperationException($"CRenderData render pool has {count} entries, expected {RenderPool.Length}.");
        var obj = ((nint*)*(nint*)(rd + 0x30))[index];
        var render = WmpModule.VaOf(((nint*)*(nint*)obj)[4]);
        if (render != RenderPool[index].Render)
            throw new InvalidOperationException(
                $"Render effect {index} slot 4 is 0x{render:X}, expected {RenderPool[index].Name} at 0x{RenderPool[index].Render:X}.");
        return new BatteryRenderObject(obj, RenderPool[index].Name);
    }

    /// <summary>The shift object at pool position <paramref name="index"/>, with its vtable verified.</summary>
    public static BatteryShiftObject Shift(int index)
    {
        var rd = RenderData;
        var count = *(int*)(rd + 0x50);
        if (count != ShiftPool.Length)
            throw new InvalidOperationException($"CRenderData shift pool has {count} entries, expected {ShiftPool.Length}.");
        var obj = ((nint*)*(nint*)(rd + 0x48))[index];
        var vt = WmpModule.VaOf(*(nint*)obj);
        if (vt != ShiftPool[index].Vtable)
            throw new InvalidOperationException(
                $"Shift {index} has vtable 0x{vt:X}, expected {ShiftPool[index].Name} at 0x{ShiftPool[index].Vtable:X}.");
        return new BatteryShiftObject(obj, ShiftPool[index].Name);
    }
}

/// <summary>A real CBatteryShiftTable-derived object inside wmp.dll.</summary>
internal sealed unsafe class BatteryShiftObject(nint obj, string name)
{
    public nint Pointer => obj;

    public string Name => name;

    private nint Slot(int i) => ((nint*)*(nint*)obj)[i];

    public double Param(int i) => *(double*)(obj + 0x08 + 8 * i);

    public void SetParam(int i, double v) => *(double*)(obj + 0x08 + 8 * i) = v;

    public int Flags => *(int*)(obj + 0xe0);

    public bool KeepOutOfRange => *(byte*)(obj + 0x1a0) != 0;

    public int W => *(int*)(obj + 0xe8);

    public int H => *(int*)(obj + 0xec);

    public bool IsComplete => *(byte*)(obj + 0x198) != 0;

    public int TransitionIndex => *(int*)(obj + 0x190);

    public int TransitionFrames => *(int*)(obj + 0x194);

    public ReadOnlySpan<int> Table => new((void*)*(nint*)(obj + 0xf8), W * H);

    public ReadOnlySpan<int> Transition(int i) => new((void*)*(nint*)(obj + 0x100 + 8 * i), W * H);

    public void MarkDirty() => ((delegate* unmanaged<nint, void>)Slot(2))(obj);

    public void Randomize() => ((delegate* unmanaged<nint, void>)Slot(3))(obj);

    public void SetSize(int w, int h, bool transitions) =>
        ((delegate* unmanaged<nint, int, int, byte, void>)Slot(5))(obj, w, h, transitions ? (byte)1 : (byte)0);

    public void Setup() => ((delegate* unmanaged<nint, void>)Slot(6))(obj);

    public void FormShift(ref int x, ref int y)
    {
        int lx = x, ly = y;
        ((delegate* unmanaged<nint, int*, int*, void>)Slot(8))(obj, &lx, &ly);
        x = lx;
        y = ly;
    }

    /// <summary><c>CBatteryShiftTable::GetData</c> (not virtual).</summary>
    public nint GetData() => ((delegate* unmanaged<nint, nint>)WmpModule.At(0x18041192c))(obj);

    /// <summary><c>CBatteryShiftTable::SetLastShift</c> (not virtual). Note it AddRefs/Releases through
    /// the secondary vtable, which only touches the refcount.</summary>
    public void SetLastShift(BatteryShiftObject? last) =>
        ((delegate* unmanaged<nint, nint, void>)WmpModule.At(0x1804131cc))(obj, last?.Pointer ?? 0);
}

/// <summary>A real CRenderEffect-derived object inside wmp.dll.</summary>
internal sealed unsafe class BatteryRenderObject(nint obj, string name)
{
    public nint Pointer => obj;

    public string Name => name;

    private nint Slot(int i) => ((nint*)*(nint*)obj)[i];

    public double Param(int i) => *(double*)(obj + 0x08 + 8 * i);

    public int Flags => *(int*)(obj + 0xe0);

    public void Randomize() => ((delegate* unmanaged<nint, void>)Slot(3))(obj);

    /// <summary>Slot 1 (<c>CMemoryEffect::SetParams</c>): d1 in xmm1, d2 xmm2, d3 xmm3, then d4..d8 and the
    /// four string pointers on the stack.</summary>
    public void SetParams(double[] p) =>
        ((delegate* unmanaged<nint, double, double, double, double, double, double, double, double, nint, nint, nint, nint, void>)Slot(1))(
            obj, p[0], p[1], p[2], p[3], p[4], p[5], p[6], p[7], 0, 0, 0, 0);

    /// <summary>Slot 4: <c>Render(TimedLevel*, CRenderData*)</c>.</summary>
    public void Render(nint timedLevel, nint renderData) =>
        ((delegate* unmanaged<nint, nint, nint, void>)Slot(4))(obj, timedLevel, renderData);
}

/// <summary>A <see cref="CrtRand"/> that replays the same script <see cref="RandRedirect"/> feeds the
/// DLL, wrapping the same way, so both sides consume identical draws.</summary>
internal sealed class ScriptedCrtRand(int[] script) : CrtRand
{
    private int _index;

    public int Position => _index;

    public void Rewind() => _index = 0;

    protected override int NextCore()
    {
        if (script.Length == 0) return 0;
        var v = script[_index % script.Length];
        _index++;
        return v;
    }
}
