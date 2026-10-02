using System;
using PlattaPlayer.Visualizations.Wmp.Alchemy.Gpu;
using PlattaPlayer.Visualizations.Wmp.Framebuffer;

namespace PlattaPlayer.Visualizations.Wmp.Alchemy.Effects;

/// <summary>
/// <c>CTShiftLinear</c> (mpvis.dll <c>FUN_18000e280</c>): the linear scroll / axis-wise zoom / sine-shear
/// shift kernel. With falloff off it is a plain integer scroll by (XShift,YShift); with falloff on, a
/// <see cref="_fallDir"/> (0..7) bit-selects which axis and which edge the frame zooms from. An optional
/// sine shear then displaces one axis by a sine of the other.
/// </summary>
public sealed class LinearShift : AlchemyEffect, IGpuWarp
{
    private int _xShift = 1;
    private int _yShift;
    private bool _falloff;
    private double _fallPctX;
    private double _fallPctY;
    private int _fallDir;
    private int _sinShake;
    private int _sinLoops = 1;

    public LinearShift()
    {
        IsWarp = true;
        // FUN_18000de44 clears the kernel's +0x52 byte, so out-of-range sources sample the fixed pixel
        // (0,0) — i.e. black — instead of standing still. See AlchemyEffect.OutOfRangeToOrigin.
        OutOfRangeToOrigin = true;
    }

    public override string Name => "Linear";

    public override void Randomize(Random random)
    {
        do
        {
            _xShift = random.Next(7) - 3; // -3..3
            _yShift = random.Next(7) - 3;
        } while (_xShift == 0 && _yShift == 0);

        _falloff = random.Next(3) != 0;        // ~67%
        _fallPctX = random.NextDouble() * 0.1; // 0..0.1
        _fallPctY = random.NextDouble() * 0.1;
        _fallDir = random.Next(8);             // 0..7
        _sinShake = random.Next(3);            // 0,1,2
        _sinLoops = 1 + random.Next(15);       // 1..15
    }

    public override void Transform(EffectContext ctx, ref int x, ref int y)
    {
        var w = ctx.Width;
        var h = ctx.Height;
        int sx = x, sy = y;

        if (!_falloff)
        {
            sx = x + _xShift;
            sy = y + _yShift;
        }
        else
        {
            var zx = _fallPctX + 1.0;
            var zy = _fallPctY + 1.0;
            // dir → 0 Xleft;1 Yzoom;2 Xright;3 Yfar;4 Xleft+Yzoom;5 Xright+Yfar;6 Xleft+Yfar;7 Xright+Yzoom
            var xLeft = _fallDir is 0 or 4 or 6;
            var xRight = _fallDir is 2 or 5 or 7;
            var yZoom = _fallDir is 1 or 4 or 7;
            var yFar = _fallDir is 3 or 5 or 6;

            if (xLeft) sx = (int)(zx * x);
            else if (xRight) { var d = (w - x) - 1; sx = x - (int)(zx * d - d); }

            if (yZoom) sy = (int)(zy * y);
            else if (yFar) { var d = (h - y) - 1; sy = y - (int)(zy * d - d); }
        }

        // The sine shear is computed ENTIRELY IN SINGLE PRECISION: the ratio, the loop scaling and the pi
        // multiply are all `divss`/`mulss` against a float32 pi of its own (0x18002377c, NOT the promoted
        // double at 0x180023740); only the call to sin widens, and its result is narrowed straight back
        // before the amplitude multiply and the float rounder. This ran in double throughout with
        // Math.Round — which is banker's rounding — and the two agree almost everywhere, so nothing about
        // the output pointed at it. It surfaces exactly where the sine lands on a half-integer: at
        // SinLoops 14, y/h = 5/12 the true value is -0.5, the double route drifts a hair above it and
        // rounds to -1 where the float route holds -1.5 and rounds to -2. Found by verify-map, which
        // sweeps far more parameter sets than verify-warps did.
        if (_sinShake == 1)
            sx += PixelBuffer.MpvisRound(
                (float)Math.Sin((float)sy / h * _sinLoops * MpvisMath.PiSingle) * (_yShift * 3));
        else if (_sinShake >= 2)
            sy += PixelBuffer.MpvisRound(
                (float)Math.Sin((float)sx / w * _sinLoops * MpvisMath.PiSingle) * (_xShift * 3));

        x = sx;
        y = sy;
    }

    public GpuWarpKind WarpKind => GpuWarpKind.Linear;

    public int WriteParams(Span<float> dst, EffectContext ctx)
    {
        dst[0] = _falloff ? 1f : 0f;
        dst[1] = _xShift;
        dst[2] = _yShift;
        dst[3] = (float)(_fallPctX + 1.0);
        dst[4] = (float)(_fallPctY + 1.0);
        dst[5] = _fallDir;
        dst[6] = _sinShake;
        dst[7] = _sinLoops;
        return 8;
    }
}
