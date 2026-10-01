using System.Runtime.InteropServices;

namespace PlattaPlayer.Wmp.Harness.Interop;

/// <summary>
/// Draws with the REAL spline (<c>FUN_18000ba50</c>) into a buffer we own, so the stroke geometry can be
/// diffed pixel-for-pixel instead of judged by eye.
///
/// The spline makes calls through its own vtable and hands its embedded <c>CTLineRender</c> (at its +0x08)
/// to the clipper, so a hand-built struct will not do — this constructs a REAL renderer and borrows the
/// spline inside it. Everything else is ours: the surface is a stub carrying only a stride, a height and a
/// pointer to a managed pixel array, which is all the drawing path ever reads from it.
///
/// The line renderer's "hold" byte is set so the palette does NOT advance while drawing. That deliberately
/// separates the two questions — where the pixels land, and what colour they take — so a geometry failure
/// cannot be mistaken for a colour one.
/// </summary>
internal sealed unsafe class SplineOracle : IDisposable
{
    private const long SplineVa = 0x18000ba50;

    /// <summary>
    /// <c>FUN_18000b0a0</c>, the CLIPPER — the spline's actual segment draw. It trims the segment against
    /// the three bounds at the line renderer's +0x00/+0x04/+0x08 and then tail-calls the Bresenham walk
    /// <c>FUN_18000b270</c>. The spline never calls the walk directly, so this is the right entry point to
    /// compare our <c>DrawPrimitives.Line</c> against.
    /// </summary>
    private const long ClipLineVa = 0x18000b0a0;

    /// <summary>
    /// <c>FUN_18000b730</c>, the plot. Its caller hands it a PIXEL POINTER and it consults the surface only
    /// for a stride and a height, so the stub surface below is all it needs.
    /// </summary>
    private const long PlotVa = 0x18000b730;

    /// <summary>The rose renderer (name id 108) — its spline sits at +0x80, line renderer at +0x88.</summary>
    private const long RoseCtorVa = 0x18000f960;

    private const int RoseSize = 0x178;

    private const int SplineOffsetInRenderer = 0x80;

    /// <summary>The embedded <c>CTLineRender</c>, which is what the clipper and the plot are given.</summary>
    private const int LineOffsetInRenderer = 0x88;

    // Field offsets within the RENDERER, derived from the spline base.
    // The clip bounds are essential: the renderer fills them from the surface size each frame
    // (`lineRender+0x08 = surfaceWidth - margin`, `+0x04 = surfaceHeight - margin`), and leaving them
    // zeroed clips every stroke away — the spline runs and draws precisely nothing.
    private const int LineMargin = 0x88 + 0x00;

    private const int LineMaxY = 0x88 + 0x04;

    private const int LineMaxX = 0x88 + 0x08;

    private const int LineColour = 0x88 + 0x0c;

    private const int LineHold = 0x88 + 0x30;

    private const int LineMode = 0x88 + 0x34;

    private const int LineSurface = 0x88 + 0x38;

    private const int LineAlpha = 0x88 + 0x40;

    private const int ParamBlock = 0x80 + 0x50;   // = 0xD0

    private const int MaxSteps = 0x80 + 0x70;     // = 0xF0

    // Stub surface layout: only these three are ever read by the drawing path.
    private const int SurfaceSize = 0x100;

    private const int SurfaceWidth = 0x30;

    private const int SurfaceHeight = 0x34;

    private const int SurfaceBits = 0xa8;

    private readonly void* _renderer;
    private readonly void* _surface;
    private readonly int[] _pixels;
    private GCHandle _pixelPin;

    public int Width { get; }

    public int Height { get; }

    public SplineOracle(int width, int height)
    {
        Width = width;
        Height = height;
        _pixels = new int[width * height];
        _pixelPin = GCHandle.Alloc(_pixels, GCHandleType.Pinned);

        _renderer = NativeMemory.AllocZeroed(RoseSize);
        ((delegate* unmanaged[Cdecl]<void*, void*>)MpvisModule.At(RoseCtorVa))(_renderer);

        _surface = NativeMemory.AllocZeroed(SurfaceSize);
        *(int*)((byte*)_surface + SurfaceWidth) = width;
        *(int*)((byte*)_surface + SurfaceHeight) = height;
        *(void**)((byte*)_surface + SurfaceBits) = (void*)_pixelPin.AddrOfPinnedObject();

        *(void**)((byte*)_renderer + LineSurface) = _surface;
        *((byte*)_renderer + LineHold) = 1;   // do not advance the palette while drawing
        ClipMargin = 0;
    }

    /// <summary>
    /// The stroke margin the renderer derives its clip bounds from — it is set to 0, 1 or 9 depending on
    /// the rose's RenderMode. Writing it also refreshes the bounds, as the real render body does.
    /// </summary>
    public int ClipMargin
    {
        get => *(int*)((byte*)_renderer + LineMargin);
        set
        {
            *(int*)((byte*)_renderer + LineMargin) = value;
            *(int*)((byte*)_renderer + LineMaxX) = Width - value;
            *(int*)((byte*)_renderer + LineMaxY) = Height - value;
        }
    }

    public int[] Pixels => _pixels;

    public void Clear() => Array.Clear(_pixels);

    /// <summary>
    /// Prefill every pixel. The blends and the five-tap blur are invisible against black, so any check of
    /// the plot footprint has to run over existing content or it only proves where the centre landed.
    /// </summary>
    public void Fill(uint argb) => Array.Fill(_pixels, unchecked((int)argb));

    /// <summary>Colour every plot writes (the line renderer's CTColor current value).</summary>
    public uint Colour
    {
        get => *(uint*)((byte*)_renderer + LineColour);
        set => *(uint*)((byte*)_renderer + LineColour) = value;
    }

    /// <summary>
    /// The line renderer's hold byte at +0x30. <c>FUN_18000b730</c> ends with
    /// <c>if (hold == 0) AdvanceColorTransition(this + 0x0c)</c>, so setting it should freeze the colour
    /// for the duration of a stroke. The oracle sets it on construction; <c>verify-plot</c> exists partly
    /// to establish whether it really does what it appears to.
    /// </summary>
    public bool Hold
    {
        get => *((byte*)_renderer + LineHold) != 0;
        set => *((byte*)_renderer + LineHold) = (byte)(value ? 1 : 0);
    }

    /// <summary>Plot footprint: 0 faint dot, 1 cross, 2/3 the 3x3 block plus a five-tap blur.</summary>
    public int PlotMode
    {
        get => *(int*)((byte*)_renderer + LineMode);
        set => *(int*)((byte*)_renderer + LineMode) = value;
    }

    public float NeighbourAlpha
    {
        get => *(float*)((byte*)_renderer + LineAlpha);
        set => *(float*)((byte*)_renderer + LineAlpha) = value;
    }

    /// <summary>Upper bound on the point count (spline+0x70); the actual N is min(span+1, this).</summary>
    public int StepLimit
    {
        get => *(int*)((byte*)_renderer + MaxSteps);
        set => *(int*)((byte*)_renderer + MaxSteps) = value;
    }

    /// <summary>The point count the spline settled on for the last stroke (spline+0x50).</summary>
    public int LastSteps => *(int*)((byte*)_renderer + ParamBlock);

    /// <summary>Raw dword at a renderer offset — for probing which field the drawing path actually reads.</summary>
    public uint Peek(int offset) => *(uint*)((byte*)_renderer + offset);

    public void Poke(int offset, uint value) => *(uint*)((byte*)_renderer + offset) = value;

    public void SetOffsetParams(int amplitude, int source, int envelopeMode, int lobes,
                                bool mirror, void* timedLevel)
    {
        var p = (byte*)_renderer + ParamBlock;
        *(int*)(p + 0x04) = amplitude;
        *(p + 0x08) = (byte)(mirror ? 1 : 0);
        *(int*)(p + 0x0c) = source;
        *(int*)(p + 0x10) = envelopeMode;
        *(int*)(p + 0x14) = lobes;
        *(void**)(p + 0x18) = timedLevel;
    }

    /// <summary>
    /// The real chord, <c>FUN_18000bdf4</c>. Note <paramref name="length"/> is the FULL span — the
    /// function halves it and puts the endpoints at <c>angle</c> and <c>angle + PI</c> about the centre.
    /// Getting that convention wrong draws the whole figure at twice the size.
    /// </summary>
    public void Chord(int cx, int cy, float angle, float length)
    {
        var fn = (delegate* unmanaged[Cdecl]<void*, int, int, float, float, void>)MpvisModule.At(ChordVa);
        fn((byte*)_renderer + SplineOffsetInRenderer, cx, cy, angle, length);
    }

    private const long ChordVa = 0x18000bdf4;

    /// <summary>Runs the real <c>FUN_18000ba50</c> from (x0,y0) to (x1,y1).</summary>
    public void Draw(int x0, int y0, int x1, int y1)
    {
        var fn = (delegate* unmanaged[Cdecl]<void*, int, int, int, int, void>)MpvisModule.At(SplineVa);
        fn((byte*)_renderer + SplineOffsetInRenderer, x0, y0, x1, y1);
    }

    /// <summary>
    /// The real clipped line, <c>FUN_18000b0a0</c> — one spline segment, drawn exactly as the spline draws
    /// it. Endpoints may lie anywhere, including far outside the surface: clipping is part of what is under
    /// test, because trimming the segment moves where the Bresenham walk STARTS and so changes the pixel
    /// pattern, not merely its extent.
    /// </summary>
    public void Line(int x0, int y0, int x1, int y1)
    {
        var fn = (delegate* unmanaged[Cdecl]<void*, int, int, int, int, void>)MpvisModule.At(ClipLineVa);
        fn((byte*)_renderer + LineOffsetInRenderer, x0, y0, x1, y1);
    }

    /// <summary>
    /// The real plot, <c>FUN_18000b730</c>, at one pixel. The caller is responsible for the pixel pointer,
    /// which is how the original works too — the walk keeps a running pointer and passes (x,y) only so the
    /// plot can apply its own two-pixel margin test.
    /// </summary>
    public void Plot(int x, int y)
    {
        var bits = (uint*)_pixelPin.AddrOfPinnedObject();
        var fn = (delegate* unmanaged[Cdecl]<void*, uint*, int, int, void>)MpvisModule.At(PlotVa);
        fn((byte*)_renderer + LineOffsetInRenderer, bits + (long)y * Width + x, x, y);
    }

    public void Dispose()
    {
        if (_pixelPin.IsAllocated) _pixelPin.Free();
        NativeMemory.Free(_surface);
        NativeMemory.Free(_renderer);
    }
}
