namespace PlattaPlayer.Codecs.Spc.Emulation;

/// <summary>
/// The S-DSP (Sony CXD1222Q-1): eight BRR voices with Gaussian interpolation, ADSR/GAIN envelopes, noise,
/// pitch modulation and the FIR echo, mixed to 16-bit stereo at 32 kHz. Ported from ares (ISC licence, see
/// THIRD-PARTY-NOTICES.md), which models the chip clock by clock: each sample is 32 clocks, and every
/// register and memory access happens on the clock the hardware does it. That is what makes timing-sensitive
/// tricks (KON/KOFF writes mid-sample, ENDX/ENVX/OUTX reads, echo buffer overlapping samples or code) sound as
/// they do on a console.
/// <para>The ares source uses exact-width integer types (n4, n11, i17…) that wrap on assignment; this port
/// masks at the same places.</para>
/// </summary>
internal sealed partial class Dsp
{
    // Envelope modes.
    private const int Release = 0, Attack = 1, Decay = 2, Sustain = 3;

    /// <summary>The 64 KiB of audio RAM, shared with the SMP.</summary>
    public byte[] Ram { get; private set; }

    /// <summary>The 128 DSP registers as read back by the SMP.</summary>
    public byte[] Registers { get; private set; } = new byte[128];

    private Voice[] _voice = new Voice[8];
    private readonly short[] _gaussian = BuildGaussianTable();

    // Clock: which of the 32 clocks of a sample comes next, the envelope/noise rate counter, and the
    // every-other-sample toggle that paces KON/KOFF.
    private int _phase;
    private int _counter;           // n15
    private bool _sample = true;

    // Main volume / FLG.
    private bool _reset = true;
    private bool _mute = true;
    private int[] _mainVolume = new int[2];  // i8
    private int[] _mainOutput = new int[2];  // i17

    // Echo.
    private int _echoFeedback;                         // i8
    private int[] _echoVolume = new int[2];   // i8
    private int[] _fir = new int[8];          // i8
    private int[,] _history = new int[2, 8];  // i16
    private int _echoPage;                             // n8
    private int _echoDelay;                            // n4
    private bool _echoReadonly = true;
    private int[] _echoInput = new int[2];    // i17
    private int[] _echoOutput = new int[2];   // i17
    private int _echoPageLatch;                        // n8
    private bool _echoReadonlyLatch;
    private int _echoAddress;                          // n16
    private int _echoOffset;                           // n16
    private int _echoLength;                           // n16
    private int _historyOffset;                        // n3

    // Noise.
    private int _noiseFrequency;                       // n5
    private int _noiseLfsr = 0x4000;                   // n15

    // BRR fetch latches.
    private int _brrBank;                              // n8
    private int _brrBankLatch;                         // n8
    private int _brrSource;                            // n8
    private int _brrAddress;                           // n16
    private int _brrNextAddress;                       // n16
    private int _brrHeader;                            // n8
    private int _brrByte;                              // n8

    // Shared per-clock latches.
    private int _latchAdsr0;                           // n8
    private int _latchEnvx;                            // n8
    private int _latchOutx;                            // n8
    private int _latchPitch;                           // n15
    private int _latchOutput;                          // i16
    private int _endxBuffer;                           // n8: the ENDX value Voice5 builds for Voice7 to store

    // Where finished samples go: interleaved left/right, appended at OutputCount.
    private short[] _output = new short[2 * 1024];

    public Dsp(byte[] ram)
    {
        Ram = ram;
        for (var n = 0; n < 8; n++) _voice[n] = new Voice { Index = n << 4, Bit = 1 << n };
    }

    /// <summary>A copy of the whole DSP state, working on <paramref name="ram"/> (a copy of its RAM).</summary>
    public Dsp CloneWith(byte[] ram)
    {
        var copy = (Dsp)MemberwiseClone();
        copy.Ram = ram;
        copy.Registers = (byte[])Registers.Clone();
        copy._voice = new Voice[_voice.Length];
        for (var n = 0; n < _voice.Length; n++) copy._voice[n] = _voice[n].Clone();
        copy._mainVolume = (int[])_mainVolume.Clone();
        copy._mainOutput = (int[])_mainOutput.Clone();
        copy._echoVolume = (int[])_echoVolume.Clone();
        copy._fir = (int[])_fir.Clone();
        copy._history = (int[,])_history.Clone();
        copy._echoInput = (int[])_echoInput.Clone();
        copy._echoOutput = (int[])_echoOutput.Clone();
        copy._output = (short[])_output.Clone();
        return copy;
    }

    /// <summary>Interleaved samples produced since <see cref="OutputCount"/> was last reset.</summary>
    public short[] Output => _output;

    /// <summary>Number of stereo frames in <see cref="Output"/>.</summary>
    public int OutputCount { get; set; }

    /// <summary>Makes room for at least <paramref name="frames"/> more frames.</summary>
    public void EnsureOutputCapacity(int frames)
    {
        var need = (OutputCount + frames) * 2;
        if (need <= _output.Length) return;
        Array.Resize(ref _output, Math.Max(need, _output.Length * 2));
    }

    /// <summary>Test only: receives every voice's internal state at each output sample (7 values per voice:
    /// BRR address, BRR offset, interpolation position, envelope, envelope mode, KON delay, buffer offset).</summary>
    internal static Action<int[]>? VoiceHook;

    private void EmitSample(int left, int right)
    {
        if (VoiceHook is { } hook)
        {
            var state = new int[56];
            for (var n = 0; n < 8; n++)
            {
                var v = _voice[n];
                state[n * 7] = v.BrrAddress;
                state[n * 7 + 1] = v.BrrOffset;
                state[n * 7 + 2] = v.GaussianOffset;
                state[n * 7 + 3] = v.Envelope;
                state[n * 7 + 4] = v.EnvelopeMode;
                state[n * 7 + 5] = v.KeyOnDelay;
                state[n * 7 + 6] = v.BufferOffset;
            }
            hook(state);
        }

        var i = OutputCount * 2;
        if (i + 2 > _output.Length) Array.Resize(ref _output, _output.Length * 2);
        _output[i] = (short)left;
        _output[i + 1] = (short)right;
        OutputCount++;
    }

    // ----- Register access ---------------------------------------------------------------------------

    public byte Read(int address) => Registers[address & 0x7f];

    public void Write(int address, byte data)
    {
        address &= 0x7f;
        Registers[address] = data;

        switch (address)
        {
            case 0x0c: _mainVolume[0] = (sbyte)data; break;   // MVOLL
            case 0x1c: _mainVolume[1] = (sbyte)data; break;   // MVOLR
            case 0x2c: _echoVolume[0] = (sbyte)data; break;   // EVOLL
            case 0x3c: _echoVolume[1] = (sbyte)data; break;   // EVOLR
            case 0x4c:                                        // KON
                for (var n = 0; n < 8; n++)
                {
                    _voice[n].KeyOn = Bit(data, n);
                    _voice[n].KeyLatch = Bit(data, n);
                }
                break;
            case 0x5c:                                        // KOFF
                for (var n = 0; n < 8; n++) _voice[n].KeyOff = Bit(data, n);
                break;
            case 0x6c:                                        // FLG
                _noiseFrequency = data & 0x1f;
                _echoReadonly = Bit(data, 5);
                _mute = Bit(data, 6);
                _reset = Bit(data, 7);
                break;
            case 0x7c:                                        // ENDX
                _endxBuffer = 0;
                Registers[0x7c] = 0;  // always cleared, regardless of data written
                break;
            case 0x0d: _echoFeedback = (sbyte)data; break;    // EFB
            case 0x2d:                                        // PMON
                for (var n = 0; n < 8; n++) _voice[n].Modulate = Bit(data, n);
                _voice[0].Modulate = false;  // voice 0 does not support modulation
                break;
            case 0x3d:                                        // NON
                for (var n = 0; n < 8; n++) _voice[n].Noise = Bit(data, n);
                break;
            case 0x4d:                                        // EON
                for (var n = 0; n < 8; n++) _voice[n].Echo = Bit(data, n);
                break;
            case 0x5d: _brrBank = data; break;                // DIR
            case 0x6d: _echoPage = data; break;               // ESA
            case 0x7d: _echoDelay = data & 0x0f; break;       // EDL
        }

        var v = _voice[(address >> 4) & 7];
        switch (address & 0x0f)
        {
            case 0x00: v.Volume0 = (sbyte)data; break;                            // VxVOLL
            case 0x01: v.Volume1 = (sbyte)data; break;                            // VxVOLR
            case 0x02: v.Pitch = (v.Pitch & 0x3f00) | data; break;                // VxPITCHL
            case 0x03: v.Pitch = (v.Pitch & 0x00ff) | ((data & 0x3f) << 8); break; // VxPITCHH
            case 0x04: v.Source = data; break;                                    // VxSRCN
            case 0x05: v.Adsr0 = data; break;                                     // VxADSR0
            case 0x06: v.Adsr1 = data; break;                                     // VxADSR1
            case 0x07: v.Gain = data; break;                                      // VxGAIN
            case 0x08: _latchEnvx = data; break;                                  // VxENVX
            case 0x09: _latchOutx = data; break;                                  // VxOUTX
            case 0x0f: _fir[(address >> 4) & 7] = (sbyte)data; break;             // FIRx
        }
    }

    /// <summary>
    /// Restores the registers from a snapshot (an SPC file's DSP block). Every register is written as the SMP
    /// would write it, so the voice and echo state follows; ENDX is restored as-is rather than cleared, and the
    /// last KON value is latched, so the voices it names key on as the song starts (as other SPC players do).
    /// </summary>
    public void LoadRegisters(ReadOnlySpan<byte> registers)
    {
        for (var address = 0; address < 128; address++)
            if (address != 0x7c) Write(address, registers[address]);

        Registers[0x7c] = registers[0x7c];
        _endxBuffer = registers[0x7c];

        // A snapshot is taken mid-song, when the copies the DSP latches once per sample already hold the
        // register values. Left at power-on zero, the first sample would use echo page 0 (writing the echo
        // into $0000-$0003) and sample directory 0.
        _brrBankLatch = _brrBank;
        _echoPageLatch = _echoPage;
        _echoReadonlyLatch = _echoReadonly;
        foreach (var v in _voice)
        {
            v.ModulateLatch = v.Modulate;
            v.NoiseLatch = v.Noise;
            v.EchoLatch = v.Echo;
        }
    }

    private static bool Bit(int value, int bit) => ((value >> bit) & 1) != 0;

    // ----- Clock ---------------------------------------------------------------------------------------

    /// <summary>Runs one of the 32 clocks of a sample. Clock 27 outputs the sample.</summary>
    public void Clock()
    {
        var v = _voice;
        switch (_phase)
        {
            case 0: Voice5(v[0]); Voice2(v[1]); break;
            case 1: Voice6(v[0]); Voice3(v[1]); break;
            case 2: Voice7(v[0]); Voice4(v[1]); Voice1(v[3]); break;
            case 3: Voice8(v[0]); Voice5(v[1]); Voice2(v[2]); break;
            case 4: Voice9(v[0]); Voice6(v[1]); Voice3(v[2]); break;
            case 5: Voice7(v[1]); Voice4(v[2]); Voice1(v[4]); break;
            case 6: Voice8(v[1]); Voice5(v[2]); Voice2(v[3]); break;
            case 7: Voice9(v[1]); Voice6(v[2]); Voice3(v[3]); break;
            case 8: Voice7(v[2]); Voice4(v[3]); Voice1(v[5]); break;
            case 9: Voice8(v[2]); Voice5(v[3]); Voice2(v[4]); break;
            case 10: Voice9(v[2]); Voice6(v[3]); Voice3(v[4]); break;
            case 11: Voice7(v[3]); Voice4(v[4]); Voice1(v[6]); break;
            case 12: Voice8(v[3]); Voice5(v[4]); Voice2(v[5]); break;
            case 13: Voice9(v[3]); Voice6(v[4]); Voice3(v[5]); break;
            case 14: Voice7(v[4]); Voice4(v[5]); Voice1(v[7]); break;
            case 15: Voice8(v[4]); Voice5(v[5]); Voice2(v[6]); break;
            case 16: Voice9(v[4]); Voice6(v[5]); Voice3(v[6]); break;
            case 17: Voice1(v[0]); Voice7(v[5]); Voice4(v[6]); break;
            case 18: Voice8(v[5]); Voice5(v[6]); Voice2(v[7]); break;
            case 19: Voice9(v[5]); Voice6(v[6]); Voice3(v[7]); break;
            case 20: Voice1(v[1]); Voice7(v[6]); Voice4(v[7]); break;
            case 21: Voice8(v[6]); Voice5(v[7]); Voice2(v[0]); break;
            case 22: Voice3a(v[0]); Voice9(v[6]); Voice6(v[7]); Echo22(); break;
            case 23: Voice7(v[7]); Echo23(); break;
            case 24: Voice8(v[7]); Echo24(); break;
            case 25: Voice3b(v[0]); Voice9(v[7]); Echo25(); break;
            case 26: Echo26(); break;
            case 27: Misc27(); Echo27(); break;
            case 28: Misc28(); Echo28(); break;
            case 29: Misc29(); Echo29(); break;
            case 30: Misc30(); Voice3c(v[0]); Echo30(); break;
            case 31: Voice4(v[0]); Voice1(v[2]); break;
        }
        _phase = (_phase + 1) & 31;
    }

    // ----- Misc ----------------------------------------------------------------------------------------

    private void Misc27()
    {
        foreach (var v in _voice) v.ModulateLatch = v.Modulate;
    }

    private void Misc28()
    {
        foreach (var v in _voice)
        {
            v.NoiseLatch = v.Noise;
            v.EchoLatch = v.Echo;
        }
        _brrBankLatch = _brrBank;
    }

    private void Misc29()
    {
        _sample = !_sample;
        if (_sample)  // clears KON 63 clocks after it was last read
            foreach (var v in _voice) v.KeyLatch &= !v.KeyOnLatch;
    }

    private void Misc30()
    {
        if (_sample)
        {
            foreach (var v in _voice)
            {
                v.KeyOnLatch = v.KeyLatch;
                v.KeyOffLatch = v.KeyOff;
            }
        }

        CounterTick();

        if (CounterPoll(_noiseFrequency))
        {
            var feedback = (_noiseLfsr << 13) ^ (_noiseLfsr << 14);
            _noiseLfsr = ((feedback & 0x4000) | (_noiseLfsr >> 1)) & 0x7fff;
        }
    }

    // ----- Counter -------------------------------------------------------------------------------------

    // Samples per counter event; rate 0 never triggers. Every rate divides 30720 (0x7800).
    private static readonly int[] CounterRate =
    [
        0, 2048, 1536, 1280, 1024, 768, 640, 512, 384, 320, 256, 192, 160, 128, 96, 80,
        64, 48, 40, 32, 24, 20, 16, 12, 10, 8, 6, 5, 4, 3, 2, 1,
    ];

    // The counters are not all aligned at zero.
    private static readonly int[] CounterOffset =
    [
        0, 0, 1040, 536, 0, 1040, 536, 0, 1040, 536, 0, 1040, 536, 0, 1040, 536,
        0, 1040, 536, 0, 1040, 536, 0, 1040, 536, 0, 1040, 536, 0, 1040, 0, 0,
    ];

    private void CounterTick()
    {
        if (_counter == 0) _counter = 2048 * 5 * 3;
        _counter--;
    }

    private bool CounterPoll(int rate) =>
        rate != 0 && (_counter + CounterOffset[rate]) % CounterRate[rate] == 0;

    // ----- Gaussian interpolation ----------------------------------------------------------------------

    /// <summary>
    /// The 512-entry interpolation kernel from the chip's ROM, generated as ares does: a windowed sinc,
    /// normalised so each set of four taps sums to 2048 — this reproduces the hardware table exactly.
    /// </summary>
    private static short[] BuildGaussianTable()
    {
        var table = new double[512];
        for (var n = 0; n < 512; n++)
        {
            var k = 0.5 + n;
            var s = Math.Sin(Math.PI * k * 1.280 / 1024);
            var t = (Math.Cos(Math.PI * k * 2.000 / 1023) - 1) * 0.50;
            var u = (Math.Cos(Math.PI * k * 4.000 / 1023) - 1) * 0.08;
            var r = s * (t + u + 1.0) / k;
            table[511 - n] = r;
        }

        var result = new short[512];
        for (var phase = 0; phase < 128; phase++)
        {
            var sum = table[phase] + table[phase + 256] + table[511 - phase] + table[255 - phase];
            var scale = 2048.0 / sum;
            result[phase] = (short)(table[phase] * scale + 0.5);
            result[phase + 256] = (short)(table[phase + 256] * scale + 0.5);
            result[511 - phase] = (short)(table[511 - phase] * scale + 0.5);
            result[255 - phase] = (short)(table[255 - phase] * scale + 0.5);
        }
        return result;
    }

    /// <summary>Exposed for verification against the published hardware table.</summary>
    internal static short[] GaussianTableForTest() => BuildGaussianTable();

    private int GaussianInterpolate(Voice v)
    {
        // Pointers into the table from the fractional position between samples.
        var fraction = (v.GaussianOffset >> 4) & 0xff;
        var forward = 255 - fraction;
        var reverse = fraction;  // mirror of the left half of the table

        var offset = (v.BufferOffset + (v.GaussianOffset >> 12)) % 12;
        var buffer = v.Buffer;
        var g = _gaussian;
        int output;
        output = (g[forward] * buffer[offset]) >> 11; if (++offset >= 12) offset = 0;
        output += (g[forward + 256] * buffer[offset]) >> 11; if (++offset >= 12) offset = 0;
        output += (g[reverse + 256] * buffer[offset]) >> 11; if (++offset >= 12) offset = 0;
        output = (short)output;
        output += (g[reverse] * buffer[offset]) >> 11;
        return Clamp16(output) & ~1;
    }

    private static int Clamp16(int value) => value > short.MaxValue ? short.MaxValue : value < short.MinValue ? short.MinValue : value;

    // i17 wrap-around.
    private static int Wrap17(int value) => (value << 15) >> 15;

    private sealed class Voice
    {
        public int Index;               // register base: 0x00 for voice 0, 0x10 for voice 1…
        public int Bit;                 // the voice's bit in the KON/ENDX/… masks

        public int Volume0, Volume1;    // i8
        public int Pitch;               // n14
        public int Source;              // n8
        public int Adsr0, Adsr1, Gain;  // n8
        public int Envx;                // n8
        public bool KeyOn, KeyOff, Modulate, Noise, Echo;

        public short[] Buffer = new short[12];  // 12 decoded samples (a ring)
        public int BufferOffset;        // n4: where the next samples will be decoded
        public int GaussianOffset;      // n16: fractional position in the sample (0x1000 = 1.0)
        public int BrrAddress;          // n16: current BRR block
        public int BrrOffset = 1;       // n4: decoding offset in the block (1-8)
        public int KeyOnDelay;          // n3: KON delay / setup phase
        public int EnvelopeMode;        // n2
        public int Envelope;            // n11: 0-2047

        // Internal latches.
        public int EnvelopeLatch;       // i32: used by GAIN mode 7, a very obscure quirk
        public bool KeyLatch, KeyOnLatch, KeyOffLatch, ModulateLatch, NoiseLatch, EchoLatch, Looped;

        public Voice Clone()
        {
            var copy = (Voice)MemberwiseClone();
            copy.Buffer = (short[])Buffer.Clone();
            return copy;
        }
    }
}
