namespace PlattaPlayer.Visualizations.MilkDrop.Audio;

/// <summary>
/// One frame of analysed audio handed to the preset equations. The band values hover around 1.0
/// (the running average), spiking to ~2–4 on strong beats, mirroring MilkDrop's <c>bass</c>/<c>mid</c>/
/// <c>treb</c> and their attenuated (time-smoothed) counterparts. The arrays expose the raw waveform
/// and spectrum for waveform/spectrum drawing.
/// </summary>
public sealed class AudioFrame
{
    public float Bass;
    public float Mid;
    public float Treb;
    public float BassAtt;
    public float MidAtt;
    public float TrebAtt;

    /// <summary>Time-domain samples (mono, roughly -1..1).</summary>
    public float[] Waveform { get; }

    /// <summary>Magnitude spectrum bins (length <see cref="Waveform"/>.Length / 2).</summary>
    public float[] Spectrum { get; }

    public AudioFrame(int waveformLength)
    {
        Waveform = new float[waveformLength];
        Spectrum = new float[waveformLength / 2];
    }
}
