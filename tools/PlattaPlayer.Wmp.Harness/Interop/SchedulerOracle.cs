using System.Runtime.InteropServices;

namespace PlattaPlayer.Wmp.Harness.Interop;

/// <summary>
/// Runs the REAL Alchemy scheduler — <c>CToleranceEffects::Render</c> (<c>0x18000a394</c>), which drives
/// <c>CToleranceRenderStep::Render</c> and <c>SelectEffects</c> — over a hand-built effect pool, so its
/// choices can be diffed frame by frame against <c>EffectScheduler</c>.
///
/// Everything the scheduler touches is laid out here exactly as <c>AddEffects</c> builds it:
/// <list type="bullet">
/// <item><c>CToleranceEffects</c>: pool TArray at <c>+0x08</c> (data <c>+0x10</c>, count <c>+0x18</c>),
/// cycle countdown <c>+0x20</c>, current cycle <c>+0x24</c>, cycle TArray at <c>+0x28</c> (data
/// <c>+0x30</c>, count <c>+0x38</c>).</item>
/// <item>One <c>CToleranceRenderCycle</c>: min <c>+0x00</c> = 1500, max <c>+0x04</c> = 7500, weight
/// <c>+0x08</c> = 1.0, step TArray at <c>+0x10</c> (data <c>+0x18</c>, count <c>+0x20</c>).</item>
/// <item>Eight <c>CToleranceRenderStep</c>s of 0x38 bytes, from <c>AddRenderStep</c>'s arguments.</item>
/// </list>
///
/// The pool entries are REAL effect objects built by their own constructors, with two changes. Each gets
/// a private copy of its vtable in which <c>MMXRender</c> (+0x30) and <c>NormalRender</c> (+0x38) point
/// at <c>0x180004e20</c>. That is a bare <c>ret</c> which the DLL's own vtables already use, so it is a
/// valid Control Flow Guard target. The effects are therefore SELECTED and RANDOMIZED for real but draw
/// nothing, which is what lets this run without surfaces. The category (<c>+0x48</c>) and weight
/// (<c>+0x20</c>) are also written to match the shipped pool.
///
/// The shift stage and the two blurs are not built. <c>CToleranceShift</c> needs warp tables, and the
/// blurs are never selected. Their slots hold BassBounce objects relabelled to categories 3, 2 and 2, and
/// the managed side must use the same stand-ins. This checks the SCHEDULER, not what the shift stage
/// does when step 0 picks it.
///
/// Nothing here is ever destroyed through the DLL. Their destructors would free into mpvis's allocator.
/// The TArrays <c>SelectEffects</c> grows belong to that allocator and are deliberately leaked.
/// </summary>
internal sealed unsafe class SchedulerOracle : IDisposable
{
    private const long EffectsRenderVa = 0x18000a394;
    private const long RetStubVa = 0x180004e20;
    private const long TArrayVtableVa = 0x1800204a0;

    private const int StepSize = 0x38;
    private const int VtableSlots = 32;
    private const int VtMmxRender = 6;
    private const int VtNormalRender = 7;

    /// <summary><c>AddEffects</c>' eight <c>AddRenderStep</c> calls: category, min, max, min life, max life, flags.</summary>
    public static readonly (int Category, int Min, int Max, int MinLife, int MaxLife, int Flags)[] Steps =
    [
        (3, 1, 1, 90, 500, 0),
        (4, 1, 2, 50, 350, 2),
        (6, 0, 1, 30, 500, 1),
        (5, 0, 4, 30, 500, 1),
        (6, 0, 1, 30, 500, 1),
        (4, 1, 3, 50, 350, 2),
        (5, 0, 2, 30, 500, 1),
        (7, 0, 1, 50, 400, 1),
    ];

    /// <summary>The shipped pool: the object each slot is built from, its category and its weight.</summary>
    public static readonly (RenderEffectKind Kind, int Category, float Weight, string Name)[] Pool =
    [
        (RenderEffectKind.BassBounce, 3, 1.0f, "Shift*"),
        (RenderEffectKind.BassBounce, 2, 1.0f, "Blur*"),
        (RenderEffectKind.BassBounce, 2, 0.2f, "SwitchBlur*"),
        (RenderEffectKind.WonderWave, 4, 1.0f, "WonderWave"),
        (RenderEffectKind.SuperStar, 4, 0.5f, "SuperStar"),
        (RenderEffectKind.BassBounce, 7, 0.5f, "BassBounce"),
        (RenderEffectKind.AtomBalls, 4, 0.9f, "AtomBalls"),
    ];

    private readonly List<RenderEffectOracle> _effects = [];
    private readonly List<nint> _allocations = [];
    private readonly byte* _root;
    private readonly byte* _steps;
    private readonly byte* _renderData;

    public SchedulerOracle(int width, int height)
    {
        var retStub = MpvisModule.At(RetStubVa);
        var tarrayVtable = MpvisModule.At(TArrayVtableVa);

        var poolArray = (long*)Alloc(Pool.Length * 8);
        for (var i = 0; i < Pool.Length; i++)
        {
            var (kind, category, weight, _) = Pool[i];
            var effect = RenderEffectOracle.Create(kind, width, height);
            _effects.Add(effect);
            var obj = (byte*)effect.Object;

            var vtable = (long*)Alloc(VtableSlots * 8);
            Buffer.MemoryCopy(*(long**)obj, vtable, VtableSlots * 8, VtableSlots * 8);
            vtable[VtMmxRender] = retStub;
            vtable[VtNormalRender] = retStub;
            *(long**)obj = vtable;

            *(int*)(obj + 0x48) = category;
            *(float*)(obj + 0x20) = weight;
            poolArray[i] = (long)obj;
        }

        _steps = (byte*)Alloc(Steps.Length * StepSize);
        for (var i = 0; i < Steps.Length; i++)
        {
            var s = _steps + i * StepSize;
            var (category, min, max, minLife, maxLife, flags) = Steps[i];
            *(int*)(s + 0x00) = category;
            *(int*)(s + 0x04) = min;
            *(int*)(s + 0x08) = max;
            *(int*)(s + 0x0c) = minLife;
            *(int*)(s + 0x10) = maxLife;
            *(int*)(s + 0x14) = flags;
            *(long*)(s + 0x18) = tarrayVtable;
        }

        var cycle = (byte*)Alloc(0x38);
        *(int*)(cycle + 0x00) = 1500;
        *(int*)(cycle + 0x04) = 7500;
        *(double*)(cycle + 0x08) = 1.0;
        *(long*)(cycle + 0x10) = tarrayVtable;
        *(long*)(cycle + 0x18) = (long)_steps;
        *(int*)(cycle + 0x20) = Steps.Length;
        *(int*)(cycle + 0x24) = Steps.Length;

        var cycles = (long*)Alloc(8);
        cycles[0] = (long)cycle;

        _root = (byte*)Alloc(0x40);
        *(long*)(_root + 0x08) = tarrayVtable;
        *(long*)(_root + 0x10) = (long)poolArray;
        *(int*)(_root + 0x18) = Pool.Length;
        *(int*)(_root + 0x1c) = Pool.Length;
        *(long*)(_root + 0x28) = tarrayVtable;
        *(long*)(_root + 0x30) = (long)cycles;
        *(int*)(_root + 0x38) = 1;
        *(int*)(_root + 0x3c) = 1;

        // Only +0x10 (resize request), +0x11 (pause) and +0x3a (MMX) are read on this path. All zero
        // except the pause, which Frame() drives.
        _renderData = (byte*)Alloc(0x400);
        *(int*)(_renderData + 0x3c) = width;
        *(int*)(_renderData + 0x40) = height;
    }

    private void* Alloc(int size)
    {
        var p = NativeMemory.AllocZeroed((nuint)size);
        _allocations.Add((nint)p);
        return p;
    }

    /// <summary>One frame of the real <c>CToleranceEffects::Render</c>.</summary>
    public void Frame(bool paused)
    {
        _renderData[0x11] = paused ? (byte)1 : (byte)0;
        ((delegate* unmanaged[Cdecl]<void*, void*, void>)MpvisModule.At(EffectsRenderVa))(_root, _renderData);
    }

    public int CycleCountdown => *(int*)(_root + 0x20);

    public int StepCountdown(int step) => *(int*)(_steps + step * StepSize + 0x30);

    /// <summary>The pool indices a step holds, in acceptance order.</summary>
    public int[] StepActive(int step)
    {
        var s = _steps + step * StepSize;
        var count = *(int*)(s + 0x28);
        var data = *(long**)(s + 0x20);
        var result = new int[count];
        for (var i = 0; i < count; i++) result[i] = PoolIndexOf((void*)data[i]);
        return result;
    }

    private int PoolIndexOf(void* obj)
    {
        for (var i = 0; i < _effects.Count; i++)
            if (_effects[i].Object == obj) return i;
        return -1;
    }

    public bool Taken(int entry) => _effects[entry].ReadBool(0x24);

    /// <summary>The fade envelope, effect +0x28 phase / +0x2c counter / +0x30 duration.</summary>
    public (int Phase, int Counter, int Duration) Envelope(int entry) =>
        (_effects[entry].ReadInt32(0x28), _effects[entry].ReadInt32(0x2c), _effects[entry].ReadInt32(0x30));

    public void Dispose()
    {
        // The effect objects are freed by their oracles; everything else here is ours. mpvis-owned
        // arrays hanging off the steps are leaked on purpose.
        foreach (var effect in _effects) effect.Dispose();
        foreach (var p in _allocations) NativeMemory.Free((void*)p);
    }
}
