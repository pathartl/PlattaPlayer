using System.Runtime.InteropServices;

namespace PlattaPlayer.Wmp.Harness.Interop;

/// <summary>
/// Runs the REAL feedback pass (<c>FUN_18000d940</c>) — the gather through the warp map, the weighted
/// neighbour blur, and the edge-row repaint — over buffers we own.
///
/// This is the function that touches every pixel of every frame, so it is the one place where a small
/// systematic error compounds into the whole field looking wrong. It had only ever been READ.
///
/// It needs remarkably little: the shift stage purely for the map pointer at +0x08, and a render data
/// carrying the field size at +0x3c/+0x40, the edge colour at +0x18 and the two surfaces at +0x48/+0x50.
/// The surfaces themselves are read for a stride (+0x30) and a pixel pointer (+0xa8) only. Everything can
/// therefore be zeroed memory with a handful of fields poked in.
///
/// <b>Note the surface bookkeeping</b>, because it reads like a bug and is not. The function swaps +0x48
/// and +0x50, gathers into the new +0x48, then swaps them BACK before blurring — so the net effect is no
/// swap at all: <c>B = gather(A)</c> then <c>A = blur(B)</c>, leaving the finished frame in the same
/// surface the renderers draw into and using the other purely as scratch.
/// </summary>
internal sealed unsafe class FeedbackOracle : IDisposable
{
    private const long FeedbackVa = 0x18000d940;

    // The shift stage, of which only the map pointer is touched.
    private const int StageSize = 0x600;

    private const int StageMap = 0x08;

    private const int RenderDataSize = 0x80;

    private const int RenderDataEdgeColour = 0x18;

    private const int RenderDataWidth = 0x3c;

    private const int RenderDataHeight = 0x40;

    private const int RenderDataSurfaceA = 0x48;

    private const int RenderDataSurfaceB = 0x50;

    private const int SurfaceSize = 0x100;

    private const int SurfaceStride = 0x30;

    private const int SurfaceHeight = 0x34;

    private const int SurfaceBits = 0xa8;

    private readonly void* _stage;
    private readonly void* _renderData;
    private readonly void* _surfaceA;
    private readonly void* _surfaceB;

    private readonly int[] _frame;
    private readonly int[] _scratch;
    private readonly int[] _map;

    private GCHandle _framePin;
    private GCHandle _scratchPin;
    private GCHandle _mapPin;

    public int Width { get; }

    public int Height { get; }

    /// <summary>Surface A — what the renderers draw into, and where the finished frame ends up.</summary>
    public int[] Frame => _frame;

    /// <summary>Surface B — scratch, holding the gathered (warped, unblurred) copy on return.</summary>
    public int[] Scratch => _scratch;

    /// <summary>The warp map: a flat source INDEX per destination pixel, as the stage builds it.</summary>
    public int[] Map => _map;

    public FeedbackOracle(int width, int height)
    {
        Width = width;
        Height = height;
        _frame = new int[width * height];
        _scratch = new int[width * height];
        _map = new int[width * height];
        _framePin = GCHandle.Alloc(_frame, GCHandleType.Pinned);
        _scratchPin = GCHandle.Alloc(_scratch, GCHandleType.Pinned);
        _mapPin = GCHandle.Alloc(_map, GCHandleType.Pinned);

        _surfaceA = MakeSurface(width, height, _framePin);
        _surfaceB = MakeSurface(width, height, _scratchPin);

        _stage = NativeMemory.AllocZeroed(StageSize);
        *(void**)((byte*)_stage + StageMap) = (void*)_mapPin.AddrOfPinnedObject();

        _renderData = NativeMemory.AllocZeroed(RenderDataSize);
        *(int*)((byte*)_renderData + RenderDataWidth) = width;
        *(int*)((byte*)_renderData + RenderDataHeight) = height;
        *(void**)((byte*)_renderData + RenderDataSurfaceA) = _surfaceA;
        *(void**)((byte*)_renderData + RenderDataSurfaceB) = _surfaceB;
    }

    private static void* MakeSurface(int width, int height, GCHandle pixels)
    {
        var surface = NativeMemory.AllocZeroed(SurfaceSize);
        *(int*)((byte*)surface + SurfaceStride) = width;
        *(int*)((byte*)surface + SurfaceHeight) = height;
        *(void**)((byte*)surface + SurfaceBits) = (void*)pixels.AddrOfPinnedObject();
        return surface;
    }

    /// <summary>The colour rows 0 and H-1 are repainted with, at renderData+0x18.</summary>
    public uint EdgeColour
    {
        get => *(uint*)((byte*)_renderData + RenderDataEdgeColour);
        set => *(uint*)((byte*)_renderData + RenderDataEdgeColour) = value;
    }

    /// <summary>
    /// Which surface the render data currently calls "current". The pass is supposed to leave this
    /// unchanged; the verb asserts that rather than assuming it.
    /// </summary>
    public bool CurrentIsFrame => *(void**)((byte*)_renderData + RenderDataSurfaceA) == _surfaceA;

    public void Run()
    {
        var fn = (delegate* unmanaged[Cdecl]<void*, void*, void>)MpvisModule.At(FeedbackVa);
        fn(_stage, _renderData);
    }

    public void Dispose()
    {
        if (_framePin.IsAllocated) _framePin.Free();
        if (_scratchPin.IsAllocated) _scratchPin.Free();
        if (_mapPin.IsAllocated) _mapPin.Free();
        NativeMemory.Free(_renderData);
        NativeMemory.Free(_stage);
        NativeMemory.Free(_surfaceA);
        NativeMemory.Free(_surfaceB);
    }
}
