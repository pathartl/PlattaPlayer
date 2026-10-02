namespace PlattaPlayer.Visualizations.Wmp.Audio;

/// <summary>
/// One frame of the audio block Windows Media Player hands a visualization — the raw <c>TimedLevel</c>
/// payload, in its native byte form.
///
/// The engines consume THIS, not floats. Keeping the byte layer as the engine's input is what lets the
/// reverse-engineering harness drive our renderers and the real WMP effects from an identical input and
/// diff the resulting frames; a float-based entry point would put an unverifiable conversion in the
/// middle of every comparison. The float-to-byte adapter lives separately, in
/// <see cref="TapToTimedLevel"/>, and is used only by the plugin surfaces.
/// </summary>
public sealed class TimedLevelFrame
{
    public const int Bins = 1024;

    /// <summary>Stopped. The host zeroes both blocks; renderers paint the background and do not advance.</summary>
    public const int StateStopped = 0;

    /// <summary>Paused. "Reuse the previous data" — renderers re-present without advancing.</summary>
    public const int StatePaused = 1;

    /// <summary>Playing, fresh data.</summary>
    public const int StatePlaying = 2;

    public byte[] Frequency0 { get; } = new byte[Bins];
    public byte[] Frequency1 { get; } = new byte[Bins];
    public byte[] Waveform0 { get; } = new byte[Bins];
    public byte[] Waveform1 { get; } = new byte[Bins];

    /// <summary>One of <see cref="StateStopped"/> / <see cref="StatePaused"/> / <see cref="StatePlaying"/>.</summary>
    public int State { get; set; }

    /// <summary>
    /// Sample timestamp in 100 ns units. Load-bearing: the real renderers skip their entire redraw when
    /// this has not advanced since the previous frame, so a host that ticks faster than the audio simply
    /// re-presents. Feeding a constant here would freeze the visualization.
    /// </summary>
    public long TimeStamp { get; set; }

    /// <summary>Number of channels the host reported via <c>IWMPEffects::MediaInfo</c>.</summary>
    public int ChannelCount { get; set; }

    public void Clear()
    {
        Array.Clear(Frequency0);
        Array.Clear(Frequency1);
        Array.Clear(Waveform0);
        Array.Clear(Waveform1);
        State = StateStopped;
        TimeStamp = 0;
    }
}
