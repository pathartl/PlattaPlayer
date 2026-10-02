namespace PlattaPlayer.Visualizations.Wmp.Battery.Shifts;

/// <summary>
/// <c>CBatteryShiftTable</c>: a warp turned into a gather table. Battery never evaluates a warp per frame.
/// Each shift class supplies <see cref="FormShift"/>, an inverse map from a destination pixel to a source
/// pixel. This base runs it over the field once, three rows per frame, and the field is then
/// <c>dst[i] = src[table[i]]</c>.
///
/// A new table fades in from the one it replaces through 18 intermediate tables. Each holds for
/// <c>rand() % 15 + 1</c> frames (<see cref="GetData"/>).
///
/// Field names follow the x64 offsets so the oracle can read the real object back field by field.
/// </summary>
public abstract class ShiftTable : MemoryEffect
{
    /// <summary>Number of intermediate transition tables (+0x100..+0x188).</summary>
    public const int TransitionCount = 18;

    private readonly int[]?[] _transitions = new int[TransitionCount][];

    /// <summary>
    /// +0x1a0. When true, a source component that falls outside the field takes the destination's own
    /// coordinate on that axis. When false, it becomes 0 (+0x1a4/+0x1a8 are never written).
    /// </summary>
    public bool KeepOutOfRange { get; protected init; } = true;

    /// <summary>+0xe8/+0xec.</summary>
    public int W { get; private set; }

    public int H { get; private set; }

    /// <summary>+0xf0/+0xf4: <c>W &gt;&gt; 1</c>, <c>H &gt;&gt; 1</c>.</summary>
    public int Cx { get; private set; }

    public int Cy { get; private set; }

    /// <summary>+0xf8: the finished table, <c>src index = srcY * W + srcX</c>.</summary>
    public int[]? Table { get; private set; }

    /// <summary>+0x190. Equal to <see cref="TransitionCount"/> means no transition is running.</summary>
    public int TransitionIndex { get; private set; } = TransitionCount;

    /// <summary>+0x194. The constructor sets 15, and SetSize does NOT reset it, so the first step of a
    /// transition inherits whatever the previous one left behind.</summary>
    public int TransitionFrames { get; private set; } = 15;

    /// <summary>+0x198.</summary>
    public bool IsComplete { get; private set; }

    /// <summary>+0x199. Set while <see cref="Complete"/> finishes the table in one go; SetPixel then skips
    /// the transition tables.</summary>
    private bool _completing;

    /// <summary>+0x19c: the next row Setup builds.</summary>
    public int Row { get; private set; }

    /// <summary>+0x1b0: the table this one transitions from.</summary>
    public ShiftTable? Last { get; private set; }

    public int[]? TransitionTable(int i) => _transitions[i];

    /// <summary>
    /// The shift pool in the order <c>CRenderData</c>'s constructor builds it. CLinearShift appears twice,
    /// so random selection (<c>rand() % 15</c>) picks it twice as often. Preset lookup by name finds the
    /// first.
    /// </summary>
    public static ShiftTable[] CreatePool() =>
    [
        new LinearShift(), new LinearShift(), new ThingusShift(), new ZoomShift(), new RingSpinShift(),
        new StretchShift(), new TileShift(), new TrigShift(), new SinShimmerShift(), new EdgeFalloffShift(),
        new StarburstShift(), new SwirlShift(), new TrigStretchShift(), new Twirlocity(), new Shiitake(),
    ];

    /// <summary>Slot 8: the inverse warp. In: destination (col, row). Out: source. Out-of-range results
    /// are fine; <see cref="Setup"/> applies the per-axis policy.</summary>
    public abstract void FormShift(ref int x, ref int y, CrtRand rand);

    /// <summary>Slot 2 (<c>0x180412160</c>).</summary>
    public override void MarkDirty()
    {
        Row = 0;
        IsComplete = false;
    }

    /// <summary>Slot 5 (<c>0x180413480</c>).</summary>
    public void SetSize(int w, int h, bool transitions)
    {
        if (W == w && H == h && Table is not null) return;

        if (Table is not null)
        {
            Table = null;
            ClearTransition();
            SetLastShift(null);
        }

        W = H = Cx = Cy = 0;
        if ((w <= 0 && h <= 0) || (ulong)(uint)w * (uint)h >= 0x100000000UL) return;

        var n = w * h;
        Table = new int[n];
        if (!transitions)
        {
            TransitionIndex = TransitionCount;
        }
        else
        {
            TransitionIndex = 0;
            for (var i = 0; i < TransitionCount; i++) _transitions[i] = new int[n];
        }

        W = w;
        H = h;
        Cx = w >> 1;
        Cy = h >> 1;
        MarkDirty();
    }

    /// <summary><c>0x180410df8</c>.</summary>
    public void ClearTransition()
    {
        for (var i = 0; i < TransitionCount; i++) _transitions[i] = null;
        TransitionIndex = TransitionCount;
    }

    /// <summary><c>0x1804131cc</c>.</summary>
    public void SetLastShift(ShiftTable? last) => Last = last;

    /// <summary>Slot 6 (<c>0x180413630</c>): build at most three rows.</summary>
    public void Setup(CrtRand rand)
    {
        if (IsComplete || Table is null) return;

        if (Last is not null && (Last.Table is null || W != Last.W || H != Last.H))
        {
            Last = null;
            ClearTransition();
        }

        for (var n = 0; n < 3; n++)
        {
            var row = Row;
            for (var col = 0; col < W; col++)
            {
                int x = col, y = row;
                FormShift(ref x, ref y, rand);
                if (x < 0 || x >= W) x = KeepOutOfRange ? col : 0;
                if (y < 0 || y >= H) y = KeepOutOfRange ? row : 0;
                SetPixel(col, row, x, y);
            }

            Row = row + 1;
            if (Row >= H)
            {
                IsComplete = true;
                return;
            }
        }
    }

    /// <summary><c>0x1804132f8</c>.</summary>
    private void SetPixel(int col, int row, int x, int y)
    {
        var idx = W * row + col;
        Table![idx] = W * y + x;

        if (_completing || Last?.Table is not { } old || TransitionIndex >= TransitionCount) return;

        // The previous mapping is decoded with UNSIGNED division. Each intermediate step moves
        // i/19 of the way, truncated per axis: (double)i * (1/19) * (double)(int)(new - old).
        var o = (uint)old[idx];
        var oy = o / (uint)W;
        var ox = o % (uint)W;
        for (var i = 1; i <= TransitionCount; i++)
        {
            var f = i * BatteryMath.TransitionStep;
            var tx = BatteryMath.Trunc(f * (int)((uint)x - ox));
            var ty = BatteryMath.Trunc(f * (int)((uint)y - oy));
            _transitions[i - 1]![idx] = unchecked((int)(((uint)ty + oy) * (uint)W + ox + (uint)tx));
        }
    }

    /// <summary>Slot 7 (<c>0x180410e60</c>): make this the live table at the render data's size.</summary>
    public void Complete(int fieldW, int fieldH, CrtRand rand)
    {
        if (fieldW != W || fieldH != H) SetSize(fieldW, fieldH, false);

        if (Table is not null && !IsComplete)
        {
            TransitionIndex = TransitionCount;
            ClearTransition();
            _completing = true;
            while (!IsComplete) Setup(rand);
            _completing = false;
        }

        Last?.SetSize(0, 0, false);
    }

    /// <summary>
    /// <c>0x18041192c</c>: the table to gather through this frame. It steps through the transitions,
    /// drawing each step's hold time.
    /// </summary>
    public int[]? GetData(CrtRand rand)
    {
        var idx = TransitionIndex;
        if (idx < TransitionCount)
        {
            var frames = TransitionFrames;
            TransitionFrames = frames - 1;
            if (frames >= 1) return _transitions[idx];

            var r = rand.Next();
            TransitionIndex = ++idx;
            TransitionFrames = r % 15 + 1;
            if (idx < TransitionCount) return _transitions[idx];
            ClearTransition();
        }

        return Table;
    }

    // ---- the polar helpers every polar shift inlines (Win7 GetCenterData / SetCenterData) -------------

    /// <summary>Angle and radius of the vector from the pixel to the centre. dx/dy are int32, and so is
    /// the squared sum, which is converted to double only once.</summary>
    protected void GetCenterData(int x, int y, out double theta, out double r)
    {
        var dx = Cx - x;
        var dy = Cy - y;
        theta = BatteryMath.Atan2(dy, dx);
        r = BatteryMath.Sqrt(dy * dy + dx * dx);
    }

    /// <summary>Back to pixels. Each component is truncated separately, and y is written first.</summary>
    protected void SetCenterData(ref int x, ref int y, double theta, double r)
    {
        var ys = TruncPull(BatteryMath.Sin(theta) * r);
        var xs = TruncPull(BatteryMath.Cos(theta) * r);
        y = H - Cy - ys;
        x = W - Cx - xs;
    }

    // ---- device-resolution builds (the GPU path; never used by the original's field) -----------------

    /// <summary>
    /// 0 for the original's field. For a device-resolution clone (<see cref="CloneForDevice"/>), the number
    /// of device pixels per field pixel.
    /// </summary>
    public double DeviceScale { get; private set; }

    /// <summary>
    /// Subtracted from a polar offset's magnitude before <see cref="SetCenterData"/> truncates it. The
    /// original truncates at FIELD-pixel granularity, which on average pulls every source half a field
    /// pixel toward the centre each frame, and that pull is part of how its warps flow. A device build
    /// truncates device pixels, so it pulls (s - 1)/2 first to keep the pull at s/2 device pixels, as
    /// Alchemy's <c>ftrunc</c> does. 0 (exactly <see cref="BatteryMath.Trunc"/>) on the field.
    /// </summary>
    private double _truncBias;

    private int TruncPull(double v)
    {
        if (_truncBias == 0.0) return BatteryMath.Trunc(v);
        var a = Math.Abs(v) - _truncBias;
        if (a <= 0.0) return 0;
        var t = (int)Math.Floor(a);
        return v < 0.0 ? -t : t;
    }

    /// <summary>
    /// Rescale the parameters that are absolute field-pixel quantities (amplitudes, offsets, per-pixel
    /// frequencies) to device pixels. Everything else in the formulas already scales through W, H and the
    /// radius.
    /// </summary>
    protected virtual void ScaleForDevice(double s)
    {
    }

    /// <summary>
    /// A copy of this shift for a device-resolution table: the same class, this shift's parameters scaled
    /// by <paramref name="s"/>, sized <paramref name="w"/>×<paramref name="h"/>, without transitions. Its
    /// <see cref="FormShift"/> never draws from rand(), so it can run on any thread.
    /// </summary>
    public ShiftTable CloneForDevice(double s, int w, int h)
    {
        var c = (ShiftTable)Activator.CreateInstance(GetType())!;
        P.CopyTo(c.P, 0);
        c.ScaleForDevice(s);
        c.DeviceScale = s;
        c._truncBias = Math.Max(0.0, (s - 1) / 2);
        c.SetSize(w, h, false);
        return c;
    }

    /// <summary>One destination pixel of a device build: <see cref="FormShift"/> plus <see cref="Setup"/>'s
    /// out-of-range policy.</summary>
    public (int X, int Y) DeviceSource(int col, int row)
    {
        int x = col, y = row;
        FormShift(ref x, ref y, null!);
        if (x < 0 || x >= W) x = KeepOutOfRange ? col : 0;
        if (y < 0 || y >= H) y = KeepOutOfRange ? row : 0;
        return (x, y);
    }
}
