using System.Runtime.CompilerServices;

namespace PlattaPlayer.Codecs.Spc.Emulation;

/// <summary>
/// The S-SMP (Sony CXP1100Q-1): an SPC700 core with its I/O registers, three timers and the 64-byte IPL ROM.
/// Ported from ares (ISC licence, see THIRD-PARTY-NOTICES.md). Every bus cycle is modelled: each read, write
/// and idle cycle advances the timers and runs the <see cref="Dsp"/> up to that moment before the access
/// happens, so the DSP sees register writes on the exact clock it would on hardware.
/// </summary>
internal sealed partial class Smp
{
    /// <summary>The IPL boot ROM mapped at $FFC0-$FFFF while CONTROL bit 7 is set.</summary>
    private static readonly byte[] IplRom =
    [
        0xcd, 0xef, 0xbd, 0xe8, 0x00, 0xc6, 0x1d, 0xd0, 0xfc, 0x8f, 0xaa, 0xf4, 0x8f, 0xbb, 0xf5, 0x78,
        0xcc, 0xf4, 0xd0, 0xfb, 0x2f, 0x19, 0xeb, 0xf4, 0xd0, 0xfc, 0x7e, 0xf4, 0xd0, 0x0b, 0xe4, 0xf5,
        0xcb, 0xf4, 0xd7, 0x00, 0xfc, 0xd0, 0xf3, 0xab, 0x01, 0x10, 0xef, 0x7e, 0xf4, 0x10, 0xeb, 0xba,
        0xf6, 0xda, 0x00, 0xba, 0xf4, 0xc4, 0xf4, 0xdd, 0x5d, 0xd0, 0xdb, 0x1f, 0x00, 0x00, 0xc0, 0xff,
    ];

    private byte[] _ram;
    private Dsp _dsp;

    // Elapsed time in SMP clocks (2.048 MHz, two per bus cycle at normal speed) and how far the DSP has run;
    // the DSP runs one clock per two SMP clocks.
    private long _clock;
    private long _dspClock;
    private long _rebased;  // clocks removed by RebaseClock (only for the test hook's timestamps)

    // ----- I/O ($00F0-$00FF) -----
    private bool _timersDisable;
    private bool _ramWritable = true;
    private bool _ramDisable;
    private bool _timersEnable = true;
    private int _externalWaitStates;
    private int _internalWaitStates;
    private bool _iplromEnable = true;
    private byte _dspAddress;

    // Ports from the S-CPU (what the SMP reads at $F4-$F7) and to it (what it writes there).
    private byte[] _portsIn = new byte[4];
    private byte[] _portsOut = new byte[4];
    private byte _aux4, _aux5;

    // Timers 0 and 1 share an 8 kHz prescaler and timer 2 has a 64 kHz one (stages 0 and 1 below); each
    // timer then counts the prescaler's falling edges up to its target.
    private Timer _timer0;
    private Timer _timer1;
    private Timer _timer2;
    private int _prescaler01, _prescaler2;  // stage 0
    private int _stage1Of01, _stage1Of2;     // stage 1 (a divide-by-two)
    private const int Prescale01 = 128, Prescale2 = 16;

    // True while both wait-state settings are the normal speed (the TEST register can slow the bus).
    private bool _normalSpeed = true;

    // How many SMP clocks the DSP runs ahead of the SMP's accesses (see Step).
    private readonly int _dspLead;

    public Smp(byte[] ram, Dsp dsp)
    {
        _ram = ram;
        _dsp = dsp;
        _dspLead = AlignLikeSnesSpc ? 0 : 1;
    }

    /// <summary>A copy of the whole SMP state, working on the given copies of the RAM and DSP.</summary>
    public Smp CloneWith(byte[] ram, Dsp dsp)
    {
        var copy = (Smp)MemberwiseClone();  // the timers are structs, so they are copied too
        copy._ram = ram;
        copy._dsp = dsp;
        copy._portsIn = (byte[])_portsIn.Clone();
        copy._portsOut = (byte[])_portsOut.Clone();
        return copy;
    }

    /// <summary>
    /// Restores the CPU and I/O state from an SPC snapshot. RAM has already been loaded; $F0-$FF in it hold
    /// the I/O registers as the dumper saw them. TEST ($F0) is not restored: dumps often hold junk there, and
    /// anything but its power-on value would stop the timers or the RAM.
    /// </summary>
    public void LoadState(int pc, byte a, byte x, byte y, byte psw, byte sp)
    {
        _pc = (ushort)pc;
        _a = a;
        _x = x;
        _y = y;
        _s = sp;
        SetPsw(psw);
        _wait = _stop = false;

        var control = _ram[0xf1];
        _iplromEnable = (control & 0x80) != 0;
        _timer0.Enable = (control & 1) != 0;
        _timer1.Enable = (control & 2) != 0;
        _timer2.Enable = (control & 4) != 0;

        _dspAddress = _ram[0xf2];
        for (var i = 0; i < 4; i++)
        {
            _portsIn[i] = _ram[0xf4 + i];
            _portsOut[i] = _ram[0xf4 + i];
        }
        _aux4 = _ram[0xf8];
        _aux5 = _ram[0xf9];

        _timer0.Target = _ram[0xfa];
        _timer1.Target = _ram[0xfb];
        _timer2.Target = _ram[0xfc];
        _timer0.Stage3 = _ram[0xfd] & 0x0f;
        _timer1.Stage3 = _ram[0xfe] & 0x0f;
        _timer2.Stage3 = _ram[0xff] & 0x0f;

        if (AlignLikeSnesSpc)
        {
            // snes_spc's timers first tick one cycle after loading: start each prescaler a cycle short of
            // a falling edge.
            _prescaler01 = Prescale01 - 2;
            _prescaler2 = Prescale2 - 2;
            _stage1Of01 = _stage1Of2 = 1;
            _timer0.Line = _timer1.Line = _timer2.Line = _timersEnable && !_timersDisable;
        }
    }

    // ----- Memory --------------------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte ReadRam(int address)
    {
        if (address >= 0xffc0 && _iplromEnable) return IplRom[address & 0x3f];
        if (_ramDisable) return 0x5a;
        return _ram[address];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteRam(int address, byte data)
    {
        // Writes to $FFC0-$FFFF always go to RAM, even while the IPL ROM is mapped.
        if (_ramWritable && !_ramDisable) _ram[address] = data;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Idle() => Wait(false, -1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte Read(int address)
    {
        address &= 0xffff;
        byte data;
        if ((address & 0xfffc) == 0x00f4)
        {
            // Reads from $F4-$F7 take more time than internal reads.
            Wait(true, address);
            data = ReadRam(address);
            if ((address & 0xfff0) == 0x00f0) data = ReadIo(address);
            Wait(true, address);
            return data;
        }

        Wait(false, address);
        data = ReadRam(address);
        if ((address & 0xfff0) == 0x00f0) data = ReadIo(address);
        return data;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Write(int address, byte data)
    {
        address &= 0xffff;
        Wait(false, address);
        if (address == WatchAddress) WatchHook?.Invoke(this, data);
        WriteRam(address, data);  // even I/O writes affect the underlying RAM
        if ((address & 0xfff0) == 0x00f0) WriteIo(address, data);
    }

    // ----- I/O registers -------------------------------------------------------------------------------

    private byte ReadIo(int address)
    {
        byte data;
        switch (address)
        {
            case 0xf0: return 0x00;  // TEST (write-only)
            case 0xf1: return 0x00;  // CONTROL (write-only)
            case 0xf2: return _dspAddress;
            case 0xf3: return _dsp.Read(_dspAddress);  // $80-$FF are read-only mirrors of $00-$7F
            case 0xf4: case 0xf5: case 0xf6: case 0xf7: return _portsIn[address - 0xf4];
            case 0xf8: return _aux4;
            case 0xf9: return _aux5;
            case 0xfa: case 0xfb: case 0xfc: return 0x00;  // timer targets (write-only)
            case 0xfd:
                data = (byte)_timer0.Stage3;
                _timer0.Stage3 = 0;
                return data;
            case 0xfe:
                data = (byte)_timer1.Stage3;
                _timer1.Stage3 = 0;
                return data;
            case 0xff:
                data = (byte)_timer2.Stage3;
                _timer2.Stage3 = 0;
                return data;
        }
        return 0;
    }

    private void WriteIo(int address, byte data)
    {
        switch (address)
        {
            case 0xf0:  // TEST
                if (_p) break;  // writes only take effect while the P flag is clear
                _timersDisable = (data & 0x01) != 0;
                _ramWritable = (data & 0x02) != 0;
                _ramDisable = (data & 0x04) != 0;
                _timersEnable = (data & 0x08) != 0;
                _externalWaitStates = (data >> 4) & 3;
                _internalWaitStates = (data >> 6) & 3;
                _normalSpeed = _externalWaitStates == 0 && _internalWaitStates == 0;
                SynchronizeStage1(ref _timer0, _stage1Of01);
                SynchronizeStage1(ref _timer1, _stage1Of01);
                SynchronizeStage1(ref _timer2, _stage1Of2);
                break;

            case 0xf1:  // CONTROL
                // A 0->1 transition resets the timer. (ares tests timer 2 with the condition inverted, a
                // typo against its own comment and bsnes; all three behave alike on hardware.)
                if (Raise(ref _timer0.Enable, (data & 1) != 0))
                {
                    _timer0.Stage2 = 0;
                    _timer0.Stage3 = 0;
                }
                if (Raise(ref _timer1.Enable, (data & 2) != 0))
                {
                    _timer1.Stage2 = 0;
                    _timer1.Stage3 = 0;
                }
                if (Raise(ref _timer2.Enable, (data & 4) != 0))
                {
                    _timer2.Stage2 = 0;
                    _timer2.Stage3 = 0;
                }

                if ((data & 0x10) != 0) _portsIn[0] = _portsIn[1] = 0;
                if ((data & 0x20) != 0) _portsIn[2] = _portsIn[3] = 0;

                _iplromEnable = (data & 0x80) != 0;
                break;

            case 0xf2: _dspAddress = data; break;
            case 0xf3:
                if ((_dspAddress & 0x80) != 0) break;  // $80-$FF are read-only mirrors
                DspWriteHook?.Invoke((_clock + _rebased) / 2, _dspAddress, data);
                _dsp.Write(_dspAddress, data);
                break;
            case 0xf4: case 0xf5: case 0xf6: case 0xf7: _portsOut[address - 0xf4] = data; break;
            case 0xf8: _aux4 = data; break;
            case 0xf9: _aux5 = data; break;
            case 0xfa: _timer0.Target = data; break;
            case 0xfb: _timer1.Target = data; break;
            case 0xfc: _timer2.Target = data; break;
            // $FD-$FF: the timer outputs are read-only.
        }
    }

    private static bool Raise(ref bool line, bool value)
    {
        var raised = !line && value;
        line = value;
        return raised;
    }

    // ----- Timing --------------------------------------------------------------------------------------

    // The DSP clock (24.576 MHz) / 12 feeds the SMP, and the wait states divide it further by {2, 4, 8, 16}.
    // Dividers of 8 and 16 are glitchy on hardware (10 and 20 clocks per cycle), but the timers still advance
    // by the expected amount.
    private static readonly int[] CycleWaitStates = [2, 4, 10, 20];
    private static readonly int[] TimerWaitStates = [2, 4, 8, 16];

    /// <summary>One bus cycle (or half of one): advances the clock, the DSP and the timers.</summary>
    /// <param name="halve">A half cycle, as the $F4-$F7 reads take.</param>
    /// <param name="address">The address accessed, or -1 for an idle cycle.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Wait(bool halve, int address)
    {
        var shift = halve ? 1 : 0;
        if (_normalSpeed)
        {
            // Normal speed: two clocks per cycle for the CPU and the timers alike.
            Step(2 >> shift);
            StepTimers(2 >> shift);
            return;
        }

        var waitStates = _externalWaitStates;
        if (address < 0) waitStates = _internalWaitStates;                              // idle cycles
        else if ((address & 0xfff0) == 0x00f0) waitStates = _internalWaitStates;        // I/O registers
        else if (address >= 0xffc0 && _iplromEnable) waitStates = _internalWaitStates;  // IPL ROM

        Step(CycleWaitStates[waitStates] >> shift);
        StepTimers(TimerWaitStates[waitStates] >> shift);
    }

    /// <summary>Test only: run the DSP up to the SMP's time rather than one clock past it, which is how
    /// snes_spc aligns the two. Lets the harness diff the emulations without that constant phase offset.</summary>
    internal static bool AlignLikeSnesSpc;

    /// <summary>Test only: observes every DSP register write (time in SMP cycles, register, value).</summary>
    internal static Action<long, int, int>? DspWriteHook;

    /// <summary>Test only: observes CPU writes to <see cref="WatchAddress"/>.</summary>
    internal static int WatchAddress = -1;
    internal static Action<Smp, byte>? WatchHook;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Step(int clocks)
    {
        _clock += clocks;
        // As ares's cooperative scheduler does, the DSP runs until it is past the SMP's time, so it is one
        // clock ahead when the access happens.
        var limit = _clock - 1 + _dspLead;
        while (_dspClock <= limit)
        {
            _dsp.Clock();
            _dspClock += 2;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void StepTimers(int clocks)
    {
        // Stage 0: the prescalers; stage 1: a divide-by-two, whose falling edge the timers count.
        _prescaler01 += clocks;
        if (_prescaler01 >= Prescale01)
        {
            _prescaler01 -= Prescale01;
            _stage1Of01 ^= 1;
            SynchronizeStage1(ref _timer0, _stage1Of01);
            SynchronizeStage1(ref _timer1, _stage1Of01);
        }

        _prescaler2 += clocks;
        if (_prescaler2 >= Prescale2)
        {
            _prescaler2 -= Prescale2;
            _stage1Of2 ^= 1;
            SynchronizeStage1(ref _timer2, _stage1Of2);
        }
    }

    private void SynchronizeStage1(ref Timer timer, int stage1)
    {
        var level = stage1 != 0 && _timersEnable && !_timersDisable;

        // Only pulses on a 1->0 transition.
        var lowered = timer.Line && !level;
        timer.Line = level;
        if (!lowered) return;

        // Stage 2: counts up to the target (a target of 0 means 256).
        if (!timer.Enable) return;
        timer.Stage2 = (timer.Stage2 + 1) & 0xff;
        if (timer.Stage2 != timer.Target) return;

        // Stage 3: the 4-bit counter the SMP reads.
        timer.Stage2 = 0;
        timer.Stage3 = (timer.Stage3 + 1) & 0x0f;
    }

    /// <summary>Keeps the clock counters small; call between instructions.</summary>
    public void RebaseClock()
    {
        var shift = _clock & ~0xffffL;
        _clock -= shift;
        _dspClock -= shift;
        _rebased += shift;
    }

    private struct Timer
    {
        public int Stage2;   // n8
        public int Stage3;   // n4
        public bool Line;
        public bool Enable;
        public int Target;   // n8
    }
}
