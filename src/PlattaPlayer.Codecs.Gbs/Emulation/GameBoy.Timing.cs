namespace PlattaPlayer.Codecs.Gbs.Emulation;

// Time (SameBoy's timing.c): the DIV counter with the timer, serial clock and APU frame sequencer hanging off
// its falling edges, and GB_advance_cycles, which runs every other component for the T-cycles the CPU spends.
internal sealed partial class GameBoy
{
    private const byte TimaRunning = 0, TimaReloading = 1, TimaReloaded = 2;

    // The DIV bit whose falling edge clocks TIMA, per TAC's clock select.
    private static readonly ushort[] TacTriggerBits = [512, 8, 32, 128];

    private int _divCycles, _divState;
    private ushort _divCounter;
    private byte _timaReloadState;  // after TIMA overflows it reads 0 for 4 cycles before reloading
    private bool _serialMasterClock;
    private byte _serialMask;
    private byte _doubleSpeedAlignment;
    private byte _serialCount;
    private int _speedSwitchHaltCountdown;
    private byte _speedSwitchCountdown;
    private byte _speedSwitchFreeze;
    private uint _cyclesSinceVblankCallback;
    private bool _lcdDisabledOutsideOfVblank;

    private void ResetTiming()
    {
        _divCycles = _divState = 0;
        _divCounter = 0;
        _timaReloadState = TimaRunning;
        _serialMasterClock = false;
        _serialMask = 0;
        _doubleSpeedAlignment = 0;
        _serialCount = 0;
        _speedSwitchHaltCountdown = 0;
        _speedSwitchCountdown = 0;
        _speedSwitchFreeze = 0;
        _cyclesSinceVblankCallback = 0;
        _lcdDisabledOutsideOfVblank = false;
    }

    private void AdvanceTimaStateMachine()
    {
        if (_timaReloadState == TimaReloaded)
        {
            _timaReloadState = TimaRunning;
        }
        else if (_timaReloadState == TimaReloading)
        {
            _io[IoIf] |= 4;
            _timaReloadState = TimaReloaded;
        }
    }

    private void IncreaseTima()
    {
        _io[IoTima]++;
        if (_io[IoTima] == 0)
        {
            _io[IoTima] = _io[IoTma];
            _timaReloadState = TimaReloading;
        }
    }

    // No link cable: every 8 edges of an internally clocked transfer shift in a 1 and finish the byte.
    private void SerialMasterEdge()
    {
        _serialMasterClock ^= true;
        if (!_serialMasterClock && (_io[IoSc] & 0x81) == 0x81)
        {
            _serialCount++;
            if (_serialCount == 8)
            {
                _serialCount = 0;
                _io[IoSc] &= unchecked((byte)~0x80);
                _io[IoIf] |= 8;
            }
            _io[IoSb] <<= 1;
            _io[IoSb] |= 1;
        }
    }

    private void SetInternalDivCounter(ushort value)
    {
        // TIMA increases when a specific bit falls.
        var triggers = (ushort)(_divCounter & ~value);
        if ((_io[IoTac] & 4) != 0 && (triggers & TacTriggerBits[_io[IoTac] & 3]) != 0) IncreaseTima();
        if ((triggers & _serialMask) != 0) SerialMasterEdge();

        var apuBit = _doubleSpeed ? 0x2000 : 0x1000;
        if ((triggers & apuBit) != 0)
        {
            ApuDivEvent();
        }
        else
        {
            var secondaryTriggers = ~_divCounter & value;
            if ((secondaryTriggers & apuBit) != 0) ApuDivSecondaryEvent();
        }
        _divCounter = value;
    }

    // The DIV state machine: DIV advances by 4 every M-cycle, 3 T-cycles after reset.
    private void TimersRun(byte cycles)
    {
        if (_stopped)
        {
            _apu.ApuCycles += (ushort)(1 << (_doubleSpeed ? 0 : 1));
            _sampleCycles += (uint)((_sampleRate << (_doubleSpeed ? 0 : 1)) << 1);
            return;
        }

        _divCycles += cycles;
        if (_divCycles <= 0) return;
        switch (_divState)
        {
            case 1: goto State1;
            case 2: goto State2;
        }

        _divCycles -= 3;
        if (_divCycles <= 0)
        {
            _divState = 1;
            return;
        }
    State1:
    Loop:
        AdvanceTimaStateMachine();
        SetInternalDivCounter((ushort)(_divCounter + 4));
        _apu.ApuCycles += (ushort)(1 << (_doubleSpeed ? 0 : 1));
        _sampleCycles += (uint)((_sampleRate << (_doubleSpeed ? 0 : 1)) << 1);
        _divCycles -= 4;
        if (_divCycles <= 0)
        {
            _divState = 2;
            return;
        }
    State2:
        if (_apu.PendingEnvelopeTick) ApuDelayedEnvelopeTick();
        goto Loop;
    }

    // The CGB's infrared port: the sensor sees only our own LED (no other device).
    private void IrRun(uint cycles)
    {
        // Fast path: not sensing and fully decayed (what the general case below computes too).
        if (_irSensor == 0 && (_io[IoRp] & 0xC0) != 0xC0)
        {
            _effectiveIrInput = false;
            return;
        }
        var isSensing = (_io[IoRp] & 0xC0) == 0xC0;
        if (isSensing && (_io[IoRp] & 1) != 0)
        {
            _irSensor += (int)cycles;
            if (_irSensor > IrMax) _irSensor = IrMax;
            _effectiveIrInput = _irSensor >= IrWarmup + IrThreshold && _irSensor <= IrWarmup + IrThreshold + IrDecay;
        }
        else
        {
            var target = isSensing ? IrWarmup : 0;
            if (_irSensor < target) _irSensor += (int)cycles;
            else if ((uint)_irSensor <= (uint)target + cycles) _irSensor = target;
            else _irSensor -= (int)cycles;
            _effectiveIrInput = false;
        }
    }

    private const int IrDecay = 31500, IrWarmup = 19900, IrThreshold = 240, IrMax = IrThreshold * 2 + IrDecay + 268;

    /// <summary>Runs everything but the CPU for <paramref name="cycles"/> T-cycles (GB_advance_cycles).</summary>
    private void AdvanceCycles(byte cycles)
    {
        if (_speedSwitchCountdown != 0)
        {
            if (_speedSwitchCountdown == cycles)
            {
                _doubleSpeed ^= true;
                _speedSwitchCountdown = 0;
            }
            else if (_speedSwitchCountdown > cycles)
            {
                _speedSwitchCountdown -= cycles;
            }
            else
            {
                var oldCycles = _speedSwitchCountdown;
                cycles -= oldCycles;
                _speedSwitchCountdown = 0;
                AdvanceCycles(oldCycles);
                _doubleSpeed ^= true;
            }
        }
        _apu.PcmMask0 = _apu.PcmMask1 = 0xFF;
        // Affected by speed boost.
        _dmaCycles = cycles;

        TimersRun(cycles);

        if (_speedSwitchHaltCountdown != 0)
        {
            _speedSwitchHaltCountdown -= cycles;
            if (_speedSwitchHaltCountdown <= 0)
            {
                _speedSwitchHaltCountdown = 0;
                _halted = false;
            }
        }

        if (_speedSwitchFreeze != 0)
        {
            if (_speedSwitchFreeze >= cycles)
            {
                _speedSwitchFreeze -= cycles;
                return;
            }
            cycles -= _speedSwitchFreeze;
            _speedSwitchFreeze = 0;
        }

        // From here on, in 8 MHz units: not affected by speed boost.
        if (!_doubleSpeed) cycles <<= 1;

        if ((_io[IoLcdc] & LcdcEnable) != 0) _doubleSpeedAlignment += cycles;

        if (_dataBusDecayCountdown != 0)
        {
            if (_dataBusDecayCountdown <= cycles)
            {
                _dataBusDecayCountdown = 0;
                _dataBus = 0xFF;
            }
            else
            {
                _dataBusDecayCountdown -= cycles;
            }
        }

        ApuRun(false);
        DisplayTick(cycles);
        if (!_stopped && _dmaCurrentDest != 0xA1) DmaRun();
        IrRun(cycles);
    }

    // Changing TAC can clock TIMA when the selected DIV bit was high and becomes (effectively) low.
    private void EmulateTimerGlitch(byte oldTac, byte newTac)
    {
        if ((oldTac & 4) == 0) return;
        var oldClocks = TacTriggerBits[oldTac & 3];
        var newClocks = TacTriggerBits[newTac & 3];
        if ((_divCounter & oldClocks) != 0 && ((newTac & 4) == 0 || (_divCounter & newClocks) == 0)) IncreaseTima();
    }
}
