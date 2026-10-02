using System;
using PlattaPlayer.Visualizations.Wmp.Alchemy.Gpu;
using PlattaPlayer.Visualizations.Wmp.Framebuffer;

namespace PlattaPlayer.Visualizations.Wmp.Alchemy.Effects;

/// <summary>
/// <c>CToleranceShiftOScope</c> (mpvis.dll <c>FUN_18000e6e0</c>): a reflection stage that folds the field
/// and then hands the folded coordinate to ONE OF THREE EMBEDDED CHILD KERNELS.
///
/// The previous version of this class was an invention. It had a single 0..4 mode selecting hand-written
/// mirror / kaleidoscope / polar / concentric / tiling routines, and no children at all. The real object
/// has TWO independent selectors and three real kernels inside it:
/// <list type="bullet">
/// <item><see cref="_reflectionMode"/> (<c>+0x90</c>, <c>rand()%5</c>) picks the fold.</item>
/// <item><see cref="_shiftMode"/> (<c>+0x94</c>, <c>rand()%4</c>) picks what happens to coordinates the
/// fold passes through: a Stretch, a Linear or a Snafu kernel constructed inside this one at
/// <c>+0xA0</c> / <c>+0x148</c> / <c>+0x1D8</c>, or a point reflection.</item>
/// </list>
/// Only the SELECTED child is randomized, so this kernel consumes a variable number of draws — which is
/// how the mismatch was caught (<c>verify-warps</c> reported 3-11 draws against our fixed 9).
///
/// The folds are also not what they looked like. Most of them call the child only when the coordinate was
/// NOT folded — mirroring and delegating are alternatives, not a pipeline — and the polar fold applies no
/// rotation at all (Ghidra drops the floating-point arguments to its cos/sin, which made an earlier
/// reading invent one).
/// </summary>
public sealed class OScopeShift : AlchemyEffect, IGpuWarp
{
    private int _reflectionMode;
    private int _shiftMode;

    /// <summary>+0x70. Drawn only when <see cref="_shiftMode"/> is 3; unused by the fold itself.</summary>
    private double _spinFactor;

    private int _centerRadius = 50;  // +0x88, drawn only when ReflectionMode is 3
    private int _littleRadius = 10;  // +0x8c, likewise
    private int _boxSize = 1;        // +0x98, drawn only when ReflectionMode is 4

    // The three children FUN_18000df64 constructs inside this object, in its order.
    private readonly StretchShift _childStretch = new();
    private readonly LinearShift _childLinear = new();
    private readonly SnafuShift _childSnafu = new();

    // Derived by the resize hook FUN_18000f1a0 and by the base resize FUN_18000ae60.
    private int _focalX = 320;
    private int _focalY = 240;
    private int _boxLeft;
    private int _boxTop;
    private double _halfDiagonal = 400;

    public OScopeShift()
    {
        IsWarp = true;
        // FUN_18000df64 writes the word 1 at +0x51, which sets +0x51 and CLEARS +0x52 — so like
        // LinearShift this kernel pulls the fixed pixel (0,0) in when a source leaves the field.
        OutOfRangeToOrigin = true;
    }

    public override string Name => "OScope";

    /// <summary><c>FUN_18000ef90</c>.</summary>
    public override void Randomize(Random random)
    {
        _reflectionMode = random.Next(5);
        _shiftMode = random.Next(4);

        // Only the selected child re-rolls. Randomizing all three would consume the wrong number of
        // draws and desynchronise everything after this kernel.
        switch (_shiftMode)
        {
            case 0: _childStretch.Randomize(random); break;
            case 1: _childLinear.Randomize(random); break;
            case 2: _childSnafu.Randomize(random); break;
            default: _spinFactor = random.NextDouble() * 0.4 - 0.2; break;
        }

        if (_reflectionMode == 3)
        {
            _centerRadius = 5 + random.Next(250);   // 5..254
            _littleRadius = 3 + random.Next(50);    // 3..52
        }
        else if (_reflectionMode == 4)
        {
            // One time in ten the tile is a real size; otherwise it is 1, which makes the gate a
            // per-pixel checkerboard.
            _boxSize = random.Next(10) == 0 ? 1 + random.Next(300) : 1;
        }
    }

    /// <summary>
    /// The resize hooks: the base <c>FUN_18000ae60</c> derives the centre and the half-diagonal, and this
    /// kernel's own <c>FUN_18000f1a0</c> adds the concentric box and resizes the three children.
    /// </summary>
    public override void Tick(EffectContext ctx)
    {
        _focalX = ctx.Width >> 1;
        _focalY = ctx.Height >> 1;
        _halfDiagonal = Math.Sqrt((double)ctx.Width * ctx.Width + (double)ctx.Height * ctx.Height) * 0.5;
        _boxLeft = _focalX - _centerRadius;
        _boxTop = _focalY - _centerRadius;

        // Only Stretch derives anything from the field size; the other two are pure.
        _childStretch.Tick(ctx);
    }

    public override void Transform(EffectContext ctx, ref int x, ref int y)
    {
        var w = ctx.Width;
        var h = ctx.Height;

        switch (_reflectionMode)
        {
            case 0:
                if (MirrorFold(ref x, ref y, w, h)) Child(ctx, ref x, ref y);
                return;

            case 1:
                Kaleidoscope(ctx, ref x, ref y, w, h);
                return;

            case 2:
                PolarFold(ctx, ref x, ref y, w, h);
                return;

            case 3:
                Concentric(ctx, ref x, ref y);
                return;

            default:
                // Tile gate: a coordinate in an odd tile on either axis passes straight through.
                if (_boxSize != 0)
                {
                    if (((x / _boxSize) & 1) != 0) return;
                    if (((y / _boxSize) & 1) != 0) return;
                }
                Child(ctx, ref x, ref y);
                return;
        }
    }

    /// <summary>
    /// The mirror used by reflection modes 0 and 2. Returns true when the child should run — which is
    /// only when NEITHER axis was folded. A folded coordinate is the whole transform for that pixel.
    /// </summary>
    private bool MirrorFold(ref int x, ref int y, int w, int h)
    {
        var xInside = x <= _focalX;
        if (!xInside) x = w - x;
        if (y > _focalY)
        {
            y = h - y;
            return false;
        }
        return xInside;
    }

    /// <summary>
    /// The four-wedge diagonal fold. Which wedge a pixel is in is decided with INTEGER cross-products
    /// against the two diagonals (<c>W·y</c> vs <c>H·x</c> and <c>W·y</c> vs <c>(W−x)·H</c>), so the
    /// boundaries land exactly where the original's do at every size.
    /// </summary>
    private void Kaleidoscope(EffectContext ctx, ref int x, ref int y, int w, int h)
    {
        var wy = w * y;
        var anti = (w - x) * h;

        if (wy < h * x)
        {
            if (wy < anti)
            {
                // Top wedge: the only path that reaches a child.
                if (x < _focalX) { Child(ctx, ref x, ref y); return; }
                x = _focalX * 2 - x;
                return;
            }

            // Right wedge.
            x = (w - x) + _focalX;
            y = y >= _focalY ? y - _focalY : _focalY - y;
            return;
        }

        if (anti <= wy)
        {
            // Bottom wedge.
            y = h - y;
            if (x <= _focalX) return;
            x = _focalX * 2 - x;
            return;
        }

        // Left wedge.
        x = _focalX - x;
        y = y >= _focalY ? y - _focalY : _focalY - y;
    }

    /// <summary>
    /// Radial fold about the centre for anything beyond a quarter of the field diagonal: the radius
    /// reflects back through that boundary (<c>r' = 2·half − r</c>) at the SAME angle. There is no
    /// rotation term — the decompiler drops the arguments to the cos/sin here, and the disassembly shows
    /// the untouched <c>atan2</c> result going straight in.
    /// </summary>
    private void PolarFold(EffectContext ctx, ref int x, ref int y, int w, int h)
    {
        var dx = x - _focalX;
        var dy = y - _focalY;
        var theta = Math.Atan2(dy, dx);
        var r = Math.Sqrt((double)(dx * dx + dy * dy));
        var half = _halfDiagonal * 0.5;

        // The original's gate is an INTEGER division, so it only trips once the truncated radius reaches
        // the truncated boundary.
        var halfInt = (int)half;
        if (halfInt != 0 && (int)r / halfInt != 0)
        {
            var rr = r - 2 * (r - half);
            x = (int)(Math.Cos(theta) * rr) + _focalX;
            y = (int)(Math.Sin(theta) * rr) + _focalY;
            return;
        }

        if (MirrorFold(ref x, ref y, w, h)) Child(ctx, ref x, ref y);
    }

    /// <summary>
    /// Outside the centre circle the field is replaced by a grid of little circles: the coordinate is
    /// taken modulo <c>LittleRadius + 2</c> and rescaled so one tile covers the whole centre disc.
    /// </summary>
    private void Concentric(EffectContext ctx, ref int x, ref int y)
    {
        var dx = _focalX - x;
        var dy = _focalY - y;
        var d = Math.Sqrt((double)(dx * dx + dy * dy));

        if (_centerRadius >= d)
        {
            Child(ctx, ref x, ref y);
            return;
        }

        var p = _littleRadius + 2;
        var ix = 0;
        var iy = 0;
        if (p >= 1)
        {
            ix = x % p;
            iy = y % p;
        }

        var s = (_centerRadius + (double)_centerRadius) / _littleRadius;
        x = PixelBuffer.MpvisRound((float)(ix * s)) + _boxLeft;
        y = PixelBuffer.MpvisRound((float)(iy * s)) + _boxTop;
    }

    /// <summary><c>FUN_18000e128</c>: dispatch to the selected child kernel, or point-reflect.</summary>
    private void Child(EffectContext ctx, ref int x, ref int y)
    {
        switch (_shiftMode)
        {
            case 0: _childStretch.Transform(ctx, ref x, ref y); break;
            case 1: _childLinear.Transform(ctx, ref x, ref y); break;
            case 2: _childSnafu.Transform(ctx, ref x, ref y); break;
            case 3:
            {
                // Point reflection through the centre, taken the long way round: the offset is rebuilt
                // from its polar form and subtracted from the opposite corner — and the angle is ROTATED
                // by the spin factor on the way (`addsd xmm6, [rsi+0x70]`). That rotation is why this
                // shift mode draws a parameter at all; without it the whole thing collapses to a plain
                // point reflection, which for a centred field is very nearly the identity.
                var dx = _focalX - x;
                var dy = _focalY - y;
                var r = Math.Sqrt((double)(dx * dx + dy * dy));
                var theta = Math.Atan2(dy, dx) + _spinFactor;
                y = (ctx.Height - _focalY) - (int)(Math.Sin(theta) * r);
                x = (ctx.Width - _focalX) - (int)(Math.Cos(theta) * r);
                break;
            }
        }
    }

    public GpuWarpKind WarpKind => GpuWarpKind.OScope;

    public int WriteParams(Span<float> dst, EffectContext ctx)
    {
        dst[0] = _reflectionMode;
        dst[1] = _shiftMode;
        dst[2] = (float)_spinFactor;
        dst[3] = _centerRadius;
        dst[4] = _littleRadius;
        dst[5] = _boxSize;
        dst[6] = _focalX;
        dst[7] = _focalY;
        dst[8] = (float)_halfDiagonal;
        return 9;
    }

    /// <summary>Which embedded kernel <see cref="_shiftMode"/> currently delegates to.</summary>
    public GpuWarpKind ChildKind => _shiftMode switch
    {
        0 => GpuWarpKind.Stretch,
        1 => GpuWarpKind.Linear,
        2 => GpuWarpKind.Snafu,
        _ => GpuWarpKind.None,   // ShiftMode 3 is the point reflection, which needs no child params
    };

    public int WriteChildParams(Span<float> dst, EffectContext ctx) => _shiftMode switch
    {
        0 => ((IGpuWarp)_childStretch).WriteParams(dst, ctx),
        1 => ((IGpuWarp)_childLinear).WriteParams(dst, ctx),
        2 => ((IGpuWarp)_childSnafu).WriteParams(dst, ctx),
        _ => 0,
    };
}
