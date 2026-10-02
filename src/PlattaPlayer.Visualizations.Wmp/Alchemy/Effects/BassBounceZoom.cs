using System;

namespace PlattaPlayer.Visualizations.Wmp.Alchemy.Effects;

/// <summary>
/// <c>CTRenderBassBounce</c> (<c>0x180011200</c>): a beat-driven zoom whose output NOTHING READS. On each bass hit the
/// zoom snaps to <see cref="_hover"/>, then eases toward a sinusoidal target over a <see cref="_period"/>-frame
/// cycle, and each frame the result is copied as a centred rect to <c>renderData+0x60</c>. No code in
/// this build reads that rect back (see <see cref="EffectScheduler.BassBounce"/>), so the effect is
/// invisible. It is kept because step 7 still selects and randomizes it, and its draws are part of the
/// random stream. Its state is still advanced so the object stays faithful if a consumer ever turns up.
/// </summary>
public sealed class BassBounceZoom : AlchemyEffect
{
    private double _hover = 0.2;
    private double _bounceFrame = 0.1;
    private int _period = 40;

    private double _zoom = 1.0;
    private int _frame;

    public override string Name => "Bass Bounce";

    public override void Randomize(Random random)
    {
        _hover = 0.13 + random.NextDouble() * 0.12;       // 0.13..0.25
        _bounceFrame = 0.05 + random.NextDouble() * 0.15; // 0.05..0.20
        _period = 25 + random.Next(31);                   // 25..55
    }

    public override void Tick(EffectContext ctx)
    {
        // FUN_180011200 gates the reset on renderData+0x1c, which is BASS HIT (+0x1d is the plain beat),
        // and skips it entirely while the effect is fading out so a late beat cannot re-snap the zoom.
        if (ctx.Audio.BassHit && Phase != 2)
        {
            _frame = 0;
            _zoom = _hover;
        }

        var i = _period > 0 ? _frame % _period : 0;
        var osc = Math.Sin((double)i / Math.Max(1, _period) * 2 * MpvisMath.Pi);
        var target = osc * _bounceFrame + (1 - _bounceFrame);

        // Steady state is a 1-pole ease, (target + 15*zoom)/16. During the fade-out the original
        // reweights BOTH the target and the filter so the zoom slides back to 1.0 (no displacement) by
        // the time the slot expires: f falls 1 -> 0, b = 15f, a = 16 - b, target = f*target + (1 - f).
        var a = 1.0;
        var b = 15.0;
        if (Phase == 2)
        {
            var f = 1.0 - (double)PhaseCounter / Math.Max(1, PhaseDuration);
            if (f < 0) f = 0;
            b = f * 15.0;
            target = f * target + (1.0 - f);
            a = 16.0 - b;
        }

        _zoom = (target * a + b * _zoom) / 16.0;
        if (_zoom > 1.0) _zoom = 1.0;
        _frame++;
    }

    /// <summary>The eased zoom, 1.0 = no change. Written to the unread rect as <c>W·zoom</c> by <c>H·zoom</c>.</summary>
    public double Zoom => _zoom;
}
