using PlattaPlayer.Visualizations.Wmp.Audio;

namespace PlattaPlayer.Visualizations.Wmp.Battery;

/// <summary>
/// The TimedLevel as Battery reads it: one flat byte block, laid out as freq0 (0x000), freq1 (0x400),
/// wave0 (0x800) and wave1 (0xC00), followed by the play state. Several effects index across the channel
/// boundaries, for example CJDar walking <c>TL[0x400 - off - 2j]</c> and CSpectrumEdge reading
/// <c>TL[0x401 + i]</c>. So the effects get the flat block and index it exactly as the original does,
/// rather than four separate arrays.
/// </summary>
public sealed class BatteryLevels
{
    public const int Freq0 = 0x000;
    public const int Freq1 = 0x400;
    public const int Wave0 = 0x800;
    public const int Wave1 = 0xC00;

    public byte[] Bytes { get; } = new byte[0x1000];

    /// <summary>TimedLevel+0x1000: 0 stopped, 1 paused, 2 playing.</summary>
    public int State { get; set; }

    public byte this[int offset] => Bytes[offset];

    public void LoadFrom(TimedLevelFrame frame)
    {
        frame.Frequency0.CopyTo(Bytes, Freq0);
        frame.Frequency1.CopyTo(Bytes, Freq1);
        frame.Waveform0.CopyTo(Bytes, Wave0);
        frame.Waveform1.CopyTo(Bytes, Wave1);
        State = frame.State;
    }

    /// <summary><c>freq0[i0] + freq0[i0+2] + freq0[i0+4] + freq1[i0+1] + freq1[i0+3] + freq1[i0+5]</c>: the
    /// six-bin bass sum that CGalaxy and CJiggyScribble scale by 1/1530 (= 6 * 255).</summary>
    public int Bass6(int i0 = 0) =>
        Bytes[Freq0 + i0] + Bytes[Freq0 + i0 + 2] + Bytes[Freq0 + i0 + 4] +
        Bytes[Freq1 + i0 + 1] + Bytes[Freq1 + i0 + 3] + Bytes[Freq1 + i0 + 5];
}
