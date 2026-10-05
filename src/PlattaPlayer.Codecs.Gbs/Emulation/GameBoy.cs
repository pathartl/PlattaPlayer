namespace PlattaPlayer.Codecs.Gbs.Emulation;

/// <summary>
/// A Game Boy Color (CGB-E) playing a GBS: the parts of SameBoy v1.0.3 that music depends on, ported to C#
/// (MIT licence, see THIRD-PARTY-NOTICES.md) and verified sample-for-sample against SameBoy itself. Every bus
/// access is modelled at T-cycle granularity as SameBoy does: the timers, APU, PPU and DMA advance before each
/// memory access, so register writes reach the APU on the exact cycle they would in SameBoy.
/// <para>
/// The machine is a CGB-E because that is what SameBoy's own GBS player runs on by default (its "automatic"
/// model is the CGB-E). It starts without a boot ROM, in CGB mode, which a GBS can't leave (only a boot ROM can
/// switch to DMG mode). The PPU is emulated for its timing only (the VBlank and STAT interrupts, LY, mode-3
/// length, VRAM/OAM blocking); no pixels are produced.
/// </para>
/// <para>
/// Ports SameBoy as is, with the model fixed: branches for other models are gone, and so are the parts a GBS
/// can't reach (the boot ROM, cartridge RAM and RTC, the camera, the SGB, the debugger, the screen).
/// </para>
/// </summary>
internal sealed partial class GameBoy
{
    /// <summary>The CPU clock: 4 MiHz (double speed doubles the CPU, not the clock rate SameBoy counts in).</summary>
    public const int ClockRate = 0x400000;

    // Register file, indexed as SameBoy's GB_REGISTER_* (AF, BC, DE, HL, SP, PC). The high byte of each pair is
    // the first-named register (A, B, D, H).
    private const int AF = 0, BC = 1, DE = 2, HL = 3, SP = 4, PC = 5;

    // I/O register indices ($FF00 + index).
    private const int IoJoyp = 0x00, IoSb = 0x01, IoSc = 0x02, IoDiv = 0x04, IoTima = 0x05, IoTma = 0x06, IoTac = 0x07,
        IoIf = 0x0F, IoNr10 = 0x10, IoNr11 = 0x11, IoNr12 = 0x12, IoNr13 = 0x13, IoNr14 = 0x14, IoNr21 = 0x16,
        IoNr22 = 0x17, IoNr23 = 0x18, IoNr24 = 0x19, IoNr30 = 0x1A, IoNr31 = 0x1B, IoNr32 = 0x1C, IoNr33 = 0x1D,
        IoNr34 = 0x1E, IoNr41 = 0x20, IoNr42 = 0x21, IoNr43 = 0x22, IoNr44 = 0x23, IoNr50 = 0x24, IoNr51 = 0x25,
        IoNr52 = 0x26, IoWavStart = 0x30, IoWavEnd = 0x3F, IoLcdc = 0x40, IoStat = 0x41, IoScy = 0x42, IoScx = 0x43,
        IoLy = 0x44, IoLyc = 0x45, IoDma = 0x46, IoBgp = 0x47, IoObp0 = 0x48, IoObp1 = 0x49, IoWy = 0x4A, IoWx = 0x4B,
        IoKey0 = 0x4C, IoKey1 = 0x4D, IoVbk = 0x4F, IoBank = 0x50, IoHdma1 = 0x51, IoHdma2 = 0x52, IoHdma3 = 0x53,
        IoHdma4 = 0x54, IoHdma5 = 0x55, IoRp = 0x56, IoBgpi = 0x68, IoBgpd = 0x69, IoObpi = 0x6A, IoObpd = 0x6B,
        IoOpri = 0x6C, IoSvbk = 0x70, IoPsm = 0x71, IoPswx = 0x72, IoPswy = 0x73, IoPsw = 0x74, IoPgb = 0x75,
        IoPcm12 = 0x76, IoPcm34 = 0x77;

    private const byte LcdcBgEn = 1, LcdcObjEn = 2, LcdcObjSize = 4, LcdcBgMap = 8, LcdcTileSel = 0x10,
        LcdcWinEnable = 0x20, LcdcWinMap = 0x40, LcdcEnable = 0x80;

    // ----- Memory -----
    private byte[] _rom;
    private int _romSize;
    private byte[] _ram = new byte[0x1000 * 8];   // 8 banks of WRAM
    private byte[] _vram = new byte[0x2000 * 2];  // 2 banks of VRAM
    private byte[] _hram = new byte[0x7F];
    private byte[] _io = new byte[0x80];
    private byte[] _oam = new byte[0xA0];
    private byte[] _bgPalettes = new byte[0x40];
    private byte[] _objPalettes = new byte[0x40];

    // ----- CPU and general hardware state (SameBoy's core_state section) -----
    private Registers _r;
    private bool _ime;
    private byte _ie;
    private byte _cgbRamBank;
    private bool _doubleSpeed;
    private bool _halted;
    private bool _stopped;
    private bool _bootRomFinished;
    private bool _imeToggle;
    private bool _haltBug;
    private int _irSensor;
    private bool _effectiveIrInput;
    private ushort _addressBus;
    private byte _dataBus;
    private uint _dataBusDecayCountdown;

    // T-cycles the CPU has spent but not yet run the rest of the machine for (see Cpu).
    private uint _pendingCycles;

    // Time the open data bus takes to decay to $FF, in 8 MHz units (SameBoy's default).
    private const uint DataBusDecay = 12;

    // ----- MBC3 (SameBoy maps every GBS onto an MBC3 cartridge without RAM) -----
    private ushort _mbcRomBank;
    private byte _mbcRamBank;
    private bool _mbcRamEnable;
    private byte _mbc3RomBank;
    private byte _mbc3RamBank;
    private bool _mbc3RtcMapped;

    private readonly GbsImage _gbs;

    public GameBoy(GbsImage gbs, int sampleRate)
    {
        _gbs = gbs;
        _rom = gbs.Rom;
        _romSize = gbs.Rom.Length;
        SetSampleRate(sampleRate);
    }

    /// <summary>A copy of the complete state; running it produces exactly what this instance would.</summary>
    public GameBoy Clone()
    {
        var copy = (GameBoy)MemberwiseClone();
        copy._ram = (byte[])_ram.Clone();
        copy._vram = (byte[])_vram.Clone();
        copy._hram = (byte[])_hram.Clone();
        copy._io = (byte[])_io.Clone();
        copy._oam = (byte[])_oam.Clone();
        copy._bgPalettes = (byte[])_bgPalettes.Clone();
        copy._objPalettes = (byte[])_objPalettes.Clone();
        copy._visibleObjs = (byte[])_visibleObjs.Clone();
        copy._objectsX = (byte[])_objectsX.Clone();
        copy._objectsY = (byte[])_objectsY.Clone();
        copy.CloneOutput(this);
        return copy;  // the ROM is never written, so it is shared
    }

    // ----- Starting a song (SameBoy's GB_gbs_switch_track) ----------------------------------------------------

    /// <summary>Resets the machine and starts song <paramref name="track"/> (0-based) as SameBoy's GBS player does:
    /// LCD and sound on, the timer set from the header, then the init routine called with the song in A.</summary>
    public void StartSong(int track)
    {
        Reset();
        WriteMemory(0xFF00 + IoLcdc, LcdcEnable);
        WriteMemory(0xFF00 + IoTac, _gbs.Tac);
        WriteMemory(0xFF00 + IoTma, _gbs.Tma);
        WriteMemory(0xFF00 + IoNr52, 0x80);
        WriteMemory(0xFF00 + IoNr51, 0xFF);
        WriteMemory(0xFF00 + IoNr50, 0x77);
        Array.Clear(_ram);
        Array.Clear(_hram);
        Array.Clear(_oam);
        WriteMemory(0xFFFF, (byte)(_gbs.Tac != 0 || _gbs.Tma != 0 ? 0x04 : 0x01));
        if ((_gbs.Tac & 0x80) != 0) _doubleSpeed = true;  // "Might mean double speed mode on a DMG"

        if (_gbs.LoadAddress != 0)
        {
            _r[SP] = _gbs.StackPointer;
            _r[PC] = GbsImage.EntryAddress;
        }
        else
        {
            // Without a load address there's no room below it for the driver: it goes just under the stack.
            _r[PC] = _r[SP] = (ushort)(_gbs.StackPointer - GbsImage.EntrySize);
            var entry = _gbs.Entry();
            for (var i = 0; i < entry.Length; i++) WriteMemory((ushort)(_r[PC] + i), entry[i]);
        }

        _bootRomFinished = true;
        _r[AF] = (ushort)((_r[AF] & 0xFF) | (byte)track << 8);
        _ie = (byte)((_gbs.Tac & 0x4) != 0 ? 4 : 1);
    }

    // SameBoy's GB_reset for a CGB-E with no boot ROM and its random power-on RAM contents disabled (zero): the
    // whole saved state is cleared, then the few non-zero power-on values set. The audio output path (sample
    // rate, filter and band-limited buffers) is not part of the saved state and is kept.
    private void Reset()
    {
        Array.Clear(_ram);
        Array.Clear(_vram);
        Array.Clear(_hram);
        Array.Clear(_io);
        Array.Clear(_oam);
        Array.Clear(_bgPalettes);
        Array.Clear(_objPalettes);
        _r = default;
        _irSensor = 0;
        _effectiveIrInput = false;
        _addressBus = 0;
        _dataBus = 0;
        _dataBusDecayCountdown = 0;
        _ie = 0;

        ResetDma();
        _mbcRamBank = 0;
        _mbcRamEnable = false;
        _mbc3RomBank = _mbc3RamBank = 0;
        _mbc3RtcMapped = false;
        _mbcRomBank = 1;
        ResetTiming();
        _apu = default;
        _apu.ApuCyclesIn2Mhz = true;
        ResetVideo();

        _cgbRamBank = 1;
        _io[IoJoyp] = 0xCF;
        _serialMask = 0x80;
        _io[IoSc] = 0x7E;
        _dmaCurrentDest = 0xA1;
        SetInternalDivCounter(8);
    }

    // ----- Running ----------------------------------------------------------------------------------------------

    // Read position in _output: frames rendered past the end of the last request are handed out first.
    private int _outputRead;

    /// <summary>Fills <paramref name="interleaved"/> (left, right, …) with the next frames.</summary>
    public void Render(Span<short> interleaved)
    {
        var done = 0;
        while (done < interleaved.Length)
        {
            var available = _outputCount - _outputRead;
            if (available == 0)
            {
                Produce();
                continue;
            }
            var take = Math.Min(available, interleaved.Length - done);
            _output.AsSpan(_outputRead, take).CopyTo(interleaved[done..]);
            _outputRead += take;
            done += take;
        }
    }

    /// <summary>Emulates <paramref name="frames"/> frames without keeping the audio.</summary>
    public void Skip(long frames)
    {
        var samples = frames * 2;
        while (samples > 0)
        {
            var available = _outputCount - _outputRead;
            if (available == 0)
            {
                Produce();
                continue;
            }
            var take = (int)Math.Min(available, samples);
            _outputRead += take;
            samples -= take;
        }
    }

    // Runs the CPU until at least one frame has been rendered.
    private void Produce()
    {
        _outputCount = _outputRead = 0;
        while (_outputCount == 0) CpuRun();
    }

    /// <summary>Test hook: called before each instruction executes (with PC already past its opcode).</summary>
    internal static Action<GameBoy, ushort>? TraceHook;

    /// <summary>Test hook: called for every write to I/O or the MBC (address, value).</summary>
    internal static Action<GameBoy, ushort, byte>? WriteHook;

    /// <summary>Test only: the state the harness's trace records (as the reference's trace does).</summary>
    internal ushort[] TraceRecord(ushort address) =>
    [
        address, _r[AF], _r[BC], _r[DE], _r[HL], _r[SP], _divCounter,
        (ushort)((_ime ? 1 : 0) | (_halted ? 2 : 0) | (_doubleSpeed ? 4 : 0)),
        _io[IoIf], _ie, _io[IoLy], (ushort)_displayState,
    ];

    internal ushort Pc => _r[PC];
    internal ushort DivCounter => _divCounter;
    internal ReadOnlySpan<byte> Io => _io;
    internal ReadOnlySpan<byte> Hram => _hram;
    internal ReadOnlySpan<byte> Wram => _ram;
}
