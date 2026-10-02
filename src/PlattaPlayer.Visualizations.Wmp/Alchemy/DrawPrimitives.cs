using System;
using PlattaPlayer.Visualizations.Wmp.Framebuffer;

namespace PlattaPlayer.Visualizations.Wmp.Alchemy;

/// <summary>
/// The shared drawing/colour primitives the Alchemy effects render with (analysis §B.7): a per-pixel
/// plot+decay writer with several dot styles, a line built from those plots, and a radial spline that
/// offsets interpolated points perpendicular by a sine-table value (used by the Scope and Balls).
/// </summary>
public sealed class DrawPrimitives
{
    /// <summary>
    /// Neighbour blend factor for mode 2/3 plots — <c>CTLineRender</c>'s field at +0x40, which is a
    /// SINGLE, so this is one too. The ring renderer sets it to 0.5; the rose sets 0.1. Remember the
    /// sense: this is the weight of what is ALREADY THERE, so a larger value means a fainter stroke.
    /// </summary>
    public float NeighbourAlpha { get; set; } = 0.5f;

    /// <summary>
    /// The SPLINE's colour transition, <c>CTWaveformRender+0x74</c>. Each renderer sets it up once per
    /// frame with <see cref="BeginStroke"/>, and <see cref="Spline"/> steps it once per POINT: every
    /// segment is drawn in one solid colour, the value the transition held before that point's step.
    ///
    /// This used to step once per PLOTTED PIXEL, from <c>FUN_18000b730</c>'s
    /// <c>if (hold == 0) AdvanceColorTransition</c>. The plot's hold byte is never clear while a renderer
    /// draws, though: the spline's per-point callback (<c>OnStartWaveformLine</c>, <c>0x18000b970</c>) sets
    /// it before the first segment and copies the pre-step colour into the plot. That per-pixel walk
    /// consumed a random draw every time a transition expired, and none of those draws happen in the
    /// original.
    /// </summary>
    public PaletteCycler? Palette { get; set; }

    /// <summary>Engine RNG, needed because a transition that runs out draws its next target.</summary>
    public Random? Random { get; set; }

    /// <summary>
    /// When set, <see cref="Spline"/> and <see cref="DrawDisc"/> hand their geometry here instead of
    /// rasterizing it. The GPU path uses this to redraw the strokes at window resolution. The colour
    /// walk and its random draws run exactly as they do when rasterizing, because they happen per POINT,
    /// not per plotted pixel.
    /// </summary>
    public Gpu.IStrokeRecorder? Recorder { get; set; }

    /// <summary>
    /// <c>FUN_18000bf70</c> as the renderers call it: start the stroke's colour transition running from
    /// <paramref name="from"/> toward <paramref name="to"/>.
    /// </summary>
    public void BeginStroke(int from, int to, int period, int steps = 1) =>
        Palette?.Begin(from, to, period, steps);

    /// <summary>
    /// <c>FUN_18000b730</c> — the stroke footprint, which is the single biggest reason our output does not
    /// look like the original's.
    ///
    /// Three things here were wrong, and all three change the character of every stroke in the effect:
    /// <list type="number">
    /// <item>The footprint. Modes 2 and 3 write a solid centre, blend the FOUR NEIGHBOURS, and then run a
    /// five-tap box blur over the whole 3x3 block. That is what makes the original's rays read as smooth
    /// glowing strands. This drew a single pixel with an alpha-blended square/halo and no blur.</item>
    /// <item>The SENSE of the blend. <c>InterpolateRGB(colour, existing, t)</c> weights the EXISTING
    /// pixel by <c>t</c>, so mode 0's 0.9 is a 10%-strength dot, not a 90% one. Ours treated the same
    /// number as near-opaque, making the faintest mode the strongest.</item>
    /// <item>The margin is TWO pixels, not one — strokes never reach the border, which is also what makes
    /// the 3x3 footprint safe to write unchecked.</item>
    /// </list>
    /// </summary>
    public void Plot(PixelBuffer buffer, int x, int y, int color, int mode)
    {
        if (x < 2 || y < 2 || x >= buffer.Width - 2 || y >= buffer.Height - 2) return;

        var pixels = buffer.Pixels;
        var stride = buffer.Width;
        var p = y * stride + x;

        switch (mode)
        {
            case 0:
                pixels[p] = PixelBuffer.LerpArgb(color, pixels[p], 0.9);
                break;

            case 1:
                pixels[p] = color;
                pixels[p + 1] = PixelBuffer.LerpArgb(color, pixels[p + 1], 0.3);
                pixels[p - 1] = PixelBuffer.LerpArgb(color, pixels[p - 1], 0.3);
                pixels[p + stride] = PixelBuffer.LerpArgb(color, pixels[p + stride], 0.3);
                pixels[p - stride] = PixelBuffer.LerpArgb(color, pixels[p - stride], 0.3);
                break;

            default:
                var a = NeighbourAlpha;
                pixels[p] = color;
                pixels[p + 1] = PixelBuffer.LerpArgb(color, pixels[p + 1], a);
                pixels[p - 1] = PixelBuffer.LerpArgb(color, pixels[p - 1], a);
                pixels[p + stride] = PixelBuffer.LerpArgb(color, pixels[p + stride], a);
                pixels[p - stride] = PixelBuffer.LerpArgb(color, pixels[p - stride], a);
                // FUN_18000b650 over the 3x3 block, in raster order.
                for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                    Blur5(pixels, p + dy * stride + dx, stride);
                break;
        }

    }

    /// <summary>
    /// <c>FUN_18000b650</c>: replace a pixel with the per-channel mean of itself and its four neighbours.
    /// The same <c>sum / 5</c> the blur effect's lookup table is built from.
    /// </summary>
    private static void Blur5(int[] pixels, int i, int stride)
    {
        int c = pixels[i], l = pixels[i - 1], r = pixels[i + 1], u = pixels[i - stride], d = pixels[i + stride];
        var rr = (((c >> 16) & 0xFF) + ((l >> 16) & 0xFF) + ((r >> 16) & 0xFF)
                + ((u >> 16) & 0xFF) + ((d >> 16) & 0xFF)) / 5;
        var gg = (((c >> 8) & 0xFF) + ((l >> 8) & 0xFF) + ((r >> 8) & 0xFF)
                + ((u >> 8) & 0xFF) + ((d >> 8) & 0xFF)) / 5;
        var bb = ((c & 0xFF) + (l & 0xFF) + (r & 0xFF) + (u & 0xFF) + (d & 0xFF)) / 5;
        pixels[i] = unchecked((int)0xFF000000) | (rr << 16) | (gg << 8) | bb;
    }

    /// <summary>
    /// The stroke margin at <c>CTLineRender+0x00</c>. The renderers derive the clip box from it every
    /// frame as <c>maxX = surfaceWidth - margin</c>, <c>maxY = surfaceHeight - margin</c>; the rose picks
    /// 0, 1 or 9 from its RenderMode.
    /// </summary>
    public int ClipMargin { get; set; }

    /// <summary>
    /// One line segment, exactly as the spline draws them: clip against the box
    /// (<c>FUN_18000b0a0</c>), then walk (<c>FUN_18000b270</c>).
    ///
    /// This replaced a textbook symmetric-error Bresenham, which differed from the original in three ways
    /// that each move pixels:
    /// <list type="number">
    /// <item><b>The endpoint is never plotted.</b> The real walk runs exactly <c>max(|dx|,|dy|)</c>
    /// iterations, plotting the CURRENT point and then stepping, so a zero-length line draws nothing at
    /// all. Plotting it as well over-painted every spline joint — and since each plot steps the walking
    /// palette, it also advanced the colour once too often per segment.</item>
    /// <item><b>The error term is the asymmetric form</b> with a strict <c>&gt;</c> test, which breaks ties
    /// the opposite way from the <c>err = dx + dy</c> variant on near-diagonals.</item>
    /// <item><b>Clipping happens first, and it is not a truncation.</b> Trimming a segment moves where the
    /// walk STARTS, so the interior pixels of a partly off-surface line are not a subset of the unclipped
    /// line's. Relying on <see cref="Plot"/>'s own margin test to swallow the strays gave a different
    /// pattern, not merely a different extent.</item>
    /// </list>
    /// </summary>
    public void Line(PixelBuffer buffer, int x0, int y0, int x1, int y1, int color, int mode)
    {
        var lo = ClipMargin;
        if (!Clip(lo, buffer.Width - lo, buffer.Height - lo, ref x0, ref y0, ref x1, ref y1)) return;
        Walk(buffer, x0, y0, x1, y1, color, mode);
    }

    /// <summary>
    /// <c>FUN_18000b0a0</c>. Both the slope and the intercept are computed in FLOAT and each clipped
    /// coordinate goes through <see cref="PixelBuffer.MpvisRound"/>, so the trimmed endpoint can land a
    /// pixel away from what a double-precision version would give. The vertical case takes a slope of
    /// exactly 1 rather than an infinity, which is why the y-clamps below can divide by it unguarded.
    /// </summary>
    private static bool Clip(int lo, int maxX, int maxY, ref int x0, ref int y0, ref int x1, ref int y1)
    {
        if (x0 < lo)
        {
            if (x1 < lo) return false;
        }
        else if (x0 < maxX && lo <= y0 && y0 < maxY &&
                 lo <= x1 && x1 < maxX && lo <= y1 && y1 < maxY)
        {
            return true;   // wholly inside: the original skips the clip entirely
        }

        if (maxX <= x0 && maxX <= x1) return false;
        if (y0 < lo && y1 < lo) return false;
        if (maxY <= y0 && maxY <= y1) return false;

        var m = x1 - x0 == 0 ? 1f : (float)(y1 - y0) / (x1 - x0);
        var c = (float)y0 - (float)x0 * m;

        if (x0 < lo) { x0 = lo; y0 = PixelBuffer.MpvisRound((float)x0 * m + c); }
        else if (maxX <= x0) { x0 = maxX - 1; y0 = PixelBuffer.MpvisRound((float)x0 * m + c); }

        if (y0 < lo) { y0 = lo; x0 = PixelBuffer.MpvisRound(((float)y0 - c) / m); }
        else if (maxY <= y0) { y0 = maxY - 1; x0 = PixelBuffer.MpvisRound(((float)y0 - c) / m); }

        if (x1 < lo) { x1 = lo; y1 = PixelBuffer.MpvisRound((float)x1 * m + c); }
        else if (maxX <= x1) { x1 = maxX - 1; y1 = PixelBuffer.MpvisRound((float)x1 * m + c); }

        if (y1 < lo) { y1 = lo; x1 = PixelBuffer.MpvisRound(((float)y1 - c) / m); }
        else if (maxY <= y1) { y1 = maxY - 1; x1 = PixelBuffer.MpvisRound(((float)y1 - c) / m); }

        // A segment that crosses a corner can still be outside after one pass of clamps; the original
        // simply drops it rather than iterating.
        return x0 >= lo && x0 < maxX && y0 >= lo && y0 < maxY &&
               x1 >= lo && x1 < maxX && y1 >= lo && y1 < maxY;
    }

    /// <summary>
    /// <c>FUN_18000b270</c>. Note the tie: at exactly 45 degrees <c>ady &lt; adx</c> is false, so the
    /// y-major branch runs.
    /// </summary>
    private void Walk(PixelBuffer buffer, int x, int y, int x1, int y1, int color, int mode)
    {
        var dx = x1 - x;
        var dy = y1 - y;
        var adx = Math.Abs(dx);
        var ady = Math.Abs(dy);
        var xStep = dx < 0 ? -1 : 1;
        var yStep = dy < 0 ? -1 : 1;
        var e = 0;

        if (ady < adx)
        {
            for (var i = adx; i > 0; i--)
            {
                Plot(buffer, x, y, color, mode);
                e += ady;
                if (e > adx) { y += yStep; e -= adx; }
                x += xStep;
            }
        }
        else
        {
            for (var i = ady; i > 0; i--)
            {
                Plot(buffer, x, y, color, mode);
                e += adx;
                if (e > ady) { e -= ady; x += xStep; }
                y += yStep;
            }
        }
    }

    /// <summary>
    /// <c>CTWaveformRender::RenderWaveform</c> (<c>0x18000ba50</c>): a stroke from (x0,y0) toward (x1,y1)
    /// whose points are displaced perpendicular to it by the waveform (<see cref="SplineOffsets"/>).
    ///
    /// Everything about the point count, the colour and the optional second stroke comes from the
    /// original, and several of those were missing here:
    /// <list type="number">
    /// <item><b>N is derived, not passed.</b> <c>N = min(max(|dx|,|dy|) + 1, stroke.MaxSteps)</c>, and the
    /// offsets are computed for that N.</item>
    /// <item><b>N points, N−1 segments, and the last point is not the endpoint.</b> Points run
    /// <c>i = 0 .. N-1</c> at parameter <c>i / N</c>, so the stroke stops one step short of (x1,y1).</item>
    /// <item><b>Every coordinate is rounded in FLOAT, half away from zero</b>
    /// (<see cref="PixelBuffer.MpvisRound"/>).</item>
    /// <item><b>The perpendicular is trigonometric and computed in FLOAT</b>:
    /// <c>theta = (float)atan2(dy, dx) + 1.5707964f</c>, widened only for cos and sin
    /// (<c>0x18000bae2</c>). On an axis-aligned chord the exact ratio gives 0 where this leaves about
    /// -4e-8, and that residue flips every exact .5 the interpolation lands on.</item>
    /// <item><b>The colour walks once per POINT.</b> The transition's period is first set to
    /// <c>N / steps</c>. Then a virtual callback (<c>OnStartWaveformLine</c>) runs once before the first
    /// segment and once after every segment. Each call hands the plot the transition's CURRENT colour
    /// and then steps it. The plot never steps anything itself.</item>
    /// <item><b><see cref="WaveformStroke.Mirror"/> (<c>+0x9a</c>) draws a second stroke</b> on the
    /// negated offsets, sharing each point's colour.</item>
    /// </list>
    /// </summary>
    public void Spline(PixelBuffer buffer, int x0, int y0, int x1, int y1, WaveformStroke stroke,
                       byte[] waveform0, byte[] waveform1, int fallbackColour = unchecked((int)0xFFFFFFFF))
    {
        var dx = x1 - x0;
        var dy = y1 - y0;
        var major = Math.Abs(Math.Abs(dx) <= Math.Abs(dy) ? dy : dx);
        var n = Math.Min(major + 1, stroke.MaxSteps);
        if (_offsets.Length < n + 1) _offsets = new double[n + 1];
        SplineOffsets.Fill(_offsets.AsSpan(0, n), waveform0, waveform1, n, stroke.Amplitude, stroke.Source,
                           stroke.Envelope, stroke.Lobes, stroke.SampleMirror);

        var theta = (float)Math.Atan2(dy, dx) + (float)(MpvisMath.Pi / 2);
        var px = (float)Math.Cos(theta);
        var py = (float)Math.Sin(theta);
        var fx0 = (float)x0;
        var fy0 = (float)y0;

        var off = (float)_offsets[0];
        var prevX = PixelBuffer.MpvisRound(fx0 + off * px);
        var prevY = PixelBuffer.MpvisRound(fy0 + off * py);
        var mirrorX = 0;
        var mirrorY = 0;
        if (stroke.Mirror)
        {
            mirrorX = PixelBuffer.MpvisRound(fx0 - off * px);
            mirrorY = PixelBuffer.MpvisRound(fy0 - off * py);
        }

        // The same points before rounding, for the recorder.
        var recorder = Recorder;
        float prevFx = fx0 + off * px, prevFy = fy0 + off * py;
        float mirrorFx = fx0 - off * px, mirrorFy = fy0 - off * py;

        if (Palette is not null && Palette.Steps > 0) Palette.SetPeriod(n / Palette.Steps);
        var colour = NextPointColour(stroke, fallbackColour);

        for (var i = 1; i < n; i++)
        {
            var t = (float)i / (float)n;
            off = (float)_offsets[i];
            var bx = (float)dx * t + fx0;
            var by = (float)dy * t + fy0;
            var ox = off * px;
            var oy = off * py;
            var ix = PixelBuffer.MpvisRound(ox + bx);
            var iy = PixelBuffer.MpvisRound(oy + by);
            if (recorder is null)
            {
                Line(buffer, prevX, prevY, ix, iy, colour, stroke.PlotMode);
            }
            else
            {
                recorder.Segment(prevFx, prevFy, ox + bx, oy + by, colour, stroke.PlotMode, NeighbourAlpha, ClipMargin);
                prevFx = ox + bx;
                prevFy = oy + by;
            }

            if (stroke.Mirror)
            {
                var mx = PixelBuffer.MpvisRound(bx - ox);
                var my = PixelBuffer.MpvisRound(by - oy);
                if (recorder is null)
                {
                    Line(buffer, mirrorX, mirrorY, mx, my, colour, stroke.PlotMode);
                }
                else
                {
                    recorder.Segment(mirrorFx, mirrorFy, bx - ox, by - oy, colour, stroke.PlotMode, NeighbourAlpha, ClipMargin);
                    mirrorFx = bx - ox;
                    mirrorFy = by - oy;
                }
                mirrorX = mx;
                mirrorY = my;
            }

            colour = NextPointColour(stroke, fallbackColour);
            prevX = ix;
            prevY = iy;
        }
    }

    private double[] _offsets = new double[64];

    /// <summary>
    /// <c>OnStartWaveformLine</c>: the colour for the next segment is the transition's value BEFORE this
    /// call steps it. With the stroke's hold byte set (<c>+0x98</c>, the constructor default) nothing steps.
    /// </summary>
    private int NextPointColour(WaveformStroke stroke, int fallback)
    {
        if (Palette is null) return fallback;
        var colour = Palette.Current;
        if (stroke.WalkColour && Random is not null) Palette.Advance(Random);
        return colour;
    }

    /// <summary>
    /// <c>FUN_18000fbc4</c>, the soft disc — the whole of the atom balls.
    ///
    /// The shape is not what it looks like from a screenshot. It is driven by
    /// <c>d2 = (r² − (dx² + dy²)) / r</c>, which is NOT a normalised radius: it is a squared-distance
    /// residue scaled by <c>r</c>, so it runs from <c>r</c> at the centre down through zero at the rim and
    /// on NEGATIVE outside. That single quantity drives both passes, and the two ranges overlap:
    /// <list type="bullet">
    /// <item>The <b>core</b> runs wherever <c>d2 &gt;= 1</c>, blending toward the fill with a weight of
    /// <c>cos((d2/r)² · π/2) · alpha</c>. At the centre <c>d2/r = 1</c>, the cosine is zero, and — because
    /// the blend weights what is ALREADY THERE — the fill lands solid. Toward the rim the weight rises to
    /// <c>alpha</c> and the fill fades out.</item>
    /// <item>The <b>rim</b> runs on a band around <c>d2 = 0</c>, roughly <c>±3.14</c> wide, which sits just
    /// inside and just OUTSIDE the disc — so the ring is painted over bare background as well as over the
    /// core, and the two overlap in a narrow annulus.</item>
    /// </list>
    ///
    /// The previous version was invented: it normalised the distance, rejected everything past a circular
    /// <c>d &gt; 1</c>, used a raised cosine over the full radius, switched to the rim colour past a hard
    /// 0.85 threshold, and composited through a synthesised alpha channel. It produced a soft dot, which
    /// is why it survived visual inspection, but it shares no term with the original.
    ///
    /// <paramref name="invert"/> is the object's flag at +0x138, which a bass hit toggles. Note what it
    /// actually does: it takes the ONE'S COMPLEMENT of the fill colour. It does not swap fill and rim.
    /// </summary>
    public static void Disc(PixelBuffer buffer, int cx, int cy, int radius, float alpha,
                            int fill, int rim, bool invert)
    {
        if (radius < 1) return;
        RasterizeDisc(buffer, cx, cy, radius, alpha, fill, rim, invert);
    }

    /// <summary><see cref="Disc"/>, or the <see cref="Recorder"/> when one is set.</summary>
    public void DrawDisc(PixelBuffer buffer, int cx, int cy, int radius, float alpha, int fill, int rim, bool invert)
    {
        if (radius < 1) return;
        if (Recorder is { } recorder) recorder.Disc(cx, cy, radius, alpha, fill, rim, invert);
        else RasterizeDisc(buffer, cx, cy, radius, alpha, fill, rim, invert);
    }

    private static void RasterizeDisc(PixelBuffer buffer, int cx, int cy, int radius, float alpha,
                                      int fill, int rim, bool invert)
    {
        // 0xFFFFFF - colour, keeping our opaque alpha (the original renders to BGRX and ignores it).
        var colour = invert
            ? unchecked((int)0xFF000000) | (0xFFFFFF - (fill & 0xFFFFFF))
            : fill;

        var w = buffer.Width;
        var h = buffer.Height;
        var pixels = buffer.Pixels;

        // Both of these are formed in INTEGER arithmetic and only then widened, exactly as the original
        // does — the squared terms never touch a double.
        var rSq = (double)(radius * radius);
        var rD = (double)radius;

        for (var x = cx - radius; x <= cx + radius; x++)
        for (var y = cy - radius; y <= cy + radius; y++)
        {
            // Unsigned compare, so a negative coordinate falls out with the overruns. Note this is a
            // SQUARE scan: there is no circular reject, the falloff alone shapes the disc.
            if ((uint)x >= (uint)w || (uint)y >= (uint)h) continue;

            var dx = x - cx;
            var dy = y - cy;
            var d2 = (rSq - (dx * dx + dy * dy)) / rD;

            var i = y * w + x;
            var c = pixels[i];

            if (d2 >= 1.0)
            {
                var t = d2 / rD;
                c = PixelBuffer.LerpArgb(colour, c, Math.Cos(t * t * MpvisMath.HalfPi) * alpha);
                pixels[i] = c;
            }

            if (d2 > -MpvisMath.DiscRimLimit && d2 < MpvisMath.DiscRimLimit)
            {
                // cos is even, so the original's abs() on d2 changes nothing — kept only because that is
                // what the instruction stream does (`xorps` against the sign mask at 0x18000fd83).
                pixels[i] = PixelBuffer.LerpArgb(c, rim, (Math.Cos(Math.Abs(d2)) + 1.0) * 0.5);
            }
        }
    }
}

/// <summary>
/// Palette cycler (analysis §B.7 <c>FUN_18000afd4</c>): eases sinusoidally between random colours over a
/// period of frames, giving the slowly-shifting hues the effects draw with.
/// </summary>
public sealed class PaletteCycler
{
    /// <summary>
    /// <c>CTColor</c>'s period base at +0x20, set to 100 by its constructor (<c>FUN_18000aed8</c>). Each
    /// transition lasts <c>rand() % 100</c> frames.
    /// </summary>
    private const int PeriodBase = 100;

    private int _from;
    private int _to;
    private int _period;
    private int _counter;

    /// <summary>Step count from <c>FUN_18000bf70</c>; greater than one selects the ping-pong transition.</summary>
    private int _steps = 1;

    /// <summary>
    /// <c>FUN_18000aed8</c>: both endpoints random, current starting on <c>from</c>, and counter and
    /// period both ZERO — so the very first <see cref="Advance"/> immediately starts a fresh transition
    /// and the constructor's <c>to</c> is discarded unused. That is what the original does.
    /// </summary>
    public PaletteCycler(Random random)
    {
        _from = RandomColor(random);
        _to = RandomColor(random);
        Current = _from;
    }

    /// <summary>A cycler with no colours yet, for a transition that is always set up before use.</summary>
    public PaletteCycler() { }

    public int Current { get; private set; }

    /// <summary>The step count, <c>CTColor</c>'s <c>[6]</c>.</summary>
    public int Steps => _steps;

    /// <summary>
    /// Overwrite the period (<c>CTColor</c>'s <c>[4]</c>) without restarting. <c>RenderWaveform</c> sets
    /// it to <c>N / steps</c> so a stroke's colour runs through its steps exactly once.
    /// </summary>
    public void SetPeriod(int period) => _period = period;

    /// <summary>
    /// <c>FUN_18000bf70</c> with a step count of 1: start an explicit transition. Effects whose
    /// randomizers seed their own palettes call this rather than letting the cycler roll its own — the
    /// atom balls do, drawing a duration and then TWO colours, of which the SECOND becomes the start
    /// point and the first the target.
    /// </summary>
    public void Begin(int from, int to, int period) => Begin(from, to, period, steps: 1);

    /// <summary>
    /// <c>FUN_18000bf70</c> in full, including the MULTI-STEP form the chord renderers use.
    ///
    /// With <paramref name="steps"/> greater than one the period is divided by it and a flag is set; the
    /// transition then PING-PONGS — on expiry it restarts with <c>from</c> and <c>to</c> swapped rather
    /// than drawing a new random target. That is what makes a stroke's colour oscillate smoothly between
    /// two palette entries instead of wandering at random, and it is why the effect reads as broad bands
    /// of two hues rather than noise.
    /// </summary>
    public void Begin(int from, int to, int period, int steps, Random? random = null)
    {
        // FUN_18000bf70's first act: a duration below 1 is REDRAWN as rand()%100 + 1. This matters far
        // more than it looks — the renderers start their strokes with a duration of 1 and a step count
        // above it, so the divided period is zero, the transition expires on the very first pixel, and
        // every restart from then on lands in this redraw. Skipping it left the colour pinned at an
        // endpoint instead of sweeping, which is what made strokes look flat and dull.
        if (period < 1) period = (random?.Next(PeriodBase) ?? 0) + 1;

        _counter = 0;
        _steps = steps;
        _period = steps > 1 ? period / steps : period;
        _from = from;
        _to = to;
        Current = from;
    }

    /// <summary>
    /// One frame of <c>FUN_18000afd4</c> (single-step path) plus <c>FUN_18000bf70</c>.
    ///
    /// Three things here were wrong and all of them drift rather than break, so nothing about the output
    /// pointed at them. The period is <c>rand() % 100</c>, not <c>60 + rand() % 120</c> — transitions are
    /// on average a third as long, so the palette moves noticeably faster. The new <c>from</c> is the
    /// CURRENT interpolated colour, not the old target, which matters because a transition is cut off
    /// mid-ease and restarting from the target would jump. And the boundary is <c>counter &gt; period</c>
    /// with no interpolation on the frame a transition starts.
    /// </summary>
    public void Advance(Random random)
    {
        _counter++;
        if (_counter > _period)
        {
            // Multi-step transitions swap their endpoints and run again; only a single-step one rolls a
            // fresh random target (FUN_18000afd4's two branches).
            if (_steps > 1)
            {
                Begin(_to, _from, _steps * _period, _steps, random);
                return;
            }

            var duration = random.Next(PeriodBase);
            var next = RandomColor(random);
            // FUN_18000bf70 redraws when the duration came out zero, so a transition is never instant.
            if (duration < 1) duration = random.Next(PeriodBase) + 1;

            _counter = 0;
            _period = duration;
            _from = Current;
            _to = next;
            Current = _from;
            return;
        }

        // Quarter-sine ease. The division is in FLOAT in the original, and its result is only then
        // widened for the sine — narrowing it here keeps the last bits of t identical.
        var t = Math.Sin((float)_counter / (float)_period * (MpvisMath.Pi / 2));
        Current = PixelBuffer.LerpArgb(_from, _to, t);
    }

    /// <summary>
    /// mpvis's CTColor::RandomColor (FUN_18000b9f8): three independent rand() draws, each taken as a
    /// FULL byte.
    ///
    /// The previous version clamped every channel to 100..255 on the theory that bright colours read
    /// better over a dark field. The effect of that was the opposite: with no channel ever near zero
    /// every colour averaged toward light grey, so the whole visualization looked pale and washed out
    /// where the real one is vividly saturated. Letting channels reach 0 is exactly what produces the
    /// strong greens, magentas and oranges the real effect shows.
    /// </summary>
    public static int RandomColor(Random random)
    {
        // Channel ORDER, from the assembled return value `b3 | b2 << 16 | b1 << 8`: the first draw is
        // GREEN, the second RED, the third BLUE. The obvious r/g/b reading is wrong, and since all three
        // are uniform bytes the mistake is invisible in isolation — it only shows once the draws have to
        // line up with the DLL's, which is what verify-color does.
        var g = random.Next(256);
        var r = random.Next(256);
        var b = random.Next(256);
        return unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
    }
}
