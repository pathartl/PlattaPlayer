using System.Runtime.CompilerServices;

namespace PlattaPlayer.Codecs.Gbs.Emulation;

// SameBoy's GB_apu_t as a value type, so "memset to zero" (APU power-off) is an assignment of default and a copy
// of the machine copies it whole. Field types follow SameBoy's, as their wrap-around matters.

[InlineArray(4)]
internal struct Bytes4
{
    private byte _element;
}

[InlineArray(4)]
internal struct Bools4
{
    private bool _element;
}

[InlineArray(6)]
internal struct Registers
{
    private ushort _element;
}

[InlineArray(2)]
internal struct SquareChannels
{
    private SquareChannel _element;
}

internal struct EnvelopeClock
{
    public bool Locked;      // FYNO's output on channel 4
    public bool Clock;       // FOSY on channel 4
    public bool ShouldLock;  // FYNO's input on channel 4
}

internal struct SquareChannel
{
    public ushort PulseLength;      // reloaded from NRx1 (xorred), in 256 Hz DIV ticks
    public byte CurrentVolume;      // reloaded from NRx2
    public byte VolumeCountdown;    // reloaded from NRx2
    public byte CurrentSampleIndex;
    public bool SampleSurpressed;
    public ushort SampleCountdown;  // in APU ticks (reloaded from SampleLength, xorred $7FF)
    public ushort SampleLength;     // from NRx3, NRx4, in APU ticks
    public bool LengthEnabled;      // NRx4
    public EnvelopeClock EnvelopeClock;
    public byte Delay;              // SameBoy's hack for the CGB-D/E phantom step
    public bool DidTick;
    public bool JustReloaded;
}

internal struct WaveChannel
{
    public bool Enable;             // NR30
    public ushort PulseLength;      // reloaded from NR31 (xorred), in 256 Hz DIV ticks
    public byte Shift;              // NR32
    public ushort SampleLength;     // NR33, NR34, in APU ticks
    public bool LengthEnabled;      // NR34
    public ushort SampleCountdown;  // in APU ticks (reloaded from SampleLength, xorred $7FF)
    public byte CurrentSampleIndex;
    public byte CurrentSampleByte;
    public bool WaveFormJustRead;
    public bool Pulsed;
    public byte BuggedReadCountdown;
}

internal struct NoiseChannel
{
    public ushort PulseLength;      // reloaded from NR41 (xorred), in 256 Hz DIV ticks
    public byte CurrentVolume;      // reloaded from NR42
    public byte VolumeCountdown;    // reloaded from NR42
    public ushort Lfsr;
    public bool Narrow;
    public byte CounterCountdown;   // counts down to 0 to tick the counter (512 kHz scaled to 2 MHz)
    public ushort Counter;          // a bit of this 14-bit register clocks the LFSR
    public bool LengthEnabled;      // NR44
    public byte Alignment;          // tracks the 512 kHz/1 MHz alignment of samples
    public bool CurrentLfsrSample;
    public bool DidStepCounter;
    public bool CountdownReloaded;
    public EnvelopeClock EnvelopeClock;
}

internal struct ApuState
{
    public bool GlobalEnable;
    public ushort ApuCycles;
    public Bytes4 Samples;
    public Bools4 IsActive;
    public byte DivDivider;   // DIV ticks the APU at 512 Hz; divided again for the 256, 128 and 64 Hz clocks
    public byte LfDiv;        // the APU runs at 2 MHz, channels 1, 2 and 4 at 1 MHz
    public byte SquareSweepCountdown;
    public byte SquareSweepCalculateCountdown;
    public byte SquareSweepCalculateCountdownReloadTimer;
    public ushort SweepLengthAddend;
    public ushort ShadowSweepSampleLength;
    public bool UnshiftedSweep;
    public bool SquareSweepInstantCalculationDone;
    public byte Channel1RestartHold;
    public ushort Channel1CompletedAddend;
    public SquareChannels Square;
    public WaveChannel Wave;
    public NoiseChannel Noise;
    public byte SkipDivEvent;
    public byte PcmMask0, PcmMask1;
    public bool ApuCyclesIn2Mhz;
    public bool PendingEnvelopeTick;
    public bool NoiseCounterActive;
    public bool NoiseBackgroundCounterActive;
    public bool LfsrSteppedInNarrow;
    public bool LfsrBit7BeforeStep;
    public bool NoiseStartedWithDacDisabled;
}
