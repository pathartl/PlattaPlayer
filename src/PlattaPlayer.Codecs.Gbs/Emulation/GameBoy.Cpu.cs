namespace PlattaPlayer.Codecs.Gbs.Emulation;

// The SM83 CPU (SameBoy's sm83_cpu.c). Each memory access first runs the rest of the machine for the T-cycles
// the CPU spent since the previous one (_pendingCycles); writes to a few PPU/APU registers are timed a cycle or
// two early or late to model SameBoy's access-conflict behaviour.
internal sealed partial class GameBoy
{
    private const int CarryFlag = 0x10, HalfCarryFlag = 0x20, SubtractFlag = 0x40, ZeroFlag = 0x80;

    private enum Conflict : byte
    {
        ReadOld, ReadNew, WriteCpu, StatCgb, PaletteCgb, LcdcCgb, LcdcCgbDouble, StatCgbDouble, Nr10CgbDouble,
        ScxDmgAndCgbDouble,
    }

    private static readonly Conflict[] CgbConflicts = BuildConflicts(false);
    private static readonly Conflict[] CgbDoubleConflicts = BuildConflicts(true);

    private static Conflict[] BuildConflicts(bool doubleSpeed)
    {
        var map = new Conflict[0x80];
        if (!doubleSpeed)
        {
            map[IoLcdc] = Conflict.LcdcCgb;
            map[IoIf] = Conflict.WriteCpu;
            map[IoLyc] = Conflict.WriteCpu;
            map[IoStat] = Conflict.StatCgb;
            map[IoBgp] = map[IoObp0] = map[IoObp1] = Conflict.PaletteCgb;
            map[IoWx] = Conflict.WriteCpu;
        }
        else
        {
            map[IoLcdc] = Conflict.LcdcCgbDouble;
            map[IoIf] = Conflict.WriteCpu;
            map[IoStat] = Conflict.StatCgbDouble;
            map[IoNr10] = Conflict.Nr10CgbDouble;
            map[IoScx] = Conflict.ScxDmgAndCgbDouble;
        }
        return map;
    }

    private byte A { get => (byte)(_r[AF] >> 8); set => _r[AF] = (ushort)((_r[AF] & 0x00FF) | value << 8); }
    private byte B { get => (byte)(_r[BC] >> 8); set => _r[BC] = (ushort)((_r[BC] & 0x00FF) | value << 8); }
    private byte C { get => (byte)_r[BC]; set => _r[BC] = (ushort)((_r[BC] & 0xFF00) | value); }
    private byte D { get => (byte)(_r[DE] >> 8); set => _r[DE] = (ushort)((_r[DE] & 0x00FF) | value << 8); }
    private byte E { get => (byte)_r[DE]; set => _r[DE] = (ushort)((_r[DE] & 0xFF00) | value); }
    private byte H { get => (byte)(_r[HL] >> 8); set => _r[HL] = (ushort)((_r[HL] & 0x00FF) | value << 8); }
    private byte L { get => (byte)_r[HL]; set => _r[HL] = (ushort)((_r[HL] & 0xFF00) | value); }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private byte CycleRead(ushort address)
    {
        if (_pendingCycles != 0) AdvanceCycles((byte)_pendingCycles);
        _addressBus = address;
        var value = ReadMemory(address);
        _pendingCycles = 4;
        return value;
    }

    // An interrupt dispatch pushing PC's low byte onto IF: returns IF's old value.
    private byte CycleWriteIf(byte value)
    {
        AdvanceCycles((byte)_pendingCycles);
        _addressBus = 0xFF00 + IoIf;
        var old = (byte)(_io[IoIf] & 0x1F);
        WriteMemory(0xFF00 + IoIf, value);
        _pendingCycles = 4;
        return old;
    }

    private void CycleWrite(ushort address, byte value)
    {
        var conflict = Conflict.ReadOld;
        if ((address & 0xFF80) == 0xFF00) conflict = (_doubleSpeed ? CgbDoubleConflicts : CgbConflicts)[address & 0x7F];
        switch (conflict)
        {
            case Conflict.ReadOld:
                AdvanceCycles((byte)_pendingCycles);
                WriteMemory(address, value);
                _pendingCycles = 4;
                break;

            case Conflict.ReadNew:
                AdvanceCycles((byte)(_pendingCycles - 1));
                WriteMemory(address, value);
                _pendingCycles = 5;
                break;

            case Conflict.WriteCpu:
                AdvanceCycles((byte)(_pendingCycles + 1));
                WriteMemory(address, value);
                _pendingCycles = 3;
                break;

            case Conflict.StatCgb:
            {
                // The LYC bit behaves differently.
                var old = _io[IoStat];
                AdvanceCycles((byte)_pendingCycles);
                WriteMemory(address, (byte)((old & 0x40) | (value & ~0x40)));
                AdvanceCycles(1);
                WriteMemory(address, value);
                _pendingCycles = 3;
                break;
            }

            case Conflict.StatCgbDouble:
            {
                var old = _io[IoStat];
                AdvanceCycles((byte)_pendingCycles);
                WriteMemory(address, (byte)((value & ~8) | (old & 8)));
                AdvanceCycles(1);
                WriteMemory(address, value);
                _pendingCycles = 3;
                break;
            }

            case Conflict.PaletteCgb:
                AdvanceCycles((byte)(_pendingCycles - 2));
                WriteMemory(address, value);
                _pendingCycles = 6;
                break;

            case Conflict.LcdcCgb:
            {
                var old = _io[IoLcdc];
                if ((~value & old & LcdcTileSel) != 0)
                {
                    AdvanceCycles((byte)_pendingCycles);
                    WriteMemory(address, value);
                    _tileSelGlitch = true;
                    AdvanceCycles(1);
                    _tileSelGlitch = false;
                    _pendingCycles = 3;
                }
                else
                {
                    AdvanceCycles((byte)_pendingCycles);
                    WriteMemory(address, value);
                    _pendingCycles = 4;
                }
                break;
            }

            case Conflict.LcdcCgbDouble:
            {
                var old = _io[IoLcdc];
                AdvanceCycles((byte)(_pendingCycles - 2));
                WriteMemory(address, (byte)((value & ~(LcdcBgEn | LcdcEnable)) | (old & (LcdcBgEn | LcdcEnable))));
                _tileSelGlitch = ((value ^ old) & LcdcTileSel) != 0;
                AdvanceCycles(2);
                _tileSelGlitch = false;
                WriteMemory(address, value);
                _pendingCycles = 4;
                break;
            }

            case Conflict.ScxDmgAndCgbDouble:
                AdvanceCycles((byte)(_pendingCycles - 2));
                WriteMemory(address, value);
                _pendingCycles = 6;
                break;

            case Conflict.Nr10CgbDouble:
                // SameBoy disables sweep stepping for a cycle here on CGB-C and older only.
                AdvanceCycles((byte)(_pendingCycles - 1));
                AdvanceCycles(1);
                WriteMemory(address, value);
                _pendingCycles = 4;
                break;
        }
        _addressBus = address;
    }

    private void CycleNoAccess() => _pendingCycles += 4;

    // An internal cycle that puts a register on the address bus (the DMG's OAM bug; the CGB has none).
    private void CycleOamBug(int register)
    {
        if (_pendingCycles != 0) AdvanceCycles((byte)_pendingCycles);
        _addressBus = _r[register];
        _pendingCycles = 4;
    }

    private void FlushPendingCycles()
    {
        if (_pendingCycles != 0) AdvanceCycles((byte)_pendingCycles);
        _pendingCycles = 0;
    }

    // ----- One step: an instruction, an interrupt dispatch, or a cycle of HALT/STOP (GB_cpu_run) -----

    private void CpuRun()
    {
        if (_stopped)
        {
            AdvanceCycles(4);
            if ((_io[IoJoyp] & 0xF) != 0xF)
            {
                LeaveStopMode();
                AdvanceCycles(8);
            }
            return;
        }

        var interruptQueue = (byte)(_ie & _io[IoIf] & 0x1F);
        if (_halted) AdvanceCycles(4);

        var effectiveIme = _ime;
        if (_imeToggle)
        {
            _ime = !_ime;
            _imeToggle = false;
        }

        if (_halted && !effectiveIme && interruptQueue != 0)
        {
            // Wake up from HALT without calling the interrupt.
            _halted = false;
            if (_hdmaOnHblank && (_io[IoStat] & 3) == 0 && _allowHdmaOnWake) _hdmaOn = true;
            _dmaCycles = 4;
            DmaRun();
            _speedSwitchHaltCountdown = 0;
        }
        else if (effectiveIme && interruptQueue != 0)
        {
            _halted = false;
            if (_hdmaOnHblank && (_io[IoStat] & 3) == 0 && _allowHdmaOnWake) _hdmaOn = true;
            _dmaCycles = 4;
            DmaRun();
            _speedSwitchHaltCountdown = 0;

            CycleRead(_r[PC]++);
            CycleOamBug(PC);
            _r[PC]--;
            CycleNoAccess();

            CycleWrite(--_r[SP], (byte)(_r[PC] >> 8));
            interruptQueue = _ie;
            if (_r[SP] == 0xFF00 + IoIf + 1)
            {
                _r[SP]--;
                interruptQueue &= CycleWriteIf((byte)_r[PC]);
            }
            else
            {
                CycleWrite(--_r[SP], (byte)_r[PC]);
                interruptQueue &= (byte)(_io[IoIf] & 0x1F);
            }

            if (interruptQueue != 0)
            {
                var bit = 0;
                while ((interruptQueue & 1) == 0)
                {
                    interruptQueue >>= 1;
                    bit++;
                }
                _pendingCycles -= 2;
                FlushPendingCycles();
                _pendingCycles = 2;
                _io[IoIf] &= (byte)~(1 << bit);
                _r[PC] = (ushort)(bit * 8 + 0x40);
            }
            else
            {
                _r[PC] = 0;
            }
            _ime = false;
        }
        else if (!_halted)
        {
            var opcode = CycleRead(_r[PC]++);
            if (_hdmaOn) HdmaRun();
            TraceHook?.Invoke(this, (ushort)(_r[PC] - 1));
            if (_haltBug)
            {
                _r[PC]--;
                _haltBug = false;
            }
            Execute(opcode);
        }

        FlushPendingCycles();
    }

    private void Execute(byte op)
    {
        switch (op)
        {
            case 0x00: break;
            case 0x01: case 0x11: case 0x21: case 0x31: LdRrD16(op); break;
            case 0x02: case 0x12: CycleWrite(_r[(op >> 4) + 1], A); break;
            case 0x03: case 0x13: case 0x23: case 0x33: { var id = (op >> 4) + 1; CycleOamBug(id); _r[id]++; break; }
            case 0x04: case 0x14: case 0x24: case 0x3C: IncHr(op); break;
            case 0x05: case 0x15: case 0x25: case 0x3D: DecHr(op); break;
            case 0x06: case 0x16: case 0x26: case 0x3E: { var id = ((op >> 4) + 1) & 3; _r[id] = (ushort)((_r[id] & 0xFF) | CycleRead(_r[PC]++) << 8); break; }
            case 0x07: Rlca(); break;
            case 0x08: LdDa16Sp(); break;
            case 0x09: case 0x19: case 0x29: case 0x39: AddHlRr(op); break;
            case 0x0A: case 0x1A: { var v = CycleRead(_r[(op >> 4) + 1]); _r[AF] = (ushort)((_r[AF] & 0xFF) | v << 8); break; }
            case 0x0B: case 0x1B: case 0x2B: case 0x3B: { var id = (op >> 4) + 1; CycleOamBug(id); _r[id]--; break; }
            case 0x0C: case 0x1C: case 0x2C: IncLr(op); break;
            case 0x0D: case 0x1D: case 0x2D: DecLr(op); break;
            case 0x0E: case 0x1E: case 0x2E: { var id = (op >> 4) + 1; _r[id] = (ushort)((_r[id] & 0xFF00) | CycleRead(_r[PC]++)); break; }
            case 0x0F: Rrca(); break;
            case 0x10: Stop(); break;
            case 0x17: Rla(); break;
            case 0x18: JrR8(); break;
            case 0x1F: Rra(); break;
            case 0x20: case 0x28: case 0x30: case 0x38: JrCcR8(op); break;
            case 0x22: CycleWrite(_r[HL]++, A); break;
            case 0x27: Daa(); break;
            case 0x2A: { var v = CycleRead(_r[HL]++); _r[AF] = (ushort)((_r[AF] & 0xFF) | v << 8); break; }
            case 0x2F: _r[AF] ^= 0xFF00; _r[AF] |= HalfCarryFlag | SubtractFlag; break;
            case 0x32: CycleWrite(_r[HL]--, A); break;
            case 0x34: IncDhl(); break;
            case 0x35: DecDhl(); break;
            case 0x36: { var v = CycleRead(_r[PC]++); CycleWrite(_r[HL], v); break; }
            case 0x37: _r[AF] |= CarryFlag; _r[AF] &= unchecked((ushort)~(HalfCarryFlag | SubtractFlag)); break;
            case 0x3A: { var v = CycleRead(_r[HL]--); _r[AF] = (ushort)((_r[AF] & 0xFF) | v << 8); break; }
            case 0x3F: _r[AF] ^= CarryFlag; _r[AF] &= unchecked((ushort)~(HalfCarryFlag | SubtractFlag)); break;
            case 0x76: Halt(); break;
            case >= 0x40 and <= 0x7F: LdRR(op); break;
            case >= 0x80 and <= 0x87: AddA(GetSrcValue(op)); break;
            case >= 0x88 and <= 0x8F: AdcA(GetSrcValue(op)); break;
            case >= 0x90 and <= 0x97: SubA(GetSrcValue(op)); break;
            case >= 0x98 and <= 0x9F: SbcA(GetSrcValue(op)); break;
            case >= 0xA0 and <= 0xA7: AndA(GetSrcValue(op)); break;
            case >= 0xA8 and <= 0xAF: XorA(GetSrcValue(op)); break;
            case >= 0xB0 and <= 0xB7: OrA(GetSrcValue(op)); break;
            case >= 0xB8 and <= 0xBF: CpA(GetSrcValue(op)); break;
            case 0xC0: case 0xC8: case 0xD0: case 0xD8: RetCc(op); break;
            case 0xC1: case 0xD1: case 0xE1: case 0xF1: PopRr(op); break;
            case 0xC2: case 0xCA: case 0xD2: case 0xDA: JpCcA16(op); break;
            case 0xC3: JpA16(); break;
            case 0xC4: case 0xCC: case 0xD4: case 0xDC: CallCcA16(op); break;
            case 0xC5: case 0xD5: case 0xE5: case 0xF5: PushRr(op); break;
            case 0xC6: AddA(CycleRead(_r[PC]++)); break;
            case 0xC7: case 0xCF: case 0xD7: case 0xDF: case 0xE7: case 0xEF: case 0xF7: case 0xFF: Rst(op); break;
            case 0xC9: Ret(); break;
            case 0xCB: CbPrefix(); break;
            case 0xCD: CallA16(); break;
            case 0xCE: AdcA(CycleRead(_r[PC]++)); break;
            case 0xD6: SubA(CycleRead(_r[PC]++)); break;
            case 0xD9: Ret(); _ime = true; break;
            case 0xDE: SbcA(CycleRead(_r[PC]++)); break;
            case 0xE0: { var t = CycleRead(_r[PC]++); CycleWrite((ushort)(0xFF00 + t), A); break; }
            case 0xE2: CycleWrite((ushort)(0xFF00 + C), A); break;
            case 0xE6: AndA(CycleRead(_r[PC]++)); break;
            case 0xE8: AddSpR8(); break;
            case 0xE9: _r[PC] = _r[HL]; break;
            case 0xEA: { var addr = (ushort)CycleRead(_r[PC]++); addr |= (ushort)(CycleRead(_r[PC]++) << 8); CycleWrite(addr, A); break; }
            case 0xEE: XorA(CycleRead(_r[PC]++)); break;
            case 0xF0:
            {
                _r[AF] &= 0xFF;
                var t = CycleRead(_r[PC]++);
                _r[AF] |= (ushort)(CycleRead((ushort)(0xFF00 + t)) << 8);
                break;
            }
            case 0xF2: _r[AF] &= 0xFF; _r[AF] |= (ushort)(CycleRead((ushort)(0xFF00 + C)) << 8); break;
            case 0xF3: _ime = false; break;  // DI is not delayed
            case 0xF6: OrA(CycleRead(_r[PC]++)); break;
            case 0xF8: LdHlSpR8(); break;
            case 0xF9: _r[SP] = _r[HL]; CycleOamBug(HL); break;
            case 0xFA:
            {
                _r[AF] &= 0xFF;
                var addr = (ushort)CycleRead(_r[PC]++);
                addr |= (ushort)(CycleRead(_r[PC]++) << 8);
                _r[AF] |= (ushort)(CycleRead(addr) << 8);
                break;
            }
            case 0xFB: if (!_ime && !_imeToggle) _imeToggle = true; break;  // EI takes effect after the next instruction
            case 0xFE: CpA(CycleRead(_r[PC]++)); break;
            default: Illegal(); break;  // D3 DB DD E3 E4 EB EC ED F4 FC FD
        }
    }

    private void Illegal()
    {
        _ie = 0;
        _halted = true;
    }

    private void EnterStopMode()
    {
        WriteMemory(0xFF00 + IoDiv, 0);
        if (!_ime) _divCycles = -4;  // the CPU-side DIV-reset signal is held
        _stopped = true;
        _allowHdmaOnWake = (_io[IoStat] & 3) != 0;
        _oamPpuBlocked = !_oamReadBlocked;
        _vramPpuBlocked = !_vramReadBlocked;
        _cgbPalettesPpuBlocked = !_cgbPalettesBlocked;
    }

    private void LeaveStopMode()
    {
        _stopped = false;
        if (_hdmaOnHblank && (_io[IoStat] & 3) == 0 && _allowHdmaOnWake) _hdmaOn = true;
        _dmaCycles = 4;
        DmaRun();
        _oamPpuBlocked = false;
        _vramPpuBlocked = false;
        _cgbPalettesPpuBlocked = false;
    }

    // STOP: enters STOP mode, or switches CPU speed when KEY1 asks for it.
    private void Stop()
    {
        FlushPendingCycles();
        ReadMemory(_r[PC]);
        var exitByJoyp = (_io[IoJoyp] & 0xF) != 0xF;
        var speedSwitch = (_io[IoKey1] & 0x1) != 0 && !exitByJoyp;
        var immediateExit = speedSwitch || exitByJoyp;
        var interruptPending = (_ie & _io[IoIf] & 0x1F) != 0;
        // When entering with IF&IE, the 2nd byte of STOP is actually executed.
        if (!exitByJoyp)
        {
            if (!immediateExit) DmaRun();
            EnterStopMode();
        }

        if (!interruptPending) CycleRead(_r[PC]++);

        if (speedSwitch)
        {
            FlushPendingCycles();
            // SameBoy doesn't emulate the PPU's "odd mode" and realigns instead.
            if ((_io[IoLcdc] & LcdcEnable) != 0 && _doubleSpeed && (_doubleSpeedAlignment & 7) != 0) _speedSwitchFreeze = 2;

            if (_doubleSpeed)
            {
                _doubleSpeed = false;
            }
            else
            {
                _speedSwitchCountdown = 6;
                _speedSwitchFreeze = 1;
            }

            if (!interruptPending)
            {
                _speedSwitchHaltCountdown = 0x20008;
                _speedSwitchFreeze = 5;
            }

            _io[IoKey1] = 0;
        }

        if (immediateExit)
        {
            LeaveStopMode();
            if (!interruptPending)
            {
                DmaRun();
                _halted = true;
                _allowHdmaOnWake = (_io[IoStat] & 3) != 0;
            }
            else
            {
                _speedSwitchHaltCountdown = 0;
            }
        }
    }

    private void Halt()
    {
        CycleRead(_r[PC]);
        _pendingCycles = 0;

        // The HALT bug also happens on a CGB.
        if ((_ie & _io[IoIf] & 0x1F) != 0)
        {
            if (_ime)
            {
                _halted = false;
                _r[PC]--;
            }
            else
            {
                _halted = false;
                _haltBug = true;
            }
        }
        else
        {
            _halted = true;
            _allowHdmaOnWake = (_io[IoStat] & 3) != 0;
        }
    }

    // ----- Loads and 16-bit arithmetic -----

    private void LdRrD16(byte op)
    {
        var id = (op >> 4) + 1;
        var value = (ushort)CycleRead(_r[PC]++);
        value |= (ushort)(CycleRead(_r[PC]++) << 8);
        _r[id] = value;
    }

    private void LdDa16Sp()
    {
        var addr = (ushort)CycleRead(_r[PC]++);
        addr |= (ushort)(CycleRead(_r[PC]++) << 8);
        CycleWrite(addr, (byte)_r[SP]);
        CycleWrite((ushort)(addr + 1), (byte)(_r[SP] >> 8));
    }

    private void AddHlRr(byte op)
    {
        var hl = _r[HL];
        CycleNoAccess();
        var rr = _r[(op >> 4) + 1];
        _r[HL] = (ushort)(hl + rr);
        _r[AF] &= unchecked((ushort)~(SubtractFlag | CarryFlag | HalfCarryFlag));
        if ((((hl & 0xFFF) + (rr & 0xFFF)) & 0x1000) != 0) _r[AF] |= HalfCarryFlag;
        if (((hl + rr) & 0x10000) != 0) _r[AF] |= CarryFlag;
    }

    private void IncHr(byte op)
    {
        var id = ((op >> 4) + 1) & 3;
        _r[id] += 0x100;
        _r[AF] &= unchecked((ushort)~(SubtractFlag | ZeroFlag | HalfCarryFlag));
        if ((_r[id] & 0x0F00) == 0) _r[AF] |= HalfCarryFlag;
        if ((_r[id] & 0xFF00) == 0) _r[AF] |= ZeroFlag;
    }

    private void DecHr(byte op)
    {
        var id = ((op >> 4) + 1) & 3;
        _r[id] -= 0x100;
        _r[AF] &= unchecked((ushort)~(ZeroFlag | HalfCarryFlag));
        _r[AF] |= SubtractFlag;
        if ((_r[id] & 0x0F00) == 0xF00) _r[AF] |= HalfCarryFlag;
        if ((_r[id] & 0xFF00) == 0) _r[AF] |= ZeroFlag;
    }

    private void IncLr(byte op)
    {
        var id = (op >> 4) + 1;
        var value = (byte)((_r[id] & 0xFF) + 1);
        _r[id] = (ushort)((_r[id] & 0xFF00) | value);
        _r[AF] &= unchecked((ushort)~(SubtractFlag | ZeroFlag | HalfCarryFlag));
        if ((_r[id] & 0x0F) == 0) _r[AF] |= HalfCarryFlag;
        if ((_r[id] & 0xFF) == 0) _r[AF] |= ZeroFlag;
    }

    private void DecLr(byte op)
    {
        var id = (op >> 4) + 1;
        var value = (byte)((_r[id] & 0xFF) - 1);
        _r[id] = (ushort)((_r[id] & 0xFF00) | value);
        _r[AF] &= unchecked((ushort)~(ZeroFlag | HalfCarryFlag));
        _r[AF] |= SubtractFlag;
        if ((_r[id] & 0x0F) == 0xF) _r[AF] |= HalfCarryFlag;
        if ((_r[id] & 0xFF) == 0) _r[AF] |= ZeroFlag;
    }

    private void IncDhl()
    {
        var value = (byte)(CycleRead(_r[HL]) + 1);
        CycleWrite(_r[HL], value);
        _r[AF] &= unchecked((ushort)~(SubtractFlag | ZeroFlag | HalfCarryFlag));
        if ((value & 0x0F) == 0) _r[AF] |= HalfCarryFlag;
        if (value == 0) _r[AF] |= ZeroFlag;
    }

    private void DecDhl()
    {
        var value = (byte)(CycleRead(_r[HL]) - 1);
        CycleWrite(_r[HL], value);
        _r[AF] &= unchecked((ushort)~(ZeroFlag | HalfCarryFlag));
        _r[AF] |= SubtractFlag;
        if ((value & 0x0F) == 0x0F) _r[AF] |= HalfCarryFlag;
        if (value == 0) _r[AF] |= ZeroFlag;
    }

    // LD r, r' / LD r, [HL] / LD [HL], r ($40-$7F except $76).
    private void LdRR(byte op)
    {
        var dst = (op >> 3) & 7;
        var src = op & 7;
        if (dst == 6)
        {
            CycleWrite(_r[HL], GetReg8(src));
            return;
        }
        if (src == 6)
        {
            SetReg8(dst, CycleRead(_r[HL]));
            return;
        }
        if (dst != src) SetReg8(dst, GetReg8(src));
    }

    // B C D E H L - A (index 6, [HL], is handled by the callers).
    private byte GetReg8(int index) => index switch
    {
        0 => B, 1 => C, 2 => D, 3 => E, 4 => H, 5 => L, _ => A,
    };

    private void SetReg8(int index, byte value)
    {
        switch (index)
        {
            case 0: B = value; break;
            case 1: C = value; break;
            case 2: D = value; break;
            case 3: E = value; break;
            case 4: H = value; break;
            case 5: L = value; break;
            default: A = value; break;
        }
    }

    private void Rlca()
    {
        var carry = (_r[AF] & 0x8000) != 0;
        _r[AF] = (ushort)((_r[AF] & 0xFF00) << 1);
        if (carry) _r[AF] |= CarryFlag | 0x0100;
    }

    private void Rla()
    {
        var bit7 = (_r[AF] & 0x8000) != 0;
        var carry = (_r[AF] & CarryFlag) != 0;
        _r[AF] = (ushort)((_r[AF] & 0xFF00) << 1);
        if (carry) _r[AF] |= 0x0100;
        if (bit7) _r[AF] |= CarryFlag;
    }

    private void Rrca()
    {
        var carry = (_r[AF] & 0x100) != 0;
        _r[AF] = (ushort)((_r[AF] >> 1) & 0xFF00);
        if (carry) _r[AF] |= CarryFlag | 0x8000;
    }

    private void Rra()
    {
        var bit1 = (_r[AF] & 0x0100) != 0;
        var carry = (_r[AF] & CarryFlag) != 0;
        _r[AF] = (ushort)((_r[AF] >> 1) & 0xFF00);
        if (carry) _r[AF] |= 0x8000;
        if (bit1) _r[AF] |= CarryFlag;
    }

    private void Daa()
    {
        int result = _r[AF] >> 8;
        _r[AF] &= unchecked((ushort)~(0xFF00 | ZeroFlag));
        if ((_r[AF] & SubtractFlag) != 0)
        {
            if ((_r[AF] & HalfCarryFlag) != 0) result = (result - 0x06) & 0xFF;
            if ((_r[AF] & CarryFlag) != 0) result -= 0x60;
        }
        else
        {
            if ((_r[AF] & HalfCarryFlag) != 0 || (result & 0x0F) > 0x09) result += 0x06;
            if ((_r[AF] & CarryFlag) != 0 || result > 0x9F) result += 0x60;
        }
        // result is an int16_t in SameBoy; only its low 9 bits matter below.
        result = (short)result;
        if ((result & 0xFF) == 0) _r[AF] |= ZeroFlag;
        if ((result & 0x100) == 0x100) _r[AF] |= CarryFlag;
        _r[AF] &= unchecked((ushort)~HalfCarryFlag);
        _r[AF] |= (ushort)(result << 8);
    }

    // ----- Jumps and calls -----

    private bool ConditionCode(byte op) => ((op >> 3) & 3) switch
    {
        0 => (_r[AF] & ZeroFlag) == 0,
        1 => (_r[AF] & ZeroFlag) != 0,
        2 => (_r[AF] & CarryFlag) == 0,
        _ => (_r[AF] & CarryFlag) != 0,
    };

    private void JrR8()
    {
        var offset = (sbyte)CycleRead(_r[PC]++);
        CycleOamBug(PC);
        _r[PC] = (ushort)(_r[PC] + offset);
    }

    private void JrCcR8(byte op)
    {
        var offset = (sbyte)CycleRead(_r[PC]++);
        if (ConditionCode(op))
        {
            _r[PC] = (ushort)(_r[PC] + offset);
            CycleOamBug(PC);
        }
    }

    private void JpCcA16(byte op)
    {
        var addr = (ushort)CycleRead(_r[PC]++);
        addr |= (ushort)(CycleRead(_r[PC]++) << 8);
        if (ConditionCode(op))
        {
            CycleNoAccess();
            _r[PC] = addr;
        }
    }

    private void JpA16()
    {
        var addr = (ushort)CycleRead(_r[PC]);
        addr |= (ushort)(CycleRead((ushort)(_r[PC] + 1)) << 8);
        CycleNoAccess();
        _r[PC] = addr;
    }

    private void CallCcA16(byte op)
    {
        var addr = (ushort)CycleRead(_r[PC]++);
        addr |= (ushort)(CycleRead(_r[PC]++) << 8);
        if (ConditionCode(op))
        {
            CycleOamBug(SP);
            CycleWrite(--_r[SP], (byte)(_r[PC] >> 8));
            CycleWrite(--_r[SP], (byte)_r[PC]);
            _r[PC] = addr;
        }
    }

    private void CallA16()
    {
        var addr = (ushort)CycleRead(_r[PC]++);
        addr |= (ushort)(CycleRead(_r[PC]++) << 8);
        CycleOamBug(SP);
        CycleWrite(--_r[SP], (byte)(_r[PC] >> 8));
        CycleWrite(--_r[SP], (byte)_r[PC]);
        _r[PC] = addr;
    }

    private void Rst(byte op)
    {
        CycleOamBug(SP);
        CycleWrite(--_r[SP], (byte)(_r[PC] >> 8));
        CycleWrite(--_r[SP], (byte)_r[PC]);
        _r[PC] = (ushort)(op ^ 0xC7);
    }

    private void Ret()
    {
        _r[PC] = CycleRead(_r[SP]++);
        _r[PC] |= (ushort)(CycleRead(_r[SP]++) << 8);
        CycleNoAccess();
    }

    private void RetCc(byte op)
    {
        if (ConditionCode(op))
        {
            CycleNoAccess();
            Ret();
        }
        else
        {
            CycleNoAccess();
        }
    }

    private void PopRr(byte op)
    {
        var id = ((op >> 4) + 1) & 3;
        _r[id] = CycleRead(_r[SP]++);
        _r[id] |= (ushort)(CycleRead(_r[SP]++) << 8);
        _r[AF] &= 0xFFF0;  // F's low nibble always reads 0
    }

    private void PushRr(byte op)
    {
        CycleOamBug(SP);
        var id = ((op >> 4) + 1) & 3;
        CycleWrite(--_r[SP], (byte)(_r[id] >> 8));
        CycleWrite(--_r[SP], (byte)_r[id]);
    }

    private void AddSpR8()
    {
        var sp = _r[SP];
        var offset = (short)(sbyte)CycleRead(_r[PC]++);
        CycleNoAccess();
        CycleNoAccess();
        _r[SP] = (ushort)(_r[SP] + offset);
        _r[AF] &= 0xFF00;
        if ((sp & 0xF) + (offset & 0xF) > 0xF) _r[AF] |= HalfCarryFlag;
        if ((sp & 0xFF) + (offset & 0xFF) > 0xFF) _r[AF] |= CarryFlag;
    }

    private void LdHlSpR8()
    {
        _r[AF] &= 0xFF00;
        var offset = (short)(sbyte)CycleRead(_r[PC]++);
        CycleNoAccess();
        _r[HL] = (ushort)(_r[SP] + offset);
        if ((_r[SP] & 0xF) + (offset & 0xF) > 0xF) _r[AF] |= HalfCarryFlag;
        if ((_r[SP] & 0xFF) + (offset & 0xFF) > 0xFF) _r[AF] |= CarryFlag;
    }

    // ----- 8-bit arithmetic -----

    private byte GetSrcValue(byte op)
    {
        var id = ((op >> 1) + 1) & 3;
        var low = (op & 1) != 0;
        if (id == AF) return low ? (byte)(_r[AF] >> 8) : CycleRead(_r[HL]);
        return low ? (byte)_r[id] : (byte)(_r[id] >> 8);
    }

    private void SetSrcValue(byte op, byte value)
    {
        var id = ((op >> 1) + 1) & 3;
        var low = (op & 1) != 0;
        if (id == AF)
        {
            if (low) _r[AF] = (ushort)((_r[AF] & 0xFF) | value << 8);
            else CycleWrite(_r[HL], value);
        }
        else if (low)
        {
            _r[id] = (ushort)((_r[id] & 0xFF00) | value);
        }
        else
        {
            _r[id] = (ushort)((_r[id] & 0xFF) | value << 8);
        }
    }

    private void AddA(byte value)
    {
        var a = (byte)(_r[AF] >> 8);
        _r[AF] = (ushort)((a + value) << 8);
        if ((byte)(a + value) == 0) _r[AF] |= ZeroFlag;
        if ((a & 0xF) + (value & 0xF) > 0x0F) _r[AF] |= HalfCarryFlag;
        if (a + value > 0xFF) _r[AF] |= CarryFlag;
    }

    private void AdcA(byte value)
    {
        var a = (byte)(_r[AF] >> 8);
        var carry = (_r[AF] & CarryFlag) != 0 ? 1 : 0;
        _r[AF] = (ushort)((a + value + carry) << 8);
        if ((byte)(a + value + carry) == 0) _r[AF] |= ZeroFlag;
        if ((a & 0xF) + (value & 0xF) + carry > 0x0F) _r[AF] |= HalfCarryFlag;
        if (a + value + carry > 0xFF) _r[AF] |= CarryFlag;
    }

    private void SubA(byte value)
    {
        var a = (byte)(_r[AF] >> 8);
        _r[AF] = (ushort)(((a - value) << 8) | SubtractFlag);
        if (a == value) _r[AF] |= ZeroFlag;
        if ((a & 0xF) < (value & 0xF)) _r[AF] |= HalfCarryFlag;
        if (a < value) _r[AF] |= CarryFlag;
    }

    private void SbcA(byte value)
    {
        var a = (byte)(_r[AF] >> 8);
        var carry = (_r[AF] & CarryFlag) != 0 ? 1 : 0;
        _r[AF] = (ushort)(((byte)(a - value - carry) << 8) | SubtractFlag);
        if ((byte)(a - value - carry) == 0) _r[AF] |= ZeroFlag;
        if ((a & 0xF) < (value & 0xF) + carry) _r[AF] |= HalfCarryFlag;
        if ((uint)a - value - (uint)carry > 0xFF) _r[AF] |= CarryFlag;
    }

    private void AndA(byte value)
    {
        var a = (byte)(_r[AF] >> 8);
        _r[AF] = (ushort)(((a & value) << 8) | HalfCarryFlag);
        if ((a & value) == 0) _r[AF] |= ZeroFlag;
    }

    private void XorA(byte value)
    {
        var a = (byte)(_r[AF] >> 8);
        _r[AF] = (ushort)((a ^ value) << 8);
        if ((a ^ value) == 0) _r[AF] |= ZeroFlag;
    }

    private void OrA(byte value)
    {
        var a = (byte)(_r[AF] >> 8);
        _r[AF] = (ushort)((a | value) << 8);
        if ((a | value) == 0) _r[AF] |= ZeroFlag;
    }

    private void CpA(byte value)
    {
        var a = (byte)(_r[AF] >> 8);
        _r[AF] &= 0xFF00;
        _r[AF] |= SubtractFlag;
        if (a == value) _r[AF] |= ZeroFlag;
        if ((a & 0xF) < (value & 0xF)) _r[AF] |= HalfCarryFlag;
        if (a < value) _r[AF] |= CarryFlag;
    }

    // ----- CB-prefixed rotates, shifts and bit operations -----

    private void CbPrefix()
    {
        var op = CycleRead(_r[PC]++);
        switch (op >> 3)
        {
            case 0: // RLC
            {
                var value = GetSrcValue(op);
                var carry = (value & 0x80) != 0;
                _r[AF] &= 0xFF00;
                SetSrcValue(op, (byte)((value << 1) | (carry ? 1 : 0)));
                if (carry) _r[AF] |= CarryFlag;
                if (value == 0) _r[AF] |= ZeroFlag;
                break;
            }
            case 1: // RRC
            {
                var value = GetSrcValue(op);
                var carry = (value & 0x01) != 0;
                _r[AF] &= 0xFF00;
                value = (byte)((value >> 1) | (carry ? 0x80 : 0));
                SetSrcValue(op, value);
                if (carry) _r[AF] |= CarryFlag;
                if (value == 0) _r[AF] |= ZeroFlag;
                break;
            }
            case 2: // RL
            {
                var value = GetSrcValue(op);
                var carry = (_r[AF] & CarryFlag) != 0;
                var bit7 = (value & 0x80) != 0;
                _r[AF] &= 0xFF00;
                value = (byte)((value << 1) | (carry ? 1 : 0));
                SetSrcValue(op, value);
                if (bit7) _r[AF] |= CarryFlag;
                if (value == 0) _r[AF] |= ZeroFlag;
                break;
            }
            case 3: // RR
            {
                var value = GetSrcValue(op);
                var carry = (_r[AF] & CarryFlag) != 0;
                var bit1 = (value & 0x1) != 0;
                _r[AF] &= 0xFF00;
                value = (byte)((value >> 1) | (carry ? 0x80 : 0));
                SetSrcValue(op, value);
                if (bit1) _r[AF] |= CarryFlag;
                if (value == 0) _r[AF] |= ZeroFlag;
                break;
            }
            case 4: // SLA
            {
                var value = GetSrcValue(op);
                var carry = (value & 0x80) != 0;
                _r[AF] &= 0xFF00;
                SetSrcValue(op, (byte)(value << 1));
                if (carry) _r[AF] |= CarryFlag;
                if ((value & 0x7F) == 0) _r[AF] |= ZeroFlag;
                break;
            }
            case 5: // SRA
            {
                var value = GetSrcValue(op);
                var bit7 = value & 0x80;
                _r[AF] &= 0xFF00;
                if ((value & 1) != 0) _r[AF] |= CarryFlag;
                value = (byte)((value >> 1) | bit7);
                SetSrcValue(op, value);
                if (value == 0) _r[AF] |= ZeroFlag;
                break;
            }
            case 6: // SWAP
            {
                var value = GetSrcValue(op);
                _r[AF] &= 0xFF00;
                SetSrcValue(op, (byte)((value >> 4) | (value << 4)));
                if (value == 0) _r[AF] |= ZeroFlag;
                break;
            }
            case 7: // SRL
            {
                var value = GetSrcValue(op);
                _r[AF] &= 0xFF00;
                SetSrcValue(op, (byte)(value >> 1));
                if ((value & 1) != 0) _r[AF] |= CarryFlag;
                if ((value >> 1) == 0) _r[AF] |= ZeroFlag;
                break;
            }
            default: // BIT, RES, SET
            {
                var value = GetSrcValue(op);
                var bit = (byte)(1 << ((op >> 3) & 7));
                if ((op & 0xC0) == 0x40)
                {
                    _r[AF] &= 0xFF00 | CarryFlag;
                    _r[AF] |= HalfCarryFlag;
                    if ((bit & value) == 0) _r[AF] |= ZeroFlag;
                }
                else if ((op & 0xC0) == 0x80)
                {
                    SetSrcValue(op, (byte)(value & ~bit));
                }
                else
                {
                    SetSrcValue(op, (byte)(value | bit));
                }
                break;
            }
        }
    }
}
