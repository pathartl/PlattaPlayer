using System.Runtime.InteropServices;

namespace PlattaPlayer.Wmp.Harness.Interop;

/// <summary>
/// Runs the REAL warp-map assembly (<c>FUN_18000d354</c>) with kernels we control.
///
/// Each kernel's <c>FormShift</c> is already proven bit-exact by <c>verify-warps</c>. This verifies the
/// layer above it — the part that is pure bookkeeping and therefore easy to get plausibly wrong: the
/// per-kernel out-of-range policy, the order the two kernels are applied in, what the second one's
/// fallback restores to, the blend back toward the identity, and the row table the flat index is built
/// from.
///
/// The stage it needs is almost nothing: a field size at +0x08/+0x0c and a row table at +0x530. The row
/// table's contents are not a guess — <c>FUN_18000cbdc</c> fills it with <c>rowTable[i] = i * width</c>
/// for <c>i = 0 .. height</c> (one entry more than there are rows), and this reproduces that exactly so a
/// stride assumption cannot hide inside the comparison.
///
/// The map object is three fields: the destination array at +0x08, a "needs work" flag at +0x10, and the
/// row to RESUME FROM at +0x14 — the assembly is restartable, which is what the morphing path exploits.
/// </summary>
internal sealed unsafe class WarpMapOracle : IDisposable
{
    private const long BuildVa = 0x18000d354;

    private const int StageSize = 0x600;

    private const int StageWidth = 0x08;

    private const int StageHeight = 0x0c;

    private const int StageRowTable = 0x530;

    private const int MapObjectSize = 0x20;

    private const int MapObjectArray = 0x08;

    private const int MapObjectFlag = 0x10;

    private const int MapObjectNextRow = 0x14;

    private readonly void* _stage;
    private readonly void* _mapObject;
    private readonly int[] _map;
    private readonly int[] _rowTable;
    private GCHandle _mapPin;
    private GCHandle _rowTablePin;

    public int Width { get; }

    public int Height { get; }

    /// <summary>The assembled map: a flat source INDEX per destination pixel.</summary>
    public int[] Map => _map;

    public WarpMapOracle(int width, int height)
    {
        Width = width;
        Height = height;

        _map = new int[width * height];
        _mapPin = GCHandle.Alloc(_map, GCHandleType.Pinned);

        // FUN_18000cbdc allocates height + 1 entries and fills rowTable[i] = i * width.
        _rowTable = new int[height + 1];
        for (var i = 0; i <= height; i++) _rowTable[i] = i * width;
        _rowTablePin = GCHandle.Alloc(_rowTable, GCHandleType.Pinned);

        _stage = NativeMemory.AllocZeroed(StageSize);
        *(int*)((byte*)_stage + StageWidth) = width;
        *(int*)((byte*)_stage + StageHeight) = height;
        *(void**)((byte*)_stage + StageRowTable) = (void*)_rowTablePin.AddrOfPinnedObject();

        _mapObject = NativeMemory.AllocZeroed(MapObjectSize);
        *(void**)((byte*)_mapObject + MapObjectArray) = (void*)_mapPin.AddrOfPinnedObject();
    }

    /// <summary>
    /// Builds the whole map with <paramref name="a"/>, optionally chained through <paramref name="b"/>.
    /// The resume row is reset first, because the real function picks up where it left off and would
    /// otherwise do nothing on a second call.
    /// </summary>
    public void Build(ShiftKernelOracle a, ShiftKernelOracle? b)
    {
        *(int*)((byte*)_mapObject + MapObjectNextRow) = 0;
        *((byte*)_mapObject + MapObjectFlag) = 1;

        var fn = (delegate* unmanaged[Cdecl]<void*, void*, void*, void*, void>)MpvisModule.At(BuildVa);
        fn(_stage, _mapObject, a.Object, b is null ? null : b.Object);
    }

    /// <summary>
    /// The kernel's out-of-range policy, read off the real object. A CLEARED flag at +0x52 selects the
    /// fixed fallback at +0x54/+0x58 (zero for every kernel in this build, i.e. the black pixel at the
    /// origin); a set flag leaves the coordinate where it came in. Reading it here rather than assuming
    /// it keeps our <c>OutOfRangeToOrigin</c> honest.
    /// </summary>
    public static (bool ToOrigin, int FixedX, int FixedY) Policy(ShiftKernelOracle kernel) =>
        (!kernel.ReadBool(0x52), kernel.ReadInt32(0x54), kernel.ReadInt32(0x58));

    public void Dispose()
    {
        if (_mapPin.IsAllocated) _mapPin.Free();
        if (_rowTablePin.IsAllocated) _rowTablePin.Free();
        NativeMemory.Free(_mapObject);
        NativeMemory.Free(_stage);
    }
}
