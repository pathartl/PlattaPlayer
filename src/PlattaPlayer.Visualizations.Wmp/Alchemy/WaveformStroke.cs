namespace PlattaPlayer.Visualizations.Wmp.Alchemy;

/// <summary>
/// The per-stroke settings of a <c>CTWaveformRender</c>: its offset parameter block at <c>+0x50</c>, its
/// two flag bytes, and the plot mode of the <c>CTLineRender</c> it embeds. The defaults are what the
/// constructor (<c>0x18000af60</c>) leaves, and they matter, because the renderers overwrite only some of
/// them: the source stays at 2 (the MEAN of both channels) and the sample index stays MIRRORED.
/// </summary>
public sealed class WaveformStroke
{
    /// <summary><c>+0x70</c>: the most points a stroke may have.</summary>
    public int MaxSteps { get; set; } = 1;

    /// <summary><c>+0x54</c>: displacement in pixels for a full-scale sample.</summary>
    public int Amplitude { get; set; } = 50;

    /// <summary><c>+0x58</c>: fold the SAMPLE index back past the midpoint.</summary>
    public bool SampleMirror { get; set; } = true;

    /// <summary><c>+0x5c</c>: which waveform to sample (<see cref="SplineOffsets"/> sources).</summary>
    public int Source { get; set; } = SplineOffsets.SourceMean;

    /// <summary><c>+0x60</c>: the envelope along the stroke.</summary>
    public int Envelope { get; set; }

    /// <summary><c>+0x64</c>: lobes of the <c>|sin|</c> envelope.</summary>
    public int Lobes { get; set; } = 2;

    /// <summary><c>+0x9a</c>: also draw the stroke on the negated offsets.</summary>
    public bool Mirror { get; set; }

    /// <summary>
    /// <c>+0x98</c> CLEAR: step the colour transition once per point. The constructor sets the byte,
    /// and every renderer clears it each frame.
    /// </summary>
    public bool WalkColour { get; set; }

    /// <summary>The embedded line renderer's plot mode (<c>CTLineRender+0x34</c>).</summary>
    public int PlotMode { get; set; }
}
