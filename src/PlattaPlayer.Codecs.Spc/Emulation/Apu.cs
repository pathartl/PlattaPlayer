namespace PlattaPlayer.Codecs.Spc.Emulation;

/// <summary>
/// The SNES audio processing unit: the SMP and DSP sharing 64 KiB of RAM, restored from an SPC snapshot and
/// run as a whole. Produces the DSP's raw 16-bit stereo output at 32 kHz (no filtering or resampling), which
/// is what the console's DAC receives.
/// </summary>
internal sealed class Apu
{
    /// <summary>The DSP's output rate: the 24.576 MHz crystal / 768.</summary>
    public const int SampleRate = 32000;

    // Emulate this many frames at a time when filling a request.
    private const int ChunkFrames = 256;

    private readonly byte[] _ram;
    private readonly Dsp _dsp;
    private readonly Smp _smp;

    // Frames the DSP produced past the end of the last request, still to be handed out.
    private int _readPos;

    public Apu(SpcFile file)
    {
        _ram = new byte[0x10000];
        _dsp = new Dsp(_ram);
        _smp = new Smp(_ram, _dsp);

        file.Ram.CopyTo(_ram);
        // While the IPL ROM is mapped the dump's $FFC0-$FFFF holds the ROM as seen on the bus; the RAM
        // underneath is saved separately.
        if ((_ram[0xf1] & 0x80) != 0) file.ExtraRam.CopyTo(_ram.AsSpan(0xffc0));

        _dsp.LoadRegisters(file.DspRegisters);
        _smp.LoadState(file.Pc, file.A, file.X, file.Y, file.Psw, file.Sp);
    }

    private Apu(Apu other)
    {
        _ram = (byte[])other._ram.Clone();
        _dsp = other._dsp.CloneWith(_ram);
        _smp = other._smp.CloneWith(_ram, _dsp);
        _readPos = other._readPos;
    }

    /// <summary>A copy of the complete state (CPU, DSP, timers, RAM and not-yet-handed-out samples); running
    /// it produces exactly what this instance would.</summary>
    public Apu Clone() => new(this);

    /// <summary>Test only: the DSP.</summary>
    internal Dsp DspForTest => _dsp;

    /// <summary>Test only: the audio RAM.</summary>
    internal ReadOnlySpan<byte> Ram => _ram;

    /// <summary>Fills <paramref name="interleaved"/> (left, right, …) with the next frames.</summary>
    public void Render(Span<short> interleaved)
    {
        var frames = interleaved.Length / 2;
        var written = 0;
        while (written < frames)
        {
            var available = _dsp.OutputCount - _readPos;
            if (available == 0)
            {
                Run(Math.Min(frames - written, ChunkFrames));
                continue;
            }

            var take = Math.Min(available, frames - written);
            _dsp.Output.AsSpan(_readPos * 2, take * 2).CopyTo(interleaved.Slice(written * 2));
            _readPos += take;
            written += take;
        }
    }

    /// <summary>Emulates <paramref name="frames"/> frames without keeping the audio.</summary>
    public void Skip(long frames)
    {
        while (frames > 0)
        {
            var available = _dsp.OutputCount - _readPos;
            if (available == 0)
            {
                Run((int)Math.Min(frames, 4096));
                continue;
            }

            var take = (int)Math.Min(available, frames);
            _readPos += take;
            frames -= take;
        }
    }

    // Runs whole instructions until the DSP has produced at least `frames` new frames.
    private void Run(int frames)
    {
        _dsp.OutputCount = 0;
        _readPos = 0;
        _dsp.EnsureOutputCapacity(frames + 16);
        while (_dsp.OutputCount < frames) _smp.Step();
        _smp.RebaseClock();
    }
}
