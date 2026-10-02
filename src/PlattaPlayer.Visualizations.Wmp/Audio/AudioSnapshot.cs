using System;
using PlattaPlayer.Visualizations.Wmp.Alchemy;

namespace PlattaPlayer.Visualizations.Wmp.Audio;

/// <summary>
/// The condensed audio state the "Ambience" engine drives itself from (analysis §B.3). Each fresh
/// frame it reduces the lowest few spectrum bins to a single bass level, keeps a 30-frame running
/// average, and raises <see cref="Beat"/>/<see cref="BassHit"/> flags that effects use to re-randomize
/// themselves. It also tracks a beat-driven focal point and a treble-advanced rotation angle
/// (analysis mentions a second helper deriving treble from the high bins).
/// </summary>
public sealed class AudioSnapshot
{
    private const int HistoryLength = 30;

    private readonly double[] _history = new double[HistoryLength];
    private int _ri;
    private int _framesSinceBeat;
    private int _framesSinceBig;

    /// <summary>Bass energy this frame, roughly 0..1.</summary>
    public double BassNow { get; private set; }

    /// <summary>Bass relative to the 30-frame running average.</summary>
    public double BassDelta { get; private set; }

    /// <summary>True on the frame a beat is detected (bass ≥ 1.10× local average, min 9-frame gap).</summary>
    public bool Beat { get; private set; }

    /// <summary>True on a strong "hit" (bass &gt; 0.9 and ≥ 1.20× average, min 20-frame gap).</summary>
    public bool BassHit { get; private set; }

    // There is deliberately no focal point here. The old analysis doc described the effects' render-data
    // fields at +0x3c/+0x40 as a beat-driven focus orbiting the centre; they are in fact the field
    // width and height (see EffectContext.FocusX). Keeping an audio-driven "focus" around invites that
    // invention straight back in.

    /// <summary>Treble energy this frame (high bins), used to advance <see cref="Rotation"/>.</summary>
    public double Treble { get; private set; }

    /// <summary>Accumulating rotation angle advanced by the treble each frame.</summary>
    public double Rotation { get; private set; }

    /// <summary>
    /// The RAW PCM waveform, both channels, exactly as handed to the effect. The chord renderers are
    /// oscilloscopes — their splines displace by these samples (see <see cref="Alchemy.SplineOffsets"/>) —
    /// so the bytes have to reach them undigested. Everything else here is derived; these are not.
    /// </summary>
    public byte[] Waveform0 { get; private set; } = [];

    public byte[] Waveform1 { get; private set; } = [];

    public void Update(TimedLevels levels, Random random)
    {
        Beat = false;
        BassHit = false;

        if (levels.State == 1)
            return; // paused: hold the previous snapshot, like the original's cached frame

        var f0 = levels.Frequency[0];
        var f1 = levels.Frequency[1];
        Waveform0 = levels.Waveform[0];
        Waveform1 = levels.Waveform[1];

        // Bass = ch0 bins {1,3,5} + ch1 bins {2,4,6}. In the original flat buffer these were addressed
        // as freq0[1,3,5] and freq0[0x402,0x404,0x406], the latter spilling into channel 1's block.
        var bass = (f0[1] + f0[3] + f0[5] + f1[2] + f1[4] + f1[6]) / 1200.0;
        BassNow = bass;

        _history[_ri] = bass;
        _ri = (_ri + 1) % HistoryLength;

        var sum = 0.0;
        for (var i = 0; i < HistoryLength; i++) sum += _history[i];
        var avg = sum / HistoryLength;
        BassDelta = bass - avg;

        _framesSinceBeat++;
        _framesSinceBig++;

        if (_framesSinceBeat > 9 && avg * MpvisMath.BeatMultiplier < bass)
        {
            Beat = true;
            _framesSinceBeat = 0;

            if (bass > 0.9 && avg * MpvisMath.BassHitMultiplier < bass && _framesSinceBig > 20)
            {
                BassHit = true;
                _framesSinceBig = 0;
            }
        }

        // Treble is SIX SPECIFIC BINS, not a slice. FUN_18000fe50 reads the flat TimedLevel at
        // 0x1f5/0x1f7/0x1f9 and 0x5f6/0x5f8/0x5fa — channel 0 bins 501/503/505 and channel 1 bins
        // 502/504/506, mirroring the odd/even split the bass sum uses. This summed thirty consecutive
        // bins instead, which is a different quantity by roughly a factor of five and made the rotation
        // it drives spin far too fast.
        Treble = (f0[501] + f0[503] + f0[505] + f1[502] + f1[504] + f1[506]) / 600.0;
        Rotation += Treble;
    }
}
