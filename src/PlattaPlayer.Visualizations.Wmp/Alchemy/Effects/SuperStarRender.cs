using System;
using PlattaPlayer.Visualizations.Wmp.Framebuffer;

namespace PlattaPlayer.Visualizations.Wmp.Alchemy.Effects;

/// <summary>
/// THE NAMES OF THIS CLASS AND <see cref="WonderWaveRender"/> WERE SWAPPED, and were swapped for a long
/// time — do not "correct" them back. The PDB settles it: <c>0x18000fe50</c> is
/// <c>CTRenderSuperStar::NormalRender</c>, and it draws what is below.
///
/// <c>CTRenderSuperStar::NormalRender</c>: a spinning STAR POLYGON, checked frame by frame against the
/// real function by the harness verb <c>verify-normalrender</c>. It has D = <see cref="_divisions"/>
/// vertices on a circle of radius <c>bass · ((H&gt;&gt;1) · Scale)</c> about <c>(W&gt;&gt;1, H&gt;&gt;1)</c>.
/// Each edge SKIPS k vertices, <c>k = D/2 + 1</c> (plus one when D is 10 or 14), so the step is
/// <c>2π/D · k</c> and the figure is a star, not a ring. It draws D+1 edges from the current angle,
/// which the treble advances. Each edge is a waveform spline (50 points, amplitude 50, the constructor's
/// mean-of-channels source with a mirrored sample index), in plot mode 3 at neighbour alpha 0.5. The
/// colour walks per point from palette A toward palette B in 3 steps.
///
/// The previous version drew a ring of adjacent vertices (D edges, no skip) from a double-precision
/// centre, clamped the radius, sampled channel 0 unmirrored, and restarted the transition per edge.
/// </summary>
public sealed class SuperStarRender : AlchemyEffect
{
    /// <summary><c>0x401921fb60000000</c>: 2π from the FLOAT π, used for the step and the wrap.</summary>
    private const double TwoPi = MpvisMath.Pi * 2;

    private double _scale = 0.6;
    private int _divisions = 6;
    private double _maxSpin = 0.15;

    /// <summary><c>+0xf8</c>. Not reset by <see cref="Randomize"/>.</summary>
    private double _angle;

    /// <summary>The spline at <c>+0x108</c>: every field the render body writes each frame.</summary>
    private readonly WaveformStroke _stroke = new()
    {
        MaxSteps = 50, Amplitude = 50, Envelope = 0, Lobes = 2, WalkColour = true, PlotMode = 3,
    };

    private readonly PaletteCycler _paletteA;
    private readonly PaletteCycler _paletteB;

    /// <summary>
    /// The constructor builds five <c>CTColor</c>s, each drawing two random colours: a spare line
    /// renderer's, palettes A and B, then the spline's line renderer's and the spline's own. Only A and B
    /// are ever read before being set up again, but all five consume draws.
    /// </summary>
    public SuperStarRender(Random random)
    {
        IsDraw = true;
        _ = new PaletteCycler(random);
        _paletteA = new PaletteCycler(random);
        _paletteB = new PaletteCycler(random);
        _ = new PaletteCycler(random);
        _ = new PaletteCycler(random);
    }

    public override string Name => "Super Star";

    /// <summary><c>0x180010760</c>, verified by <c>verify-renders</c>.</summary>
    public override void Randomize(Random random)
    {
        _scale = 0.2 + random.NextDouble() * 0.8;      // 0.2..1.0

        // The adjustment triggers when the raw draw is 2 or 3 (Divisions 5 or 6); +2 and +15 are
        // EXCLUSIVE alternatives; and the extra rand() is consumed only when the adjustment applies.
        var draw = random.Next(12);
        _divisions = draw + 3;                          // 3..14
        if (draw is 2 or 3) _divisions += random.Next(3) == 0 ? 15 : 2;

        _maxSpin = random.NextDouble() * 0.6 - 0.3;     // -0.3..0.3
    }

    public override void Tick(EffectContext ctx)
    {
        // Only the POSITIVE overflow wraps; a negative MaxSpin runs the angle negative indefinitely.
        _angle = ctx.Audio.Treble * _maxSpin + _angle;
        if (_angle > TwoPi) _angle -= TwoPi;
    }

    /// <summary>The bass sum is read straight from the bins, which is the same number as <c>BassNow</c>.</summary>
    private double Radius(EffectContext ctx) => ctx.Audio.BassNow * ((ctx.FocusY >> 1) * _scale);

    /// <summary>The vertex skip: <c>k = D/2 + 1</c>, one more for D of 10 or 14.</summary>
    private double Step()
    {
        var k = (_divisions >> 1) + 1;
        if (_divisions is 10 or 14) k++;
        return TwoPi / _divisions * k;
    }

    private (int x, int y) Vertex(EffectContext ctx, double angle, double r) =>
        ((int)(Math.Cos(angle) * r) + (ctx.FocusX >> 1), (int)(Math.Sin(angle) * r) + (ctx.FocusY >> 1));

    public override void Draw(PixelBuffer buffer, EffectContext ctx)
    {
        var r = Radius(ctx);
        var step = Step();
        var angle = _angle;
        var (x, y) = Vertex(ctx, angle, r);

        _paletteA.Advance(ctx.Random);
        _paletteB.Advance(ctx.Random);

        var draw = ctx.Draw;
        draw.BeginStroke(_paletteA.Current, _paletteB.Current, 1, 3);
        draw.ClipMargin = 0;
        draw.NeighbourAlpha = 0.5f;

        for (var i = 0; i <= _divisions; i++)
        {
            angle += step;
            var (nx, ny) = Vertex(ctx, angle, r);
            draw.Spline(buffer, x, y, nx, ny, _stroke, ctx.Audio.Waveform0, ctx.Audio.Waveform1);
            (x, y) = (nx, ny);
        }
    }
}
