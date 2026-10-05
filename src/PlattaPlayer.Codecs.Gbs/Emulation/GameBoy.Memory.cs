namespace PlattaPlayer.Codecs.Gbs.Emulation;

// The memory map and I/O registers (SameBoy's memory.c and mbc.c), including OAM DMA and HDMA. The cartridge is
// an MBC3 without RAM: $A000-$BFFF reads the open data bus.
internal sealed partial class GameBoy
{
    private enum Bus { Main, Ram, Vram }

    // ----- DMA state -----
    private bool _hdmaOn;
    private bool _hdmaOnHblank;
    private byte _hdmaStepsLeft;
    private ushort _hdmaCurrentSrc, _hdmaCurrentDest;
    private byte _dmaCurrentDest;
    private ushort _dmaCurrentSrc;
    private ushort _dmaCycles;
    private sbyte _dmaCyclesModulo;
    private bool _dmaPpuVramConflict;
    private ushort _dmaPpuVramConflictAddr;
    private bool _allowHdmaOnWake;
    private bool _dmaRestarting;

    // Transient flags (not saved by SameBoy, but meaningful only within one access).
    private bool _inDmaRead;
    private bool _hdmaInProgress;
    private bool _returnedOpenBus;
    private ushort _addrForHdmaConflict;
    private bool _duringDivWrite;

    private void ResetDma()
    {
        _hdmaOn = _hdmaOnHblank = false;
        _hdmaStepsLeft = 0;
        _hdmaCurrentSrc = _hdmaCurrentDest = 0;
        _dmaCurrentDest = 0;
        _dmaCurrentSrc = 0;
        _dmaCycles = 0;
        _dmaCyclesModulo = 0;
        _dmaPpuVramConflict = false;
        _dmaPpuVramConflictAddr = 0;
        _allowHdmaOnWake = false;
        _dmaRestarting = false;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static Bus BusFor(int address) => address switch
    {
        < 0x8000 => Bus.Main,
        < 0xA000 => Bus.Vram,
        < 0xC000 => Bus.Main,
        _ => Bus.Ram,
    };

    private bool DmaActive => _dmaCurrentDest != 0xA1;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private bool IsAddressInDmaUse(ushort address) => DmaActive && IsAddressInDmaUseSlow(address);

    private bool IsAddressInDmaUseSlow(ushort address)
    {
        if (address >= 0xFE00 || _hdmaInProgress) return false;
        if (_dmaCurrentDest == 0xFF || _dmaCurrentDest == 0x0) return false;  // warm up
        if (_dmaCurrentSrc == address) return false;
        if (_dmaCurrentSrc >= 0xE000 && (_dmaCurrentSrc & ~0x2000) == address) return false;
        if (address >= 0xC000) return BusFor(_dmaCurrentSrc) != Bus.Vram;
        if (_dmaCurrentSrc >= 0xE000) return BusFor(address) != Bus.Vram;
        return BusFor(address) == BusFor(_dmaCurrentSrc);
    }

    // ----- Reads -----

    private byte ReadMemory(ushort address)
    {
        if (IsAddressInDmaUse(address))
        {
            if (BusFor(address) == Bus.Main && _dmaCurrentSrc >= 0xE000) return 0xFF;
            if (BusFor(_dmaCurrentSrc) != Bus.Ram && address >= 0xC000)
                address = (ushort)(((_dmaCurrentSrc - 1) & 0x1000) | (address & 0xFFF) | 0xC000);
            else if (_dmaCurrentSrc >= 0xE000 && address >= 0xC000)
                address = (ushort)(((_dmaCurrentSrc - 1) & 0x1000) | (address & 0xFFF) | 0xC000);
            else
                address = (ushort)(_dmaCurrentSrc - 1);
        }

        byte data;
        switch (address >> 12)
        {
            case 0: case 1: case 2: case 3: data = _rom[(address & 0x3FFF) & (_romSize - 1)]; break;
            case 4: case 5: case 6: case 7: data = _rom[((address & 0x3FFF) + _mbcRomBank * 0x4000) & (_romSize - 1)]; break;
            case 8: case 9: data = ReadVram(address); break;
            case 0xA: case 0xB:
                // No cartridge RAM (enabled or not): the open bus.
                _returnedOpenBus = true;
                data = _dataBus;
                break;
            case 0xC: case 0xE: data = _ram[address & 0x0FFF]; break;
            case 0xD: data = _ram[(address & 0x0FFF) + _cgbRamBank * 0x1000]; break;
            default: data = ReadHighMemory(address); break;
        }

        if (BusFor(address) == Bus.Main && address < 0xFF00)
        {
            if (_returnedOpenBus)
            {
                _returnedOpenBus = false;
            }
            else
            {
                _dataBus = data;
                _dataBusDecayCountdown = DataBusDecay;
            }
        }
        return data;
    }

    private byte ReadVram(ushort address)
    {
        if (!DmaActive)
        {
            DisplaySync();
        }
        else if ((_dmaCurrentDest & 0xE000) == 0x8000)
        {
            return _cpuVramBus = _vram[(address & 0x1FFF) + (_cgbVramBank ? 0x2000 : 0)];
        }

        if (_vramReadBlocked && !_inDmaRead) return 0xFF;
        if (_displayState == 22 && !_doubleSpeed)
        {
            if ((address & 0x1000) != 0)
            {
                address = _lastTileIndexAddress;
            }
            else if ((_lastTileDataAddress & 0x1000) != 0)
            {
                var ret = _cpuVramBus;
                _cpuVramBus = _vram[(address & 0x1FFF) + (_cgbVramBank ? 0x2000 : 0)];
                return ret;
            }
            else
            {
                address = _lastTileDataAddress;
            }
        }
        return _cpuVramBus = _vram[(address & 0x1FFF) + (_cgbVramBank ? 0x2000 : 0)];
    }

    // OAM as the CPU and DMA see it; on a CGB-E the unused area $FEA0-$FEFF reads a pattern of its address.
    private byte ReadOam(byte address) => address < 0xA0 ? _oam[address] : (byte)((address & 0xF0) | (address >> 4));

    private void SyncPpuIfNeeded(int register)
    {
        switch (register)
        {
            case IoIf: case IoLcdc: case IoStat: case IoScy: case IoScx: case IoLy: case IoLyc: case IoDma: case IoBgp:
            case IoObp0: case IoObp1: case IoWy: case IoWx: case IoHdma1: case IoHdma2: case IoHdma3: case IoHdma4:
            case IoHdma5: case IoBgpi: case IoBgpd: case IoObpi: case IoObpd: case IoOpri:
                DisplaySync();
                break;
        }
    }

    private byte ReadHighMemory(ushort address)
    {
        if (address < 0xFE00) return _ram[(address & 0x0FFF) + _cgbRamBank * 0x1000];

        if (address < 0xFF00)
        {
            DisplaySync();
            if (DmaActive && (_dmaCurrentDest != 0 || _dmaRestarting)) return 0xFF;
            if (_oamReadBlocked) return 0xFF;
            return ReadOam((byte)address);
        }

        if (address < 0xFF80)
        {
            var register = address & 0xFF;
            SyncPpuIfNeeded(register);
            switch (register)
            {
                case IoIf: return (byte)(_io[IoIf] | 0xE0);
                case IoTac: return (byte)(_io[IoTac] | 0xF8);
                case IoStat: return (byte)(_io[IoStat] | 0x80);
                case IoOpri: return (byte)(_io[IoOpri] | 0xFE);
                case IoPcm12:
                    ApuRun(true);
                    return (byte)((_apu.IsActive[1] ? _apu.Samples[1] << 4 : 0) | (_apu.IsActive[0] ? _apu.Samples[0] : 0));
                case IoPcm34:
                    ApuRun(true);
                    return (byte)((_apu.IsActive[3] ? _apu.Samples[3] << 4 : 0) | (_apu.IsActive[2] ? _apu.Samples[2] : 0));
                case IoJoyp: case IoTma: case IoLcdc: case IoScy: case IoScx: case IoLy: case IoLyc: case IoBgp:
                case IoObp0: case IoObp1: case IoWy: case IoWx: case IoSc: case IoSb: case IoDma:
                    return _io[register];
                case IoTima:
                    return _timaReloadState == TimaReloading ? (byte)0 : _io[IoTima];
                case IoDiv:
                    return (byte)(_divCounter >> 8);
                case IoHdma5:
                    return (byte)((_hdmaOn || _hdmaOnHblank ? 0 : 0x80) | ((_hdmaStepsLeft - 1) & 0x7F));
                case IoSvbk:
                    return _io[IoSvbk];
                case IoVbk:
                    return (byte)((_cgbVramBank ? 1 : 0) | 0xFE);
                case IoBgpi: case IoObpi:
                    return (byte)(_io[register] | 0x40);
                case IoBgpd: case IoObpd:
                {
                    if (_cgbPalettesBlocked) return 0xFF;
                    var index = _io[register - 1] & 0x3F;
                    return register == IoBgpd ? _bgPalettes[index] : _objPalettes[index];
                }
                case IoKey1:
                    return (byte)((_io[IoKey1] & 0x7F) | (_doubleSpeed ? 0xFE : 0x7E));
                case IoBank:
                    return (byte)(0xFE | (_bootRomFinished ? 1 : 0));
                case IoRp:
                {
                    // You read your own IR LED if it's on.
                    var ret = (byte)((_io[IoRp] & 0xC1) | 0x2E);
                    if ((_io[IoRp] & 0xC0) == 0xC0 && _effectiveIrInput) ret &= unchecked((byte)~2);
                    return ret;
                }
                case IoPswx: case IoPswy: case IoPsw:
                    return _io[register];
                case IoPgb:
                    return (byte)(_io[register] | 0x8F);
                default:
                    if (register is >= IoNr10 and <= IoWavEnd) return ApuRead(register);
                    return 0xFF;
            }
        }

        if (address == 0xFFFF) return _ie;
        return _hram[address - 0xFF80];
    }

    // ----- Writes -----

    private void WriteMemory(ushort address, byte value)
    {
        if (BusFor(address) == Bus.Main && address < 0xFF00)
        {
            _dataBus = value;
            _dataBusDecayCountdown = DataBusDecay;
        }
        if (WriteHook is not null && (address >= 0xFF00 || address < 0x8000)) WriteHook(this, address, value);

        if (IsAddressInDmaUse(address))
        {
            if (BusFor(address) == Bus.Main && _dmaCurrentSrc >= 0xE000) return;

            if ((_dmaCurrentSrc < 0xC000 || _dmaCurrentSrc >= 0xE000) && address >= 0xC000)
            {
                address = (ushort)(((_dmaCurrentSrc - 1) & 0x1000) | (address & 0xFFF) | 0xC000);
            }
            else
            {
                // The write lands where the DMA is reading; the byte the DMA is copying is lost below cartridge RAM.
                address = (ushort)(_dmaCurrentSrc - 1);
                if (address >= 0xA000) return;
                _oam[(byte)(_dmaCurrentDest - 1)] = 0;
            }
        }

        switch (address >> 12)
        {
            case 0: case 1: case 2: case 3: case 4: case 5: case 6: case 7: WriteMbc(address, value); break;
            case 8: case 9:
                DisplaySync();
                if (!_vramWriteBlocked) _vram[(address & 0x1FFF) + (_cgbVramBank ? 0x2000 : 0)] = value;
                break;
            case 0xA: case 0xB: break;  // no cartridge RAM
            case 0xC: case 0xE: _ram[address & 0x0FFF] = value; break;
            case 0xD: _ram[(address & 0x0FFF) + _cgbRamBank * 0x1000] = value; break;
            default: WriteHighMemory(address, value); break;
        }
    }

    private void WriteMbc(ushort address, byte value)
    {
        switch (address & 0xF000)
        {
            case 0x0000: case 0x1000: _mbcRamEnable = (value & 0xF) == 0xA; break;
            case 0x2000: case 0x3000: _mbc3RomBank = value; break;
            case 0x4000: case 0x5000:
                _mbc3RamBank = (byte)(value & 7);
                _mbc3RtcMapped = (value & 8) != 0;
                break;
        }
        // GB_update_mbc_mappings for an MBC3 (not an MBC30: 7 bank bits, bank 0 maps as 1).
        _mbcRomBank = (ushort)(_mbc3RomBank & 0x7F);
        _mbcRamBank = _mbc3RamBank;
        if (_mbcRomBank == 0) _mbcRomBank = 1;
    }

    private void WriteOam(byte address, byte value)
    {
        if (address < 0xA0) _oam[address] = value;  // the CGB-E ignores writes to $FEA0-$FEFF
    }

    private void WriteHighMemory(ushort address, byte value)
    {
        if (address < 0xFE00)
        {
            _ram[(address & 0x0FFF) + _cgbRamBank * 0x1000] = value;
            return;
        }

        if (address < 0xFF00)
        {
            DisplaySync();
            if (_oamWriteBlocked) return;
            if (DmaActive) return;
            WriteOam((byte)address, value);
            return;
        }

        if (address < 0xFF80)
        {
            var register = address & 0xFF;
            SyncPpuIfNeeded(register);
            switch (register)
            {
                case IoWx:
                    _io[IoWx] = value;
                    UpdateWxGlitch();
                    break;

                case IoIf: case IoScx: case IoScy: case IoBgp: case IoObp0: case IoObp1: case IoSb: case IoPswx:
                case IoPswy: case IoPsw: case IoPgb:
                    _io[register] = value;
                    return;

                case IoOpri:
                    // Only the boot ROM can change object priority mode; afterwards the register just stores.
                    if ((_io[IoKey0] & 8) != 0)
                    {
                        _io[register] = value;
                        _objectPriority = (byte)((value & 1) != 0 ? ObjectPriorityX : ObjectPriorityIndex);
                    }
                    else
                    {
                        _io[register] = value;
                    }
                    return;

                case IoWy:
                    _io[IoWy] = value;
                    _wyCheckScheduled = true;
                    return;

                case IoLyc:
                    if (_displayState == 29)
                    {
                        _lyForComparison = 153;
                        StatUpdate();
                        _lyForComparison = 0;
                    }
                    _io[IoLyc] = value;
                    // The states where LY changes call StatUpdate themselves, for T-cycle accurate LYC writes.
                    if (_displayState != 35 && _displayState != 26 && _displayState != 15 && _displayState != 16)
                    {
                        if (_displayState == 14)
                        {
                            _lyForComparison = 153;
                            StatUpdate();
                            _lyForComparison = 0xFFFF;
                        }
                        else
                        {
                            StatUpdate();
                        }
                    }
                    return;

                case IoTima:
                    if (_timaReloadState != TimaReloaded) _io[IoTima] = value;
                    return;

                case IoTma:
                    _io[IoTma] = value;
                    if (_timaReloadState != TimaRunning) _io[IoTima] = value;
                    return;

                case IoTac:
                    EmulateTimerGlitch(_io[IoTac], value);
                    _io[IoTac] = value;
                    return;

                case IoLcdc:
                    if ((value & LcdcEnable) != 0 && (_io[IoLcdc] & LcdcEnable) == 0)
                    {
                        // LCD turned on.
                        if (!_lcdDisabledOutsideOfVblank && _cyclesSinceVblankCallback > 10 * 456) DisplayVblank();
                        _displayCycles = 0;
                        _displayState = 0;
                        _doubleSpeedAlignment = 0;
                        _cyclesForLine = 0;
                    }
                    else if ((value & LcdcEnable) == 0 && (_io[IoLcdc] & LcdcEnable) != 0)
                    {
                        _doubleSpeedAlignment = 0;
                        LcdOff();
                    }
                    _io[IoLcdc] = value;
                    _wyCheckScheduled = true;
                    return;

                case IoStat:
                    _io[IoStat] &= 7;
                    _io[IoStat] |= (byte)(value & ~7);
                    _io[IoStat] |= 0x80;
                    if (_doubleSpeed && _displayState == 8 && _oamSearchIndex == 0 && _displayCycles == 0 && (value & 0x20) != 0)
                    {
                        _modeForInterrupt = 2;
                        StatUpdate();
                        _modeForInterrupt = 0xFF;
                    }
                    else
                    {
                        StatUpdate();
                    }
                    return;

                case IoDiv:
                    _duringDivWrite = true;
                    SetInternalDivCounter(0);
                    _duringDivWrite = false;
                    _divState = 0;
                    _divCycles = 0;
                    return;

                case IoJoyp:
                    // No buttons are pressed: the selected lines all read 1.
                    if ((_io[IoJoyp] & 0x30) != (value & 0x30)) _io[IoJoyp] = (byte)((value & 0xF0) | 0xCF);
                    return;

                case IoBank:
                    _bootRomFinished |= (value & 1) != 0;
                    return;

                case IoKey0:
                    return;  // writable only while the boot ROM runs

                case IoDma:
                    _dmaRestarting = _dmaCurrentDest != 0xA1 && _dmaCurrentDest != 0xA0;
                    _dmaCycles = 0;
                    _dmaCyclesModulo = 2;
                    _dmaCurrentDest = 0xFF;
                    _dmaCurrentSrc = (ushort)(value << 8);
                    _io[IoDma] = value;
                    StatUpdate();
                    return;

                case IoSvbk:
                    _cgbRamBank = (byte)(value & 0x7);
                    if (_cgbRamBank == 0) _cgbRamBank++;
                    _io[IoSvbk] = (byte)(value | ~0x7);
                    return;

                case IoVbk:
                    _cgbVramBank = (value & 0x1) != 0;
                    return;

                case IoBgpi: case IoObpi:
                    _io[register] = value;
                    return;

                case IoBgpd: case IoObpd:
                {
                    var indexRegister = register - 1;
                    if (!_cgbPalettesBlocked)
                    {
                        var index = _io[indexRegister] & 0x3F;
                        if (register == IoBgpd) _bgPalettes[index] = value;
                        else _objPalettes[index] = value;
                    }
                    if ((_io[indexRegister] & 0x80) != 0)
                    {
                        _io[indexRegister]++;
                        _io[indexRegister] |= 0x80;
                    }
                    return;
                }

                case IoKey1:
                    _io[IoKey1] = value;
                    return;

                case IoHdma1:
                    _hdmaCurrentSrc &= 0xF0;
                    _hdmaCurrentSrc |= (ushort)(value << 8);
                    if (_hdmaCurrentSrc >= 0xE000) _hdmaCurrentSrc |= 0xF000;
                    return;
                case IoHdma2:
                    _hdmaCurrentSrc &= 0xFF00;
                    _hdmaCurrentSrc |= (ushort)(value & 0xF0);
                    return;
                case IoHdma3:
                    _hdmaCurrentDest &= 0xF0;
                    _hdmaCurrentDest |= (ushort)(value << 8);
                    return;
                case IoHdma4:
                    _hdmaCurrentDest &= 0xFF00;
                    _hdmaCurrentDest |= (ushort)(value & 0xF0);
                    return;
                case IoHdma5:
                    _hdmaStepsLeft = (byte)((value & 0x7F) + 1);
                    if ((value & 0x80) == 0 && _hdmaOnHblank)
                    {
                        _hdmaOnHblank = false;
                        return;
                    }
                    _hdmaOn = (value & 0x80) == 0;
                    _hdmaOnHblank = (value & 0x80) != 0;
                    if (_hdmaOnHblank && (_io[IoStat] & 3) == 0 && _displayState != 7) _hdmaOn = true;
                    return;

                case IoSc:
                    // No link cable: an internally clocked transfer shifts in 1s.
                    _serialCount = 0;
                    if (_serialMasterClock) SerialMasterEdge();
                    _io[IoSc] = (byte)(value | ~0x83);
                    _serialMask = (byte)((value & 2) != 0 ? 4 : 0x80);
                    return;

                case IoRp:
                    _io[IoRp] = value;
                    return;

                default:
                    if (register is >= IoNr10 and <= IoWavEnd) ApuWrite(register, value);
                    return;
            }
            return;
        }

        if (address == 0xFFFF)
        {
            DisplaySync();
            _ie = value;
            return;
        }

        _hram[address - 0xFF80] = value;
    }

    // ----- OAM DMA and HDMA -----

    private void DmaRun()
    {
        if (_dmaCurrentDest == 0xA1) return;
        if (_halted || _stopped) return;
        var cycles = _dmaCycles + _dmaCyclesModulo;
        _inDmaRead = true;
        while (cycles >= 4)
        {
            cycles -= 4;
            if (_dmaCurrentDest >= 0xA0)
            {
                _dmaCurrentDest++;
                if (_displayState == 8)
                {
                    _io[IoStat] |= 2;
                    StatUpdate();
                }
                break;
            }
            if (_hdmaInProgress && (_hdmaStepsLeft > 1 || (_hdmaCurrentDest & 0xF) != 0xF))
                _dmaCurrentDest++;
            else if (_dmaCurrentSrc < 0xE000)
                _oam[_dmaCurrentDest++] = ReadMemory(_dmaCurrentSrc);
            else
                _oam[_dmaCurrentDest++] = 0xFF;

            // _dmaCurrentSrc must be the correct value during ReadMemory.
            _dmaCurrentSrc++;
            _dmaPpuVramConflict = false;
        }
        _inDmaRead = false;
        _dmaCyclesModulo = (sbyte)cycles;
        _dmaCycles = 0;
    }

    private void HdmaRun()
    {
        var cycles = (byte)(_doubleSpeed ? 4 : 2);
        _addrForHdmaConflict = 0xFFFF;
        var vramBase = _cgbVramBank ? 0x2000 : 0;
        _hdmaInProgress = true;
        AdvanceCycles(cycles);
        while (_hdmaOn)
        {
            var value = _dataBus;
            _addrForHdmaConflict = 0xFFFF;

            if (_hdmaCurrentSrc < 0x8000 || (_hdmaCurrentSrc & 0xE000) == 0xC000 || (_hdmaCurrentSrc & 0xE000) == 0xA000)
                value = ReadMemory(_hdmaCurrentSrc);
            if (DmaActive && (_dmaCyclesModulo == 2 || _doubleSpeed)) WriteOam((byte)_hdmaCurrentSrc, value);
            _hdmaCurrentSrc++;
            AdvanceCycles(cycles);
            if (_addrForHdmaConflict == 0xFFFF)
            {
                var addr = _hdmaCurrentDest++ & 0x1FFF;
                _vram[vramBase + addr] = value;
                if (_vramWriteBlocked) _vram[(vramBase ^ 0x2000) + addr] = value;
            }
            else
            {
                // The PPU read VRAM in the same cycle (CGB-E behaviour).
                _addrForHdmaConflict &= 0x1FFF;
                var addr = _hdmaCurrentDest & _addrForHdmaConflict & 0x1FFF;
                _vram[vramBase + addr] = value;
                if (_vramWriteBlocked) _vram[(vramBase ^ 0x2000) + addr] = value;
                _hdmaCurrentDest++;
            }

            if ((_hdmaCurrentDest & 0xF) == 0)
            {
                if (--_hdmaStepsLeft == 0 || _hdmaCurrentDest == 0)
                {
                    _hdmaOn = false;
                    _hdmaOnHblank = false;
                }
                else if (_hdmaOnHblank)
                {
                    _hdmaOn = false;
                }
            }
        }
        _hdmaInProgress = false;
        if (!_doubleSpeed) AdvanceCycles(2);
    }
}
