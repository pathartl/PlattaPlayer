using System;
using PlattaPlayer.Visualizations.Wmp.Alchemy.Gpu;

namespace PlattaPlayer.Visualizations.Wmp.Alchemy.Effects;

/// <summary>
/// <c>CTShiftSnafu</c> (mpvis.dll <c>FUN_18000e500</c>): the repeating-stripe "comb" shift. The frame is
/// divided into stripes of period 2·<see cref="_width"/>; alternate stripes are pushed by
/// <see cref="_speed"/> pixels in opposite directions along X (or along Y when <see cref="_vertical"/>),
/// with edge guards that nudge Y instead near a stripe boundary.
/// </summary>
public sealed class SnafuShift : AlchemyEffect, IGpuWarp
{
    private int _speed = 1;
    private int _width = 8;
    private bool _vertical;

    public SnafuShift() => IsWarp = true;

    public override string Name => "Snafu";

    /// <summary>
    /// FUN_18000ed70, in its exact draw ORDER — width first, then speed.
    ///
    /// This used to draw speed first. The values stayed in range and the output stayed plausible, so
    /// nothing about a rendered frame gave it away; it took feeding the real randomizer and this one the
    /// same rand() script (harness <c>verify-warps</c>) to see that every parameter was coming out of the
    /// wrong draw. Note this kernel was previously recorded as "verified faithful" on the strength of its
    /// FormShift matching the decompile — which it does. The randomizer had never been read.
    /// </summary>
    public override void Randomize(Random random)
    {
        _width = 1 + random.Next(40);      // 1..40
        _speed = 1 + random.Next(4);       // 1..4
        _vertical = random.Next(4) == 0;   // 25%
    }

    public override void Transform(EffectContext ctx, ref int x, ref int y)
    {
        var w = ctx.Width;
        var p = 2 * _width;
        var mx = p >= 1 ? Mod(x, p) : 0;
        var my = p >= 1 ? Mod(y, p) : 0;

        int sx = x, sy = y;
        if (_vertical)
        {
            sy = mx <= _width ? y - _speed : y + _speed;
        }
        else if (my > _width)
        {
            if (_width <= w - x || mx <= my - _width) sx = x + _speed;
            else sy = y + _speed;
        }
        else
        {
            if (_width <= x || mx <= my) sx = x - _speed;
            else sy = y + _speed;
        }

        x = sx;
        y = sy;
    }

    private static int Mod(int a, int n)
    {
        var r = a % n;
        return r < 0 ? r + n : r;
    }

    public GpuWarpKind WarpKind => GpuWarpKind.Snafu;

    public int WriteParams(Span<float> dst, EffectContext ctx)
    {
        dst[0] = _vertical ? 1f : 0f;
        dst[1] = _width;
        dst[2] = _speed;
        return 3;
    }
}
