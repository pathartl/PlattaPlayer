using System;
using PlattaPlayer.Visualizations.Wmp.Alchemy.Gpu;

namespace PlattaPlayer.Visualizations.Wmp.Alchemy.Effects;

/// <summary>
/// <c>CTShiftStretch</c> (mpvis.dll <c>FUN_18000e590</c>): a polar pull toward a focal point with a cubic
/// radial falloff (strong at the rim) plus a radius-dependent angular twist (<see cref="_rotation"/>), which
/// makes the feedback spiral. An optional radial sine ripple replaces the twist term. This is the kernel the
/// earlier port modelled as "Flow" but without the rotation-into-angle spiral term.
/// </summary>
public sealed class StretchShift : AlchemyEffect, IGpuWarp
{
    private double _rotation;
    private double _movePct = 0.1;
    private bool _flowPoint;
    private double _pctX = 0.5;
    private double _pctY = 0.5;
    private bool _sinShake;
    private int _sinLoops = 1;

    /// <summary>
    /// The pull centre, as INTEGERS. It matters that these are not doubles: the original adds the centre
    /// AFTER truncating the rotated offset (<c>sx = (int)(cos(ang) * r') + cx</c>), and truncation of a
    /// negative offset rounds toward zero, so folding the centre in first shifts a whole half-plane of
    /// pixels by one. That single difference was the last thing separating this kernel from the real one.
    /// </summary>
    private int _cx;

    private int _cy;

    private double _pull;
    private double _maxR = 1;

    public StretchShift() => IsWarp = true;

    public override string Name => "Stretch";

    /// <summary>
    /// FUN_18000ee00, which had never been read — the previous version of this method was a
    /// reconstruction and every part of it was wrong: the draw ORDER, four of the seven ranges, and the
    /// sense of two of the booleans. Feeding the real randomizer and this one the same rand() script
    /// (harness <c>verify-warps</c>) put the two side by side.
    ///
    /// Two details worth keeping visible. SinShake is drawn FIRST because the rotation range depends on
    /// it — a shaking stretch twists more than twice as hard. And the booleans are
    /// <c>~rand() &amp; 1</c>, i.e. true on an EVEN draw, which is the opposite of the natural reading.
    /// </summary>
    public override void Randomize(Random random)
    {
        _sinShake = random.Next(2) == 0;
        _sinLoops = 1 + random.Next(15);
        // The twist is small either way, but SinShake widens it from +/-0.10 to +/-0.22. The old
        // reconstruction guessed +/-0.015, roughly an order of magnitude too timid.
        _rotation = _sinShake
            ? random.NextDouble() * 0.44 - 0.22
            : random.NextDouble() * 0.2 - 0.1;
        _movePct = 0.05 + random.NextDouble() * 0.25;   // 0.05..0.30 pull strength
        _flowPoint = random.Next(2) == 0;
        _pctX = 0.05 + random.NextDouble() * 0.9;       // 0.05..0.95
        _pctY = 0.05 + random.NextDouble() * 0.9;
    }

    public override void Tick(EffectContext ctx)
    {
        var w = ctx.Width;
        var h = ctx.Height;
        // The clamp lives in the precompute (FUN_18000ea40), not the randomizer, and never bites for the
        // shipped 0.05..0.95 range — it is here so the field geometry matches if a value ever escapes.
        _pctX = Math.Clamp(_pctX, 0.01, 0.99);
        _pctY = Math.Clamp(_pctY, 0.01, 0.99);
        var focalX = (int)(w * _pctX);
        var focalY = (int)(h * _pctY);
        // Not FlowPoint: the object's centre fields at +0x10/+0x14, which the resize sets to W>>1, H>>1.
        _cx = _flowPoint ? focalX : w >> 1;
        _cy = _flowPoint ? focalY : h >> 1;
        _pull = (int)(h * _movePct);
        _maxR = Math.Max(1.0, Math.Max(
            Math.Max(Dist(0, 0, focalX, focalY), Dist(w, 0, focalX, focalY)),
            Math.Max(Dist(0, h, focalX, focalY), Dist(w, h, focalX, focalY))));
    }

    private static double Dist(double x, double y, double cx, double cy)
    {
        var dx = x - cx;
        var dy = y - cy;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public override void Transform(EffectContext ctx, ref int x, ref int y)
    {
        var dx = x - _cx;
        var dy = y - _cy;
        var theta = Math.Atan2(dy, dx);
        var r = Math.Sqrt(dx * dx + dy * dy);
        var t = r / _maxR;
        var rr = r - t * t * t * _pull;
        // Without SinShake the twist term is the NORMALISED radius t (0..1), not the absolute radius.
        // Read from the disassembly of FUN_18000e590: xmm6 holds r/maxR, the SinShake branch overwrites
        // it with sin(sinLoops*t*PI), and whichever survives is multiplied by the rotation and added to
        // theta. Ghidra's output hides this because it drops the floating-point arguments to the final
        // cos/sin calls.
        //
        // Using r' here instead made the twist up to ~400x too strong (r' reaches ~400 px where t
        // reaches 1), which smeared content into a spiral that filled the field — measured at 59-84%
        // ink coverage against a 3-17% reference.
        var term = _sinShake ? Math.Sin(_sinLoops * t * MpvisMath.Pi) : t;
        var ang = term * _rotation + theta;
        x = (int)(Math.Cos(ang) * rr) + _cx;
        y = (int)(Math.Sin(ang) * rr) + _cy;
    }

    public GpuWarpKind WarpKind => GpuWarpKind.Stretch;

    public int WriteParams(Span<float> dst, EffectContext ctx)
    {
        dst[0] = (float)_cx;
        dst[1] = (float)_cy;
        dst[2] = (float)_pull;
        dst[3] = (float)_maxR;
        dst[4] = (float)_rotation;
        dst[5] = _sinShake ? 1f : 0f;
        dst[6] = _sinLoops;
        return 7;
    }
}
