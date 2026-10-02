using System;
using PlattaPlayer.Visualizations.Wmp.Framebuffer;

namespace PlattaPlayer.Visualizations.Wmp.Alchemy.Effects;

/// <summary>
/// <c>CTStrongRenderAtomBalls::NormalRender</c> (<c>0x180010380</c>): two point-symmetric soft discs, and
/// for five frames after every BEAT a waveform spline strung between them. Checked frame by frame
/// against the real function by <c>verify-normalrender</c>.
///
/// Per frame, in order:
/// <list type="number">
/// <item><c>CTJBall::Move</c> (<c>0x18000b5b4</c>): the position moves by the OLD velocity. The position
/// is never clamped. A coordinate outside <c>0..W</c> (or <c>0..H</c>) negates that velocity component
/// and flags a bounce; otherwise the component is damped by a friction of <c>0.8f</c>.</item>
/// <item>Unless it bounced, each velocity component gets a random kick of <c>±bass</c> (two draws).</item>
/// <item>The radius is a spring toward Ball1Radius:
/// <c>vel = ((target − cur)·1)/1 + vel; vel = ((0.8f·bass)/1 + vel)·damp; cur += vel</c>, and each disc's
/// radius is <c>(int)(cur·bass) + 1</c>.</item>
/// <item>On a BEAT (<c>renderData+0x1d</c>, not the bass hit): radius doubled, invert toggled, alpha
/// 0.4, and a five-frame burst starts. During a burst the first ball's radius scales by
/// <c>burst/2.5</c>, and a spline joins the two balls, in plot mode 1, margin 1, 500 points max,
/// amplitude 50, walking its colour from the fill toward the rim over <c>rand()%6+1</c> steps.</item>
/// <item>Both discs, then both palettes advance.</item>
/// </list>
/// The previous version clamped the position, had no friction, jitter or spline, used invented spring
/// constants, scaled everything by the window size, and keyed the burst on the bass hit.
/// </summary>
public sealed class AtomBallsRender : AlchemyEffect
{
    /// <summary><c>0x3fe99999a0000000</c> (<c>0.8f</c>): the ball's friction (<c>+0x70</c>) and the
    /// spring's bass push (<c>+0x160</c>).</summary>
    private const double PointEight = 0.800000011920929;

    /// <summary><c>+0x148</c> and <c>+0x158</c>, both set to 1.0 by the randomizer.</summary>
    private const double SpringGain = 1.0;

    private const double SpringDenominator = 1.0;

    /// <summary>
    /// The damping range, <c>0x3FD999999A000000</c> — single-precision 0.4 promoted to a double, not
    /// 0.4. See <see cref="MpvisMath.Pi"/> for why these matter.
    /// </summary>
    private const double DampRange = 0.3999999761581421;

    private double _posX;
    private double _posY;
    private double _velX;
    private double _velY;
    private bool _bounced;

    private double _ball1Radius = 16;
    private double _ball2Radius = 16;
    private double _damp = 0.5;

    private double _curRadius;
    private double _radVel;
    private bool _invert;
    private int _burst;

    /// <summary>The spline at <c>+0x178</c>.</summary>
    private readonly WaveformStroke _stroke = new()
    {
        MaxSteps = 500, Amplitude = 50, Envelope = 0, Lobes = 2, WalkColour = true, PlotMode = 1,
    };

    private readonly PaletteCycler _paletteA;
    private readonly PaletteCycler _paletteB;

    /// <summary>
    /// The constructor builds five <c>CTColor</c>s, each drawing two random colours: a spare line
    /// renderer's, palettes A and B, then the spline's line renderer's and the spline's own. Only A and B
    /// are ever read before being set up again, but all five consume draws.
    /// </summary>
    public AtomBallsRender(Random random)
    {
        IsDraw = true;
        _ = new PaletteCycler(random);
        _paletteA = new PaletteCycler(random);
        _paletteB = new PaletteCycler(random);
        _ = new PaletteCycler(random);
        _ = new PaletteCycler(random);
    }

    public override string Name => "Two Balls";

    /// <summary>
    /// <c>0x180010a30</c>, which draws TWENTY-ONE values (twenty-two when the second radius comes up
    /// independent). The spring velocity and the burst counter are NOT reset.
    ///
    /// The two palette transitions at the end are the bulk of it: each draws a duration and then two
    /// colours, and the SECOND colour is the transition's start point while the first is its target.
    /// </summary>
    public override void Randomize(Random random)
    {
        _posX = FieldWidth > 0 ? random.Next(FieldWidth) : 0;
        _posY = FieldHeight > 0 ? random.Next(FieldHeight) : 0;
        _velX = random.NextDouble() * 4 - 2;                 // -2..2
        _velY = random.NextDouble() * 4 - 2;
        _ball1Radius = 9 + random.NextDouble() * 21;         // 9..30
        _damp = 0.5 + random.NextDouble() * DampRange;       // 0.5..0.9

        // The balls are the same size fourteen times in fifteen. Ball2Radius is stored but nothing reads
        // it: both discs size from the one spring.
        _ball2Radius = random.Next(15) == 0 ? 9 + random.NextDouble() * 21 : _ball1Radius;
        _curRadius = _ball1Radius;

        BeginPalette(_paletteA, random);
        BeginPalette(_paletteB, random);
    }

    private static void BeginPalette(PaletteCycler palette, Random random)
    {
        var period = 5 + random.Next(50);
        var to = PaletteCycler.RandomColor(random);
        var from = PaletteCycler.RandomColor(random);
        palette.Begin(from, to, period);
    }

    /// <summary>The frame's geometry, after the state update.</summary>
    private readonly record struct Frame(int X, int Y, int Radius1, int Radius2, float Alpha, bool Burst);

    /// <summary>Everything in the render body up to the drawing.</summary>
    private Frame Step(EffectContext ctx)
    {
        var w = FieldWidth;
        var h = FieldHeight;

        // CTJBall::Move.
        var vx = _velX;
        var vy = _velY;
        _posX += vx;
        _posY += vy;
        _bounced = false;
        if (_posX < 0.0 || w < _posX) { _bounced = true; _velX = -vx; }
        else _velX = vx * PointEight;
        if (_posY < 0.0 || h < _posY) { _bounced = true; _velY = -vy; }
        else _velY = vy * PointEight;

        var bass = ctx.Audio.BassNow;
        if (!_bounced)
        {
            _velX = ((ctx.Random.Next(2) * 2) - 1) * bass + _velX;
            _velY = ((ctx.Random.Next(2) * 2) - 1) * bass + _velY;
        }

        var x = PixelBuffer.MpvisRound((float)_posX);
        var y = PixelBuffer.MpvisRound((float)_posY);
        var alpha = (float)bass;

        _radVel = ((_ball1Radius - _curRadius) * SpringGain) / SpringDenominator + _radVel;
        _radVel = ((PointEight * bass) / SpringDenominator + _radVel) * _damp;
        _curRadius = _radVel + _curRadius;

        var r2 = (int)(_curRadius * bass) + 1;
        int r1;
        if (!ctx.Audio.Beat)
        {
            r1 = _burst != 0 ? PixelBuffer.MpvisRound((float)(_burst / 2.5 * r2)) : r2;
        }
        else
        {
            _burst = 5;
            r2 *= 2;
            _invert = !_invert;
            alpha = 0.4f;
            r1 = r2;
        }

        return new Frame(x, y, r1, r2, alpha, _burst != 0);
    }

    public override void Draw(PixelBuffer buffer, EffectContext ctx)
    {
        var f = Step(ctx);
        var w = FieldWidth;
        var h = FieldHeight;

        if (f.Burst)
        {
            var steps = ctx.Random.Next(6) + 1;
            var draw = ctx.Draw;
            draw.BeginStroke(_paletteA.Current, _paletteB.Current, 1, steps);
            draw.ClipMargin = 1;
            draw.Spline(buffer, f.X, f.Y, w - f.X, h - f.Y, _stroke, ctx.Audio.Waveform0, ctx.Audio.Waveform1);
            _burst--;
        }

        ctx.Draw.DrawDisc(buffer, f.X, f.Y, f.Radius1, f.Alpha, _paletteA.Current, _paletteB.Current, _invert);
        ctx.Draw.DrawDisc(buffer, w - f.X, h - f.Y, f.Radius2, f.Alpha, _paletteA.Current, _paletteB.Current, _invert);

        _paletteA.Advance(ctx.Random);
        _paletteB.Advance(ctx.Random);
    }
}
