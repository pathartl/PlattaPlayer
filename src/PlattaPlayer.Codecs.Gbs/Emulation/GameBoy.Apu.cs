namespace PlattaPlayer.Codecs.Gbs.Emulation;

// The APU's channels, frame sequencer and registers (SameBoy's apu.c, CGB-E paths), including SameBoy's
// catalogue of hardware quirks: the zombie-mode envelope (NRx2 glitch), sweep overflow checks, the
// length-enable extra clock, the CGB-E phantom step on restart, and the NR43 LFSR write glitches of its CGB-E.
internal sealed partial class GameBoy
{
    private const int Square1 = 0, Square2 = 1, Wave = 2, Noise = 3;
    private const byte SkipDivEventInactive = 0, SkipDivEventSkipped = 1, SkipDivEventSkip = 2;

    private ApuState _apu;

    private static readonly byte[] Duties =
    [
        0, 0, 0, 0, 0, 0, 0, 1,
        1, 0, 0, 0, 0, 0, 0, 1,
        1, 0, 0, 0, 0, 1, 1, 1,
        0, 1, 1, 1, 1, 1, 1, 0,
    ];

    // OR-ed into register reads ($FF10-$FF3F): unreadable bits read 1.
    private static readonly byte[] ReadMask =
    [
        0x80, 0x3F, 0x00, 0xFF, 0xBF,
        0xFF, 0x3F, 0x00, 0xFF, 0xBF,
        0x7F, 0xFF, 0x9F, 0xFF, 0xBF,
        0xFF, 0xFF, 0x00, 0x00, 0xBF,
        0x00, 0x00, 0x70, 0xFF, 0xFF,
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    ];

    // The NR43 "category 2" write glitch of SameBoy's CGB-E, indexed by old and new shift (octal digits).
    private static readonly byte[] Nr43GlitchMap = BuildNr43GlitchMap();

    private static byte[] BuildNr43GlitchMap()
    {
        var map = new byte[64];
        void Set(int octal, byte value) => map[octal / 10 * 8 + octal % 10] = value;
        Set(02, 4); Set(03, 2); Set(04, 2); Set(05, 2);
        Set(12, 2); Set(13, 4); Set(14, 2); Set(15, 2);
        Set(20, 1); Set(21, 2); Set(23, 1); Set(24, 5); Set(25, 3);
        Set(34, 2); Set(35, 2);
        Set(41, 2); Set(42, 2); Set(43, 2);
        Set(50, 6); Set(52, 2); Set(53, 2);
        return map;
    }

    private bool IsDacEnabled(int index) => index switch
    {
        Square1 => (_io[IoNr12] & 0xF8) != 0,
        Square2 => (_io[IoNr22] & 0xF8) != 0,
        Wave => _apu.Wave.Enable,
        _ => (_io[IoNr42] & 0xF8) != 0,
    };

    private void UpdateSquareSample(int index, uint cycles)
    {
        ref var channel = ref _apu.Square[index];
        if (channel.SampleSurpressed) return;
        var duty = _io[index == Square1 ? IoNr11 : IoNr21] >> 6;
        UpdateSample(index, (sbyte)(Duties[channel.CurrentSampleIndex + duty * 8] != 0 ? channel.CurrentVolume : 0), cycles);
    }

    private void UpdateWaveSample(uint cycles)
    {
        ref var wave = ref _apu.Wave;
        if ((wave.CurrentSampleIndex & 1) != 0)
            UpdateSample(Wave, (sbyte)((wave.CurrentSampleByte & 0xF) >> wave.Shift), cycles);
        else
            UpdateSample(Wave, (sbyte)((wave.CurrentSampleByte >> 4) >> wave.Shift), cycles);
    }

    private static void SetEnvelopeClock(ref EnvelopeClock clock, bool value, bool direction, byte volume)
    {
        if (clock.Clock == value) return;
        if (value)
        {
            clock.Clock = true;
            clock.ShouldLock = (volume == 0xF && direction) || (volume == 0x0 && !direction);
        }
        else
        {
            clock.Clock = false;
            clock.Locked |= clock.ShouldLock;
        }
    }

    // Writing NRx2 while the channel plays ("zombie mode") changes the volume in odd ways.
    private static void Nrx2Glitch(ref byte volume, byte value, byte oldValue, ref byte countdown, ref EnvelopeClock clock)
    {
        if (clock.Clock) countdown = (byte)(value & 7);
        var shouldTick = (value & 7) != 0 && (oldValue & 7) == 0 && !clock.Locked;
        var shouldInvert = ((value & 8) ^ (oldValue & 8)) != 0;

        if ((value & 0xF) == 8 && (oldValue & 0xF) == 8 && !clock.Locked) shouldTick = true;

        if (shouldInvert)
        {
            if ((value & 8) != 0)
            {
                if ((oldValue & 7) == 0 && !clock.Locked)
                {
                    volume ^= 0xF;
                }
                else
                {
                    volume = (byte)(0xE - volume);
                    volume &= 0xF;
                }
                shouldTick = false;
            }
            else
            {
                volume = (byte)(0x10 - volume);
                volume &= 0xF;
            }
        }
        if (shouldTick)
        {
            if ((value & 8) != 0) volume++;
            else volume--;
            volume &= 0xF;
        }
        else if ((value & 7) == 0 && clock.Clock)
        {
            SetEnvelopeClock(ref clock, false, false, 0);
        }
    }

    private void TickSquareEnvelope(int index)
    {
        ref var channel = ref _apu.Square[index];
        SetEnvelopeClock(ref channel.EnvelopeClock, false, false, 0);
        if (channel.EnvelopeClock.Locked) return;
        var nrx2 = _io[index == Square1 ? IoNr12 : IoNr22];
        if ((nrx2 & 7) == 0) return;
        if (_doubleSpeed)
        {
            if (index == Square1) _apu.PcmMask0 &= (byte)(_apu.Square[Square1].CurrentVolume | 0xF1);
            else _apu.PcmMask0 &= (byte)((_apu.Square[Square2].CurrentVolume << 4) | 0x3F);
        }

        SetEnvelopeClock(ref channel.EnvelopeClock, false, false, 0);
        if ((nrx2 & 8) != 0) channel.CurrentVolume++;
        else channel.CurrentVolume--;

        if (_apu.IsActive[index]) UpdateSquareSample(index, 0);
    }

    private void TickNoiseEnvelope()
    {
        ref var noise = ref _apu.Noise;
        SetEnvelopeClock(ref noise.EnvelopeClock, false, false, 0);
        if (noise.EnvelopeClock.Locked) return;
        var nr42 = _io[IoNr42];
        if ((nr42 & 7) == 0) return;
        if (_doubleSpeed) _apu.PcmMask1 &= (byte)((noise.CurrentVolume << 4) | 0x1F);

        if ((nr42 & 8) != 0) noise.CurrentVolume++;
        else noise.CurrentVolume--;

        if (_apu.IsActive[Noise]) UpdateSample(Noise, (sbyte)((noise.Lfsr & 1) != 0 ? noise.CurrentVolume : 0), 0);
    }

    private void SweepCalculationDone(uint cycles)
    {
        // APU bug: the sweep frequency is checked after adding the sweep delta twice.
        if (_apu.Channel1RestartHold == 0) _apu.ShadowSweepSampleLength = _apu.Square[Square1].SampleLength;
        if ((_io[IoNr10] & 8) != 0) _apu.SweepLengthAddend ^= 0x7FF;
        if (_apu.ShadowSweepSampleLength + _apu.SweepLengthAddend > 0x7FF && (_io[IoNr10] & 8) == 0)
        {
            _apu.IsActive[Square1] = false;
            UpdateSample(Square1, 0, (uint)(_apu.SquareSweepCalculateCountdown * 2) - cycles);
        }
        _apu.Channel1CompletedAddend = _apu.SweepLengthAddend;
    }

    private void TriggerSweepCalculation()
    {
        if ((_io[IoNr10] & 0x70) == 0 || _apu.SquareSweepCountdown != 7) return;
        if ((_io[IoNr10] & 0x07) != 0)
        {
            _apu.Square[Square1].SampleLength = (ushort)(_apu.SweepLengthAddend + _apu.ShadowSweepSampleLength + ((_io[IoNr10] & 0x8) != 0 ? 1 : 0));
            _apu.Square[Square1].SampleLength &= 0x7FF;
        }
        if (_apu.Channel1RestartHold == 0)
        {
            _apu.SweepLengthAddend = _apu.Square[Square1].SampleLength;
            _apu.SweepLengthAddend >>= _io[IoNr10] & 7;
        }

        // Recalculation and the overflow check only happen after a delay.
        _apu.SquareSweepCalculateCountdown = (byte)(_io[IoNr10] & 0x7);
        _apu.SquareSweepCalculateCountdownReloadTimer = (byte)(1 + _apu.LfDiv);
        if (!_doubleSpeed && _duringDivWrite) _apu.SquareSweepCalculateCountdownReloadTimer = 1;
        _apu.UnshiftedSweep = (_io[IoNr10] & 0x7) == 0;
        _apu.SquareSweepCountdown = (byte)(((_io[IoNr10] >> 4) & 7) ^ 7);
        if (_apu.SquareSweepCalculateCountdown == 0) _apu.SquareSweepInstantCalculationDone = true;
    }

    private void ApuDelayedEnvelopeTick()
    {
        _apu.PendingEnvelopeTick = false;
        if (!_apu.GlobalEnable) return;

        ApuRun(true);
        _apu.PcmMask0 = _apu.PcmMask1 = 0xFF;

        for (var i = Square1; i <= Square2; i++)
            if (_apu.Square[i].EnvelopeClock.Clock) TickSquareEnvelope(i);
        if (_apu.Noise.EnvelopeClock.Clock) TickNoiseEnvelope();
    }

    // A falling edge of DIV bit 4 (bit 5 in double speed): the 512 Hz frame sequencer step.
    private void ApuDivEvent()
    {
        ApuRun(true);
        _apu.PcmMask0 = _apu.PcmMask1 = 0xFF;

        if (!_apu.GlobalEnable) return;
        if (_apu.SkipDivEvent == SkipDivEventSkip)
        {
            _apu.SkipDivEvent = SkipDivEventSkipped;
            return;
        }
        if (_apu.SkipDivEvent == SkipDivEventSkipped) _apu.SkipDivEvent = SkipDivEventInactive;
        else _apu.DivDivider++;

        if ((_apu.DivDivider & 7) == 7)
        {
            for (var i = Square1; i <= Square2; i++)
            {
                ref var channel = ref _apu.Square[i];
                if (!channel.EnvelopeClock.Clock)
                {
                    channel.VolumeCountdown--;
                    channel.VolumeCountdown &= 7;
                }
            }
            if (!_apu.Noise.EnvelopeClock.Clock)
            {
                _apu.Noise.VolumeCountdown--;
                _apu.Noise.VolumeCountdown &= 7;
            }
        }

        if (_doubleSpeed)
        {
            // CGB-D/E: in double speed the envelope ticks a cycle later.
            _apu.PendingEnvelopeTick = true;
        }
        else
        {
            for (var i = Square1; i <= Square2; i++)
                if (_apu.Square[i].EnvelopeClock.Clock) TickSquareEnvelope(i);
            if (_apu.Noise.EnvelopeClock.Clock) TickNoiseEnvelope();
        }

        if ((_apu.DivDivider & 1) == 1)
        {
            for (var i = Square1; i <= Square2; i++)
            {
                ref var channel = ref _apu.Square[i];
                if (channel.LengthEnabled && channel.PulseLength != 0 && --channel.PulseLength == 0)
                {
                    _apu.IsActive[i] = false;
                    UpdateSample(i, 0, 0);
                }
            }

            if (_apu.Wave.LengthEnabled && _apu.Wave.PulseLength != 0 && --_apu.Wave.PulseLength == 0)
            {
                _apu.IsActive[Wave] = false;
                UpdateSample(Wave, 0, 0);
            }

            if (_apu.Noise.LengthEnabled && _apu.Noise.PulseLength != 0 && --_apu.Noise.PulseLength == 0)
            {
                _apu.IsActive[Noise] = false;
                UpdateSample(Noise, 0, 0);
            }
        }

        if ((_apu.DivDivider & 3) == 3)
        {
            _apu.SquareSweepCountdown++;
            _apu.SquareSweepCountdown &= 7;
            TriggerSweepCalculation();
        }
    }

    // A rising edge of the same DIV bit: envelope countdown reloads.
    private void ApuDivSecondaryEvent()
    {
        ApuRun(true);
        _apu.PcmMask0 = _apu.PcmMask1 = 0xFF;

        if (!_apu.GlobalEnable) return;
        for (var i = Square1; i <= Square2; i++)
        {
            ref var channel = ref _apu.Square[i];
            var nrx2 = _io[i == Square1 ? IoNr12 : IoNr22];
            if (_apu.IsActive[i] && channel.VolumeCountdown == 0)
            {
                channel.VolumeCountdown = (byte)(nrx2 & 7);
                SetEnvelopeClock(ref channel.EnvelopeClock, channel.VolumeCountdown != 0, (nrx2 & 8) != 0, channel.CurrentVolume);
            }
        }

        if (_apu.IsActive[Noise] && _apu.Noise.VolumeCountdown == 0)
        {
            _apu.Noise.VolumeCountdown = (byte)(_io[IoNr42] & 7);
            SetEnvelopeClock(ref _apu.Noise.EnvelopeClock, _apu.Noise.VolumeCountdown != 0, (_io[IoNr42] & 8) != 0,
                _apu.Noise.CurrentVolume);
        }
    }

    private void UpdateLfsr(uint cyclesOffset)
    {
        _apu.Noise.CurrentLfsrSample = (_apu.Noise.Lfsr & 1) != 0;
        if (_apu.IsActive[Noise])
            UpdateSample(Noise, (sbyte)(_apu.Noise.CurrentLfsrSample ? _apu.Noise.CurrentVolume : 0), cyclesOffset);
    }

    private void StepLfsr(uint cyclesOffset)
    {
        ref var noise = ref _apu.Noise;
        _apu.LfsrBit7BeforeStep = (noise.Lfsr & 0x80) != 0;
        var highBitMask = noise.Narrow ? 0x4040 : 0x4000;
        var newHighBit = ((noise.Lfsr ^ (noise.Lfsr >> 1) ^ 1) & 1) != 0;
        noise.Lfsr >>= 1;
        if (newHighBit) noise.Lfsr |= (ushort)highBitMask;
        else noise.Lfsr &= (ushort)~highBitMask;  // relevant when switching LFSR widths

        UpdateLfsr(cyclesOffset);
        _apu.LfsrSteppedInNarrow = noise.Narrow;
    }

    /// <summary>Runs the channels for the APU cycles accumulated since the last run (GB_apu_run). Unless forced,
    /// runs are batched until a sample is due or a channel needs cycle-exact attention.</summary>
    private void ApuRun(bool force)
    {
        const uint clockRate = ClockRate;
        var origForce = force;

    Restart:
        var cycles = _apu.ApuCycles;

        if (force
            || cycles + _cyclesSinceRender >= _maxCyclesPerSample
            || _sampleCycles >= clockRate
            || _apu.SquareSweepCalculateCountdown != 0 || _apu.Channel1RestartHold != 0
            || _apu.SquareSweepCalculateCountdownReloadTimer != 0
            || _apu.Wave.BuggedReadCountdown != 0 || (_apu.Wave.Enable && _apu.Wave.Pulsed))
        {
            force = true;
        }
        if (!force) return;

        // Renders are never more than _maxCyclesPerSample apart: split long runs.
        while (cycles + _cyclesSinceRender > _maxCyclesPerSample)
        {
            if (_cyclesSinceRender > _maxCyclesPerSample) break;

            _apu.ApuCycles = (ushort)(_maxCyclesPerSample - _cyclesSinceRender);
            if (_apu.ApuCycles != 0)
            {
                cycles -= _apu.ApuCycles;
                ApuRun(true);
                if (!origForce)
                {
                    force = false;
                    _apu.ApuCycles = cycles;
                    goto Restart;
                }
                continue;
            }

            if (_sampleCycles >= clockRate)
            {
                _sampleCycles -= clockRate;
                Render();
            }
            break;
        }

        _apu.ApuCycles = 0;
        if (cycles == 0)
        {
            while (_sampleCycles >= clockRate)
            {
                _sampleCycles -= clockRate;
                Render();
            }
            return;
        }

        if (_apu.Wave.BuggedReadCountdown != 0)
        {
            var cyclesLeft = cycles;
            while (cyclesLeft != 0)
            {
                cyclesLeft--;
                if (--_apu.Wave.BuggedReadCountdown == 0)
                {
                    _apu.Wave.CurrentSampleByte = _io[IoWavStart + (_addressBus & 0xF)];
                    if (_apu.IsActive[Wave]) UpdateWaveSample(0);
                    break;
                }
            }
        }

        // To align the square signal to 1 MHz.
        _apu.LfDiv ^= (byte)(cycles & 1);
        _apu.Noise.Alignment += (byte)cycles;

        uint sweepCycles = (uint)(cycles / 2);
        if ((cycles & 1) != 0 && _apu.LfDiv == 0) sweepCycles++;

        if (_apu.SquareSweepCalculateCountdownReloadTimer > sweepCycles)
        {
            _apu.SquareSweepCalculateCountdownReloadTimer -= (byte)sweepCycles;
            sweepCycles = 0;
        }
        else
        {
            if (_apu.SquareSweepCalculateCountdownReloadTimer != 0 && _apu.SquareSweepCalculateCountdown == 0
                && _apu.SquareSweepInstantCalculationDone)
            {
                SweepCalculationDone(cycles);
            }
            _apu.SquareSweepInstantCalculationDone = false;
            sweepCycles -= _apu.SquareSweepCalculateCountdownReloadTimer;
            _apu.SquareSweepCalculateCountdownReloadTimer = 0;
        }

        // Calculation is paused while the shift is 0.
        if (_apu.SquareSweepCalculateCountdown != 0 && ((_io[IoNr10] & 7) != 0 || _apu.UnshiftedSweep))
        {
            if (_apu.SquareSweepCalculateCountdown > sweepCycles)
            {
                _apu.SquareSweepCalculateCountdown -= (byte)sweepCycles;
            }
            else
            {
                _apu.SquareSweepCalculateCountdown = 0;
                SweepCalculationDone(cycles);
            }
        }

        if (_apu.Channel1RestartHold != 0)
        {
            if (_apu.Channel1RestartHold > cycles) _apu.Channel1RestartHold -= (byte)cycles;
            else _apu.Channel1RestartHold = 0;
        }

        for (var i = Square1; i <= Square2; i++)
        {
            if (!_apu.IsActive[i]) continue;
            ref var channel = ref _apu.Square[i];
            var cyclesLeft = cycles;
            if (channel.Delay != 0)
            {
                if (channel.Delay < cyclesLeft) channel.Delay = 0;
                else channel.Delay -= (byte)cyclesLeft;
            }
            while (cyclesLeft > channel.SampleCountdown)
            {
                cyclesLeft -= (ushort)(channel.SampleCountdown + 1);
                channel.SampleCountdown = (ushort)((channel.SampleLength ^ 0x7FF) * 2 + 1);
                channel.CurrentSampleIndex++;
                channel.CurrentSampleIndex &= 0x7;
                channel.SampleSurpressed = false;
                if (cyclesLeft == 0 && _apu.Samples[i] == 0) _apu.PcmMask0 &= (byte)(i == Square1 ? 0xF0 : 0x0F);
                channel.DidTick = true;
                UpdateSquareSample(i, (uint)(cycles - cyclesLeft));
            }
            channel.JustReloaded = cyclesLeft == 0;
            if (cyclesLeft != 0) channel.SampleCountdown -= cyclesLeft;
        }

        _apu.Wave.WaveFormJustRead = false;
        if (_apu.IsActive[Wave])
        {
            ref var wave = ref _apu.Wave;
            var cyclesLeft = cycles;
            while (cyclesLeft > wave.SampleCountdown)
            {
                cyclesLeft -= (ushort)(wave.SampleCountdown + 1);
                wave.SampleCountdown = (ushort)(wave.SampleLength ^ 0x7FF);
                wave.CurrentSampleIndex++;
                wave.CurrentSampleIndex &= 0x1F;
                wave.CurrentSampleByte = _io[IoWavStart + (wave.CurrentSampleIndex >> 1)];
                UpdateWaveSample((uint)(cycles - cyclesLeft));
                wave.WaveFormJustRead = true;
            }
            if (cyclesLeft != 0)
            {
                wave.SampleCountdown -= cyclesLeft;
                wave.WaveFormJustRead = false;
            }
        }
        else if (_apu.Wave.Enable && _apu.Wave.Pulsed)
        {
            // A DAC-on but stopped wave channel keeps fetching wave RAM at whatever address is on the bus.
            ref var wave = ref _apu.Wave;
            var cyclesLeft = cycles;
            while (cyclesLeft > wave.SampleCountdown)
            {
                cyclesLeft -= (ushort)(wave.SampleCountdown + 1);
                wave.SampleCountdown = (ushort)(wave.SampleLength ^ 0x7FF);
                if (cyclesLeft != 0) wave.CurrentSampleByte = _io[IoWavStart + (_addressBus & 0xF)];
                else wave.BuggedReadCountdown = 1;
            }
            if (cyclesLeft != 0) wave.SampleCountdown -= cyclesLeft;
            if (wave.SampleCountdown == 0) wave.BuggedReadCountdown = 2;
        }

        if (_apu.NoiseCounterActive || _apu.NoiseBackgroundCounterActive)
        {
            ref var noise = ref _apu.Noise;
            var cyclesLeft = cycles;
            var divisor = (uint)(_io[IoNr43] & 0x07) << 2;
            if (divisor == 0) divisor = 2;
            if (noise.CounterCountdown == 0) noise.CounterCountdown = (byte)divisor;
            while (cyclesLeft >= noise.CounterCountdown)
            {
                cyclesLeft -= noise.CounterCountdown;
                noise.CounterCountdown = (byte)divisor;
                var mask = 1 << (_io[IoNr43] >> 4);
                var oldBit = (noise.Counter & mask) != 0;
                noise.Counter++;
                noise.Counter &= 0x3FFF;
                noise.DidStepCounter = true;
                var newBit = (noise.Counter & mask) != 0;

                if (newBit && !oldBit && _apu.IsActive[Noise])
                {
                    if (cyclesLeft == 0 && _apu.Samples[Noise] == 0 && !_doubleSpeed) _apu.PcmMask1 &= 0x0F;
                    StepLfsr((uint)(cycles - cyclesLeft));
                }
            }
            if (cyclesLeft != 0)
            {
                noise.CounterCountdown -= (byte)cyclesLeft;
                noise.CountdownReloaded = false;
            }
            else
            {
                noise.CountdownReloaded = true;
            }
        }

        if (_sampleRate != 0)
        {
            _cyclesSinceRender += cycles;
            _sampleFraction += SampleFractionMultiply(cycles);
            if (_sampleCycles >= clockRate)
            {
                _sampleCycles -= clockRate;
                Render();
            }
        }
    }

    private void ApuInit()
    {
        _apu = default;
        _apu.ApuCyclesIn2Mhz = true;
        _apu.LfDiv = 1;
        _apu.Wave.Shift = 4;
        // Turning the APU on while DIV's bit 4 (5 in double speed) is set skips the first frame sequencer step.
        if ((_divCounter & (_doubleSpeed ? 0x2000 : 0x1000)) != 0)
        {
            _apu.SkipDivEvent = SkipDivEventSkip;
            _apu.DivDivider = 1;
        }
        _apu.Square[Square1].SampleCountdown = 0xFFFF;
        _apu.Square[Square2].SampleCountdown = 0xFFFF;
    }

    private byte ApuRead(int register)
    {
        ApuRun(true);
        if (register == IoNr52)
        {
            var value = 0;
            for (var i = 0; i < 4; i++)
            {
                value >>= 1;
                if (_apu.IsActive[i]) value |= 0x8;
            }
            if (_apu.GlobalEnable) value |= 0x80;
            return (byte)(value | 0x70);
        }

        // While the wave channel plays, wave RAM reads give the byte it is playing.
        if (register is >= IoWavStart and <= IoWavEnd && _apu.IsActive[Wave])
            register = IoWavStart + _apu.Wave.CurrentSampleIndex / 2;

        return (byte)(_io[register] | ReadMask[register - IoNr10]);
    }

    private void Nr10WriteGlitch(byte value)
    {
        if (_apu.SquareSweepCalculateCountdownReloadTimer == 2)
        {
            // The countdown just reloaded: reload it again.
            _apu.SquareSweepCalculateCountdown = (byte)(value & 0x7);
            if (_apu.SquareSweepCalculateCountdown == 0) _apu.SquareSweepCalculateCountdownReloadTimer = 0;
        }
        if ((value & 7) != 0 && (_io[IoNr10] & 7) == 0 && _apu.LfDiv == 0 && _apu.SquareSweepCalculateCountdown > 1)
        {
            _apu.SquareSweepCalculateCountdown--;
            if (_apu.SquareSweepCalculateCountdown == 0) SweepCalculationDone(0);
        }
    }

    private void PrepareNoiseStart()
    {
        ref var noise = ref _apu.Noise;
        _apu.NoiseCounterActive = (_io[IoNr42] & 0xF8) != 0;  // resets on APU off and DAC disable
        var wasStartedWithDacDisabled = _apu.NoiseStartedWithDacDisabled;
        _apu.NoiseStartedWithDacDisabled = !_apu.NoiseCounterActive;
        var divisor = _io[IoNr43] & 0x07;
        var wasBackgroundCounting = _apu.NoiseBackgroundCounterActive;
        _apu.NoiseBackgroundCounterActive = true;
        var instantStep = false;
        var div1Glitch = false;

        if (divisor > 1 && noise.CounterCountdown == 1)
        {
            noise.Counter++;
            noise.Counter &= 0x3FFF;
        }
        else if (noise.CounterCountdown == 2 && (noise.Alignment & 3) == 0 && _apu.IsActive[Noise])
        {
            if (divisor == 0)
            {
                divisor = 8;
            }
            else if (divisor == 1)
            {
                if (!noise.DidStepCounter) div1Glitch = true;
                var mask = 1 << (_io[IoNr43] >> 4);
                var oldBit = (noise.Counter & mask) != 0;
                noise.Counter++;
                noise.Counter &= 0x3FFF;
                var newBit = (noise.Counter & mask) != 0;
                if (newBit && !oldBit) instantStep = true;
            }
        }
        noise.CounterCountdown = (byte)(divisor == 0 ? 6 : divisor * 4 + 6);
        if ((noise.Alignment & 1) != 0)
        {
            if (divisor == 0)
            {
                if (wasBackgroundCounting) noise.CounterCountdown--;
                else noise.CounterCountdown++;
            }
            else if ((noise.Alignment & 2) != 0)
            {
                if (divisor == 1 && !_apu.IsActive[Noise]) noise.CounterCountdown++;
                else noise.CounterCountdown -= 3;
            }
            else
            {
                noise.CounterCountdown--;
                if (divisor == 1 && _apu.IsActive[Noise]) noise.CounterCountdown -= 4;
            }
        }
        else if (divisor != 0)
        {
            if ((noise.Alignment & 2) != 0) noise.CounterCountdown -= 2;
            else if (divisor > 1) noise.CounterCountdown -= 4;
            else if (divisor == 1 && _apu.IsActive[Noise] && (_io[IoNr43] & 0xF0) == 0) noise.CounterCountdown -= 4;
        }

        // Background counting glitches.
        if (divisor > 1)
        {
            if (!_apu.NoiseCounterActive && (noise.Alignment & 3) == 0) noise.CounterCountdown += 4;
        }
        else if (wasBackgroundCounting && !_apu.IsActive[Noise] && (noise.Alignment & 3) == 0)
        {
            if (divisor == 0)
            {
                if (wasStartedWithDacDisabled) noise.CounterCountdown += 28;
            }
            else
            {
                noise.CounterCountdown -= 4;
            }
        }

        if (div1Glitch) noise.CounterCountdown -= 4;

        noise.Lfsr = (ushort)(divisor == 0 && _apu.IsActive[Noise] && (noise.Alignment & 3) == 3 ? 0x0055 : 0);
        if (instantStep) StepLfsr(0);
    }

    // Writing NR43 can clock the LFSR, sometimes with corruption, depending on the counter bits the old and new
    // shift select (SameBoy's deterministic model of its own CGB-E).
    private void Nr43Write(byte value)
    {
        ref var noise = ref _apu.Noise;
        var oldNarrow = noise.Narrow;
        noise.Narrow = (value & 8) != 0;
        var old = _io[IoNr43];
        _io[IoNr43] = value;

        if ((old & 0xF0) == (value & 0xF0)) return;

        var effectiveCounter = noise.Counter;
        var oldBit = ((effectiveCounter >> (old >> 4)) & 1) != 0;
        var glitchValue = (old & 0x7F) | (value & 0x80);
        var glitchBit = ((effectiveCounter >> (glitchValue >> 4)) & 1) != 0;
        var newBit = ((effectiveCounter >> (value >> 4)) & 1) != 0;

        if (oldBit == newBit && newBit != glitchBit)
        {
            if (newBit)
            {
                // Category 1.
                if ((value & 0x80) == 0)
                {
                    StepLfsr(0);
                }
                else
                {
                    var t1 = (old >> 4) & 7;
                    var t2 = (value >> 4) & 7;
                    if ((t1 ^ 7) + t2 > 7 || ((t1 ^ 7) & t2) != 0)
                    {
                        // Copy bit 8 to bit 7.
                        noise.Lfsr &= unchecked((ushort)~0x80);
                        noise.Lfsr |= (ushort)((noise.Lfsr >> 1) & 0x80);

                        if ((t1 == 0 || t1 == 4) && t2 == 3)
                        {
                            noise.Lfsr &= (ushort)((noise.Lfsr >> 1) | 0x545);
                            UpdateLfsr(0);
                        }
                        else if (t1 == 2 && t2 == 3)
                        {
                            var mask = 0x555;
                            if ((noise.Lfsr & 0xC) == 0xC) mask |= 8;
                            if ((noise.Lfsr & 0xC00) == 0xC00) mask |= 0x800;
                            noise.Lfsr &= (ushort)((noise.Lfsr >> 1) | mask);
                            UpdateLfsr(0);
                        }
                        if (!noise.Narrow && oldNarrow && _apu.LfsrSteppedInNarrow)
                        {
                            if (_apu.LfsrBit7BeforeStep) noise.Lfsr |= 0x40;
                            else noise.Lfsr &= unchecked((ushort)~0x40);
                        }
                        noise.Lfsr |= (ushort)(noise.Narrow ? 0x4040 : 0x4000);
                        _apu.LfsrSteppedInNarrow = noise.Narrow;
                    }
                }
            }
            else
            {
                // Category 2.
                var glitch = (value & 0x80) != 0 ? Nr43GlitchMap[((old & 0x70) >> 1) | ((value & 0x70) >> 4)] : 0;
                switch (glitch)
                {
                    case 1: // step, then bit 1 &= bit 0
                    case 6: // like 1, with the bit below the LFSR's top glitched too
                        StepLfsr(0);
                        if (glitch == 6)
                        {
                            if ((noise.Narrow && (noise.Lfsr & 0x71) == 0x20) || (noise.Lfsr & 0x71) == 0x61)
                                noise.Lfsr &= unchecked((ushort)~0x20);
                            if ((noise.Lfsr & 0x7001) == 0x2000 || (noise.Lfsr & 0x7001) == 0x6001)
                                noise.Lfsr &= unchecked((ushort)~0x2000);
                        }
                        if ((noise.Lfsr & 0x3) == 2) noise.Lfsr &= unchecked((ushort)~2);
                        break;
                    case 2: // step, AND with the previous value except bit 0
                    {
                        var prev = noise.Lfsr;
                        StepLfsr(0);
                        noise.Lfsr &= (ushort)(prev | 1);
                        break;
                    }
                    case 5:
                    case 3:
                        if (glitch == 5)
                        {
                            if ((noise.Lfsr & 0x3) == 2) noise.Lfsr &= (ushort)(noise.Narrow ? ~0x4040 : ~0x4000);
                            if ((noise.Lfsr & 0x19) == 8) noise.Lfsr &= unchecked((ushort)~8);
                        }
                        // No step: bit 0 = bit 1.
                        noise.Lfsr &= unchecked((ushort)~1);
                        noise.Lfsr |= (ushort)((noise.Lfsr >> 1) & 1);
                        UpdateLfsr(0);
                        _apu.LfsrSteppedInNarrow = noise.Narrow;
                        break;
                    case 4: // step, bit 1 &= bit 0, the bit below the top &= the top
                    {
                        var prev = noise.Lfsr;
                        StepLfsr(0);
                        noise.Lfsr &= (ushort)(prev | (noise.Narrow ? ~0x2022 : ~0x2002));
                        break;
                    }
                    default:
                        StepLfsr(0);
                        break;
                }
            }
        }
        else if (!oldBit && newBit)
        {
            StepLfsr(0);
        }
    }

    private void ApuWrite(int register, byte value)
    {
        ApuRun(true);
        // While the APU is off only NR52 and wave RAM can be written (on a CGB, the lengths too are locked).
        if (!_apu.GlobalEnable && register != IoNr52 && register < IoWavStart) return;

        // While the wave channel plays, wave RAM writes go to the byte it is playing.
        if (register is >= IoWavStart and <= IoWavEnd && _apu.IsActive[Wave])
            register = IoWavStart + _apu.Wave.CurrentSampleIndex / 2;

        switch (register)
        {
            case IoNr50:
            case IoNr51:
                _io[register] = value;
                // Re-output every channel's current sample through the new volumes/panning.
                for (var i = 4; i-- > 0;)
                {
                    var sample = (sbyte)_apu.Samples[i];
                    _apu.Samples[i] = 0x10;  // invalidate to force the update
                    UpdateSample(i, sample, 0);
                }
                break;

            case IoNr52:
                if ((value & 0x80) != 0 && !_apu.GlobalEnable)
                {
                    ApuInit();
                    _apu.GlobalEnable = true;
                }
                else if ((value & 0x80) == 0 && _apu.GlobalEnable)
                {
                    for (var i = 4; i-- > 0;) UpdateSample(i, 0, 0);
                    _apu = default;
                    Array.Clear(_io, IoNr10, IoWavStart - IoNr10);
                    _apu.GlobalEnable = false;
                    _apu.ApuCyclesIn2Mhz = true;
                }
                break;

            case IoNr10:
            {
                if (_apu.SquareSweepCalculateCountdown != 0 || _apu.SquareSweepCalculateCountdownReloadTimer != 0)
                    Nr10WriteGlitch(value);
                var oldNegate = (_io[IoNr10] & 8) != 0 ? 1 : 0;
                _io[IoNr10] = value;
                if (_apu.ShadowSweepSampleLength + _apu.Channel1CompletedAddend + oldNegate > 0x7FF && (value & 8) == 0)
                {
                    _apu.IsActive[Square1] = false;
                    UpdateSample(Square1, 0, 0);
                }
                TriggerSweepCalculation();
                break;
            }

            case IoNr11:
            case IoNr21:
            {
                var index = register == IoNr21 ? Square2 : Square1;
                _apu.Square[index].PulseLength = (ushort)(0x40 - (value & 0x3F));
                break;
            }

            case IoNr12:
            case IoNr22:
            {
                var index = register == IoNr22 ? Square2 : Square1;
                if ((value & 0xF8) == 0)
                {
                    // This disables the DAC.
                    _io[register] = value;
                    _apu.IsActive[index] = false;
                    UpdateSample(index, 0, 0);
                }
                else if (_apu.IsActive[index])
                {
                    ref var channel = ref _apu.Square[index];
                    Nrx2Glitch(ref channel.CurrentVolume, value, _io[register], ref channel.VolumeCountdown, ref channel.EnvelopeClock);
                    UpdateSquareSample(index, 0);
                }
                break;
            }

            case IoNr13:
            case IoNr23:
            {
                ref var channel = ref _apu.Square[register == IoNr23 ? Square2 : Square1];
                channel.SampleLength &= unchecked((ushort)~0xFF);
                channel.SampleLength |= value;
                if (channel.JustReloaded) channel.SampleCountdown = (ushort)((channel.SampleLength ^ 0x7FF) * 2 + 1);
                break;
            }

            case IoNr14:
            case IoNr24:
                WriteNrx4(register == IoNr24 ? Square2 : Square1, register, value);
                break;

            case IoNr30:
                _apu.Wave.Enable = (value & 0x80) != 0;
                if (!_apu.Wave.Enable)
                {
                    _apu.Wave.Pulsed = false;
                    if (_apu.IsActive[Wave] && _apu.Wave.SampleCountdown == 0)
                        _apu.Wave.CurrentSampleByte = _io[IoWavStart + (_r[PC] & 0xF)];
                    _apu.IsActive[Wave] = false;
                    UpdateSample(Wave, 0, 0);
                }
                break;

            case IoNr31:
                _apu.Wave.PulseLength = (ushort)(0x100 - value);
                break;

            case IoNr32:
                _apu.Wave.Shift = ((value >> 5) & 3) switch { 0 => 4, 1 => 0, 2 => 1, _ => 2 };
                if (_apu.IsActive[Wave]) UpdateWaveSample(0);
                break;

            case IoNr33:
                _apu.Wave.SampleLength &= unchecked((ushort)~0xFF);
                _apu.Wave.SampleLength |= value;
                if (_apu.Wave.BuggedReadCountdown == 1) _apu.Wave.SampleCountdown = (ushort)(_apu.Wave.SampleLength ^ 0x7FF);
                break;

            case IoNr34:
            {
                ref var wave = ref _apu.Wave;
                wave.SampleLength &= 0xFF;
                wave.SampleLength |= (ushort)((value & 7) << 8);
                if ((value & 0x80) != 0)
                {
                    wave.Pulsed = true;
                    wave.CurrentSampleIndex = 0;
                    if (_apu.IsActive[Wave] && wave.SampleCountdown == 0) wave.CurrentSampleByte = _io[IoWavStart];
                    if (wave.Enable)
                    {
                        _apu.IsActive[Wave] = true;
                        UpdateSample(Wave, (sbyte)((wave.CurrentSampleByte >> 4) >> wave.Shift), 0);
                    }
                    wave.SampleCountdown = (ushort)((wave.SampleLength ^ 0x7FF) + 3);
                    if (wave.PulseLength == 0)
                    {
                        wave.PulseLength = 0x100;
                        wave.LengthEnabled = false;
                    }
                    // The sample itself doesn't change yet (verified on hardware).
                }

                // Enabling length while the frame sequencer's next step won't clock it clocks it once.
                if ((value & 0x40) != 0 && !wave.LengthEnabled && (_apu.DivDivider & 1) != 0 && wave.PulseLength != 0)
                {
                    wave.PulseLength--;
                    if (wave.PulseLength == 0)
                    {
                        if ((value & 0x80) != 0)
                        {
                            wave.PulseLength = 0xFF;
                        }
                        else
                        {
                            _apu.IsActive[Wave] = false;
                            UpdateSample(Wave, 0, 0);
                        }
                    }
                }
                wave.LengthEnabled = (value & 0x40) != 0;
                break;
            }

            case IoNr41:
                _apu.Noise.PulseLength = (ushort)(0x40 - (value & 0x3F));
                break;

            case IoNr42:
                if ((value & 0xF8) == 0)
                {
                    // This disables the DAC.
                    if (_apu.IsActive[Noise] && (_io[IoNr43] & 7) != 0)
                    {
                        if (_apu.Noise.CounterCountdown <= 2) _apu.Noise.Counter++;
                        _apu.NoiseBackgroundCounterActive = false;
                    }
                    _io[register] = value;
                    _apu.IsActive[Noise] = false;
                    UpdateSample(Noise, 0, 0);
                    _apu.NoiseCounterActive = false;
                }
                else if (_apu.IsActive[Noise])
                {
                    ref var noise = ref _apu.Noise;
                    Nrx2Glitch(ref noise.CurrentVolume, value, _io[register], ref noise.VolumeCountdown, ref noise.EnvelopeClock);
                    UpdateSample(Noise, (sbyte)(noise.CurrentLfsrSample ? noise.CurrentVolume : 0), 0);
                }
                break;

            case IoNr43:
                if (_apu.Noise.CountdownReloaded)
                {
                    var divisor = (value & 0x07) << 2;
                    if (divisor == 0) divisor = 2;
                    _apu.Noise.CounterCountdown = (byte)(divisor + (divisor == 2 ? 0 : (_apu.Noise.Alignment & 3) switch
                    {
                        0 => 2, 1 => 1, 2 => 0, _ => 3,
                    }));
                }
                Nr43Write(value);
                break;

            case IoNr44:
            {
                ref var noise = ref _apu.Noise;
                if ((value & 0x80) != 0)
                {
                    noise.EnvelopeClock.Locked = false;
                    noise.EnvelopeClock.Clock = false;
                    noise.Lfsr = 0;
                    PrepareNoiseStart();

                    noise.CurrentVolume = (byte)(_io[IoNr42] >> 4);
                    noise.CurrentLfsrSample = false;
                    noise.VolumeCountdown = (byte)(_io[IoNr42] & 7);
                    noise.DidStepCounter = (noise.Alignment & 3) == 2;

                    if ((_io[IoNr42] & 0xF8) != 0)
                    {
                        _apu.IsActive[Noise] = true;
                        UpdateSample(Noise, 0, 0);
                    }

                    if (noise.PulseLength == 0)
                    {
                        noise.PulseLength = 0x40;
                        noise.LengthEnabled = false;
                    }
                }

                if ((value & 0x40) != 0 && !noise.LengthEnabled && (_apu.DivDivider & 1) != 0 && noise.PulseLength != 0)
                {
                    noise.PulseLength--;
                    if (noise.PulseLength == 0)
                    {
                        if ((value & 0x80) != 0)
                        {
                            noise.PulseLength = 0x3F;
                        }
                        else
                        {
                            _apu.IsActive[Noise] = false;
                            UpdateSample(Noise, 0, 0);
                        }
                    }
                }
                noise.LengthEnabled = (value & 0x40) != 0;
                break;
            }
        }
        _io[register] = value;
    }

    // NR14/NR24: frequency high bits, length enable, restart.
    private void WriteNrx4(int index, int register, byte value)
    {
        ref var channel = ref _apu.Square[index];
        var wasActive = _apu.IsActive[index];
        // When the length changes right before being reloaded, from >= $700 to < $700, SameBoy steps the sample
        // index back (its write timing isn't T-cycle exact).
        if ((value & 0x80) == 0 && _apu.IsActive[index] && (_io[register] & 0x7) == 7 && (value & 7) != 7
            && channel.DidTick && channel.SampleCountdown >> 1 == (channel.SampleLength ^ 0x7FF))
        {
            channel.CurrentSampleIndex--;
            channel.CurrentSampleIndex &= 7;
            channel.SampleSurpressed = false;
        }

        var oldSampleLength = channel.SampleLength;
        channel.SampleLength &= 0xFF;
        channel.SampleLength |= (ushort)((value & 7) << 8);
        if (channel.JustReloaded) channel.SampleCountdown = (ushort)((channel.SampleLength ^ 0x7FF) * 2 + 1);

        if ((value & 0x80) != 0)
        {
            // The sample index is kept across restarts; only turning the APU off resets it.
            channel.EnvelopeClock.Locked = false;
            channel.EnvelopeClock.Clock = false;
            channel.DidTick = false;
            var forceUnsurpressed = false;
            if (!_apu.IsActive[index])
            {
                if ((value & 4) == 0 && (((channel.SampleCountdown - channel.Delay) / 2) & 0x400) == 0)
                {
                    channel.CurrentSampleIndex++;
                    channel.CurrentSampleIndex &= 0x7;
                    forceUnsurpressed = true;
                }
                channel.Delay = (byte)(6 - _apu.LfDiv);
                channel.SampleCountdown = (ushort)((channel.SampleLength ^ 0x7FF) * 2 + channel.Delay);
            }
            else
            {
                var extraDelay = 0;
                if (!channel.JustReloaded && (value & 4) == 0 && (((channel.SampleCountdown - 1 - channel.Delay) / 2) & 0x400) == 0)
                {
                    channel.CurrentSampleIndex++;
                    channel.CurrentSampleIndex &= 0x7;
                    channel.SampleSurpressed = false;
                }
                else if (channel.SampleLength == 0x7FF && oldSampleLength != 0x7FF && channel.SampleSurpressed)
                {
                    extraDelay += 2;
                }
                // Timing quirk: if already active, sound starts 2 (2 MHz) ticks earlier.
                channel.Delay = (byte)(4 - _apu.LfDiv + extraDelay);
                channel.SampleCountdown = (ushort)((channel.SampleLength ^ 0x7FF) * 2 + channel.Delay);
            }
            var nrx2 = _io[index == Square1 ? IoNr12 : IoNr22];
            channel.CurrentVolume = (byte)(nrx2 >> 4);
            // The volume change takes effect at once on the sound already playing.
            if (_apu.IsActive[index]) UpdateSquareSample(index, 0);

            channel.VolumeCountdown = (byte)(nrx2 & 7);

            if ((nrx2 & 0xF8) != 0 && !_apu.IsActive[index])
            {
                _apu.IsActive[index] = true;
                UpdateSample(index, 0, 0);
                channel.SampleSurpressed = !forceUnsurpressed;
            }
            if (channel.PulseLength == 0)
            {
                channel.PulseLength = 0x40;
                channel.LengthEnabled = false;
            }

            if (index == Square1)
            {
                _apu.SquareSweepInstantCalculationDone = false;
                _apu.ShadowSweepSampleLength = 0;
                _apu.Channel1CompletedAddend = 0;
                if ((_io[IoNr10] & 7) != 0)
                {
                    // APU bug: with a nonzero shift the overflow check also runs on trigger.
                    _apu.SquareSweepCalculateCountdown = (byte)(_io[IoNr10] & 0x7);
                    _apu.SquareSweepCalculateCountdownReloadTimer = 2;
                    _apu.UnshiftedSweep = false;
                    if (!wasActive) _apu.SquareSweepCalculateCountdownReloadTimer++;
                    _apu.SweepLengthAddend = channel.SampleLength;
                    _apu.SweepLengthAddend >>= _io[IoNr10] & 7;
                }
                else
                {
                    _apu.SweepLengthAddend = 0;
                }
                _apu.Channel1RestartHold = (byte)(4 - _apu.LfDiv);
                _apu.SquareSweepCountdown = (byte)(((_io[IoNr10] >> 4) & 7) ^ 7);
            }
        }

        // Enabling length while the frame sequencer's next step won't clock it clocks it once.
        if ((value & 0x40) != 0 && !channel.LengthEnabled && (_apu.DivDivider & 1) != 0 && channel.PulseLength != 0)
        {
            channel.PulseLength--;
            if (channel.PulseLength == 0)
            {
                if ((value & 0x80) != 0)
                {
                    channel.PulseLength = 0x3F;
                }
                else
                {
                    _apu.IsActive[index] = false;
                    UpdateSample(index, 0, 0);
                }
            }
        }
        channel.LengthEnabled = (value & 0x40) != 0;
    }
}
