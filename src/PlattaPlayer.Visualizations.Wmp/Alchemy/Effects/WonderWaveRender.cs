using System;
using PlattaPlayer.Visualizations.Wmp.Framebuffer;

namespace PlattaPlayer.Visualizations.Wmp.Alchemy.Effects;

/// <summary>
/// THE NAMES OF THIS CLASS AND <see cref="SuperStarRender"/> WERE SWAPPED — see the note there before
/// changing either. The PDB settles it: <c>0x180010120</c> is <c>CTRenderWonderWave::NormalRender</c>.
///
/// <c>CTRenderWonderWave::NormalRender</c>: a rotating CHORD through the field centre, one field-diagonal
/// long, drawn as a waveform spline. It is checked frame by frame against the real function by
/// <c>verify-normalrender</c>.
///
/// <b>The chords are STRAIGHT.</b> The render body sets the spline amplitude to
/// <c>(int)Spin * fieldHeight</c>. It truncates Spin BEFORE multiplying (<c>cvttsd2si ecx, [rsi+0x60]</c>
/// then <c>imul</c>, at <c>0x1800101fa</c>), and Spin is 0.10..0.425, so the amplitude is always zero.
/// The port had read it as <c>(int)(Spin * H)</c> and bent every chord 48..204 px with the live waveform.
/// That was the single biggest source of excess ink, and it is why the early captures of the isolated
/// real renderer showed "a fan of straight chords".
///
/// The rest, all from the same function:
/// <list type="bullet">
/// <item>The angle (<c>+0x120</c>) advances AFTER drawing by <c>bass²·SpinDelta</c>, <c>bass·SpinDelta</c>
/// or <c>SpinDelta</c> for SpinMode 0/1/2, and not at all for 3. SpinDelta, not Spin.</item>
/// <item>"Mirrored" (<c>+0x74</c>) does not mirror anything. It scales the chord length by the bass.</item>
/// <item>A second chord is drawn when CrossMode (<c>+0x76</c>) is set, at an angle chosen by BassFlex. The
/// cross offset (<c>+0x128</c>, the randomizer's "Phase") drifts by <c>SpinDelta·bass/2</c> in BassFlex 0.</item>
/// <item>CrossLine (<c>+0x75</c>) is copied to the spline's mirror flag by <c>OnSetParams</c>, so the
/// chord is drawn twice on opposite offsets. With zero amplitude the two copies coincide.</item>
/// <item>The plot mode is RenderMode, with a clip margin of 0, 1 or 9 and a neighbour alpha of 0.1.</item>
/// </list>
/// </summary>
public sealed class WonderWaveRender : AlchemyEffect
{
    /// <summary><c>0x3fc90fdb</c>: π/2 as a FLOAT, added to the (float) angle.</summary>
    private const float HalfPiF = 1.5707964f;

    private int _renderMode;
    private int _sinLoops = 10;
    private int _scalePct;
    private int _subdivisions = 1;
    private double _spin = 0.2;
    private double _spinDelta;
    private int _spinMode;
    private int _bassFlex;
    private bool _mirrored;
    private bool _crossLine;
    private bool _crossMode;

    /// <summary><c>+0x128</c>: the cross chord's offset from the main angle ("Phase" in the randomizer).</summary>
    private double _phase;

    /// <summary><c>+0x120</c>: the main chord's angle. Not reset by <see cref="Randomize"/>.</summary>
    private double _angle;

    /// <summary>The spline at <c>+0x80</c>.</summary>
    private readonly WaveformStroke _stroke = new() { WalkColour = true };

    private readonly PaletteCycler _paletteA;
    private readonly PaletteCycler _paletteB;

    /// <summary>
    /// The constructor (<c>0x18000f960</c>) builds four <c>CTColor</c>s, each drawing two random colours: the
    /// spline's line renderer's, the spline's own, then palettes A and B. Only A and B are ever read
    /// before being set up again, but all four consume draws.
    /// </summary>
    public WonderWaveRender(Random random)
    {
        IsDraw = true;
        _ = new PaletteCycler(random);
        _ = new PaletteCycler(random);
        _paletteA = new PaletteCycler(random);
        _paletteB = new PaletteCycler(random);
    }

    public override string Name => "Wonder Wave";

    /// <summary><c>0x180010860</c>, verified by <c>verify-renders</c>, then <c>OnSetParams</c>.</summary>
    public override void Randomize(Random random)
    {
        _renderMode = random.Next(3);
        _sinLoops = 10 + random.Next(512);                  // 10..521
        // ScalePct alone consumes THREE draws — it is a min-of-3 from 0..2, biased hard toward zero.
        _scalePct = MpvisMath.MinOfK(random, 0, 2, 3);
        _subdivisions = 1 + random.Next(4);                 // 1..4
        _spin = 0.10 + random.NextDouble() * 0.325;         // 0.10..0.425
        _spinDelta = random.NextDouble() * 0.26 - 0.13;     // -0.13..0.13
        _spinMode = random.Next(4);
        _mirrored = random.Next(2) == 0;
        _crossLine = random.Next(3) == 0;
        _crossMode = random.Next(3) == 0;
        _bassFlex = random.Next(3);
        _phase = random.NextDouble() * MpvisMath.Pi - MpvisMath.Pi / 2;

        // OnSetParams (0x1800106a0, vtable +0x58). Its clamps never bite on randomized values; the one
        // thing it changes is the spline's mirror flag.
        _stroke.Mirror = _crossLine;
    }

    /// <summary>
    /// The chord length: <c>(float)sqrt((float)(W² + H²))</c>, the field diagonal, times the bass when
    /// "Mirrored" is set.
    /// </summary>
    private float Length(EffectContext ctx)
    {
        var sq = ctx.FocusX * ctx.FocusX + ctx.FocusY * ctx.FocusY;
        var length = (float)Math.Sqrt((float)sq);
        if (_mirrored) length *= (float)ctx.Audio.BassNow;
        return length;
    }

    /// <summary>The cross chord's angle for this frame; BassFlex 0 also drifts the offset.</summary>
    private float CrossAngle(EffectContext ctx)
    {
        switch (_bassFlex)
        {
            case 0:
                _phase -= _spinDelta * ctx.Audio.BassNow * 0.5;
                return (float)(_phase + _angle);
            case 1:
                return (float)(_phase + _angle);
            case 2:
                return (float)_angle;
            default:
                return 0f;
        }
    }

    /// <summary>The angle update at the end of the render body.</summary>
    private void AdvanceAngle(EffectContext ctx)
    {
        var bass = ctx.Audio.BassNow;
        switch (_spinMode)
        {
            case 0: _angle += bass * bass * _spinDelta; break;
            case 1: _angle += _spinDelta * bass; break;
            case 2: _angle += _spinDelta; break;
        }
    }

    /// <summary>
    /// <c>RenderWaveformRad</c> (<c>0x18000bdf4</c>): endpoints <c>length/2</c> out from the centre, the
    /// stroke running FROM the <c>angle</c> end TO the <c>angle + π</c> end. Each coordinate is
    /// <c>round((float)trig · (float)(length·0.5))</c>.
    /// </summary>
    private static (int x0, int y0, int x1, int y1) Chord(EffectContext ctx, float angle, float length)
    {
        var half = length * 0.5f;
        double far = angle + (float)MpvisMath.Pi;
        var cx = ctx.FocusX >> 1;
        var cy = ctx.FocusY >> 1;
        return (cx + PixelBuffer.MpvisRound((float)Math.Cos(angle) * half),
                cy + PixelBuffer.MpvisRound((float)Math.Sin(angle) * half),
                cx + PixelBuffer.MpvisRound((float)Math.Cos(far) * half),
                cy + PixelBuffer.MpvisRound((float)Math.Sin(far) * half));
    }

    public override void Draw(PixelBuffer buffer, EffectContext ctx)
    {
        _paletteA.Advance(ctx.Random);
        _paletteB.Advance(ctx.Random);

        var draw = ctx.Draw;
        draw.BeginStroke(_paletteB.Current, _paletteA.Current, 1, _subdivisions * 2);
        _stroke.PlotMode = _renderMode;
        draw.ClipMargin = _renderMode switch { 1 => 1, 2 => 9, _ => 0 };
        draw.NeighbourAlpha = 0.1f;
        _stroke.MaxSteps = _sinLoops;
        _stroke.Envelope = _scalePct;
        _stroke.Amplitude = (int)_spin * ctx.Height; // truncates to 0 — see the class note
        _stroke.Lobes = _subdivisions;

        var length = Length(ctx);
        DrawChord(buffer, ctx, (float)_angle + HalfPiF, length);
        if (_crossMode) DrawChord(buffer, ctx, CrossAngle(ctx), length);

        AdvanceAngle(ctx);
    }

    private void DrawChord(PixelBuffer buffer, EffectContext ctx, float angle, float length)
    {
        var (x0, y0, x1, y1) = Chord(ctx, angle, length);
        ctx.Draw.Spline(buffer, x0, y0, x1, y1, _stroke, ctx.Audio.Waveform0, ctx.Audio.Waveform1);
    }
}
