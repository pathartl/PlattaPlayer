namespace PlattaPlayer.Codecs.Gbs.Emulation;

// The PPU (SameBoy's display.c, CGB-E paths), for its timing only: modes, LY/LYC, the STAT and VBlank
// interrupts, VRAM/OAM/palette blocking and mode-3 length (scrolling, window, objects), plus the PPU's own
// VRAM/OAM reads where they interact with DMA. Pixels are not produced; pixel FIFOs are tracked by fill level.
//
// GB_display_run is a coroutine in SameBoy: GB_SLEEP saves a state number and returns, and the next call jumps
// back in through a switch. Here every resume point is a label at the method's top level and the loops around
// them are written with gotos, keeping SameBoy's state numbers.
internal sealed partial class GameBoy
{
    private const int LineLength = 456;
    private const int Lines = 144;
    private const int VirtualLines = 154;
    private const int Mode2Length = 80;
    private const uint LcdcPeriod = 70224;
    private const byte ObjectPriorityX = 0, ObjectPriorityIndex = 1;

    private const byte FetcherGetTileT1 = 0, FetcherGetTileT2 = 1, FetcherGetTileDataLowerT1 = 2,
        FetcherGetTileDataLowerT2 = 3, FetcherGetTileDataHighT1 = 4, FetcherGetTileDataHighT2 = 5, FetcherPush = 6;

    private int _displayCycles, _displayState;
    private bool _cgbVramBank;
    private byte _positionInLine;
    private bool _statInterruptLine;
    private byte _windowY;
    private bool _oamReadBlocked, _vramReadBlocked, _oamWriteBlocked, _vramWriteBlocked;
    private byte _currentLine;
    private ushort _lyForComparison;
    private byte _bgFifoReadEnd, _bgFifoSize, _oamFifoReadEnd, _oamFifoSize;
    private byte _fetcherY;
    private ushort _cyclesForLine;
    private byte _currentTile;
    private byte _currentTileAttributes;
    private byte _currentTileData0, _currentTileData1;
    private byte _fetcherState;
    private bool _windowIsBeingFetched;
    private bool _wxTriggered;
    private byte[] _visibleObjs = new byte[10];
    private byte[] _objectsX = new byte[10];
    private byte[] _objectsY = new byte[10];
    private byte _objectTileData0, _objectTileData1;
    private byte _mode2YBus;
    private byte _mode2XBus;  // the same bus carries the object's flags during object fetches
    private byte _nVisibleObjs;
    private byte _origNVisibleObjs;
    private byte _oamSearchIndex;
    private byte _modeForInterrupt;
    private bool _lycInterruptLine;
    private bool _cgbPalettesBlocked;
    private byte _objectPriority;
    private bool _oamPpuBlocked, _vramPpuBlocked, _cgbPalettesPpuBlocked;
    private bool _objectFetchAborted;
    private bool _duringObjectFetch;
    private ushort _objectLowLineAddress;
    private bool _wyTriggered;
    private byte _windowTileX;
    private ushort _lastTileDataAddress, _lastTileIndexAddress;
    private byte _dataForSelGlitch;
    private bool _delayedGlitchHblankInterrupt;
    private bool _disableWindowPixelInsertionGlitch;
    private bool _insertBgPixel;
    private byte _cpuVramBus;
    private bool _lastTileset;
    private bool _cgbWxGlitch;
    private bool _lineHasFractionalScrolling;
    private byte _wyCheckModulo;
    private bool _wyCheckScheduled;
    private bool _wyJustChecked;
    private bool _wx166InterruptGlitch;
    private ushort _mode3BatchingLength;
    private bool _tileSelGlitch;

    private ref byte ObjectFlags => ref _mode2XBus;

    private void ResetVideo()
    {
        _displayCycles = _displayState = 0;
        _cgbVramBank = false;
        _positionInLine = 0;
        _statInterruptLine = false;
        _windowY = 0;
        _oamReadBlocked = _vramReadBlocked = _oamWriteBlocked = _vramWriteBlocked = false;
        _currentLine = 0;
        _lyForComparison = 0;
        _bgFifoReadEnd = _bgFifoSize = _oamFifoReadEnd = _oamFifoSize = 0;
        _fetcherY = 0;
        _cyclesForLine = 0;
        _currentTile = _currentTileAttributes = _currentTileData0 = _currentTileData1 = 0;
        _fetcherState = 0;
        _windowIsBeingFetched = _wxTriggered = false;
        Array.Clear(_visibleObjs);
        Array.Clear(_objectsX);
        Array.Clear(_objectsY);
        _objectTileData0 = _objectTileData1 = 0;
        _mode2YBus = _mode2XBus = 0;
        _nVisibleObjs = _origNVisibleObjs = _oamSearchIndex = 0;
        _modeForInterrupt = 0;
        _lycInterruptLine = _cgbPalettesBlocked = false;
        _objectPriority = ObjectPriorityIndex;
        _oamPpuBlocked = _vramPpuBlocked = _cgbPalettesPpuBlocked = false;
        _objectFetchAborted = _duringObjectFetch = false;
        _objectLowLineAddress = 0;
        _wyTriggered = false;
        _windowTileX = 0;
        _lastTileDataAddress = _lastTileIndexAddress = 0;
        _dataForSelGlitch = 0;
        _delayedGlitchHblankInterrupt = _disableWindowPixelInsertionGlitch = _insertBgPixel = false;
        _cpuVramBus = 0;
        _lastTileset = _cgbWxGlitch = _lineHasFractionalScrolling = false;
        _wyCheckModulo = 0;
        _wyCheckScheduled = _wyJustChecked = _wx166InterruptGlitch = false;
        _mode3BatchingLength = 0;
        _tileSelGlitch = false;
    }

    private void DisplaySync() => DisplayRun(0, true);

    // The emulation-relevant part of a VBlank (SameBoy also hands the frame to the frontend here).
    private void DisplayVblank()
    {
        _cyclesSinceVblankCallback = 0;
        _lcdDisabledOutsideOfVblank = false;
    }

    private void WyCheck()
    {
        if ((_io[IoLcdc] & LcdcEnable) == 0) return;
        var comparison = _currentLine;
        if (_doubleSpeed && _lyForComparison != 0xFF) comparison = (byte)_lyForComparison;
        if ((_io[IoLcdc] & LcdcWinEnable) != 0 && _io[IoWy] == comparison) _wyTriggered = true;
    }

    private void StatUpdate()
    {
        if ((_io[IoLcdc] & LcdcEnable) == 0) return;
        if (DmaActive && (_io[IoStat] & 3) == 2) _io[IoStat] &= unchecked((byte)~3);

        var previousInterruptLine = _statInterruptLine;
        // The LY=LYC bit.
        if (_lyForComparison != 0xFFFF)
        {
            if (_lyForComparison == _io[IoLyc])
            {
                _lycInterruptLine = true;
                _io[IoStat] |= 4;
            }
            else
            {
                _lycInterruptLine = false;
                _io[IoStat] &= unchecked((byte)~4);
            }
        }

        _statInterruptLine = _modeForInterrupt switch
        {
            0 => (_io[IoStat] & 8) != 0,
            1 => (_io[IoStat] & 0x10) != 0,
            2 => (_io[IoStat] & 0x20) != 0,
            _ => false,
        };

        if ((_io[IoStat] & 0x40) != 0 && _lycInterruptLine) _statInterruptLine = true;
        if (_statInterruptLine && !previousInterruptLine) _io[IoIf] |= 2;
    }

    private void LcdOff()
    {
        _cyclesForLine = 0;
        _displayState = 0;
        _displayCycles = 0;
        if (_hdmaOnHblank && (_io[IoStat] & 3) != 0) _hdmaOn = true;

        // While the LCD is off, LY is 0 and STAT's mode is 0.
        _io[IoLy] = 0;
        _io[IoStat] &= unchecked((byte)~3);

        _oamReadBlocked = _vramReadBlocked = _oamWriteBlocked = _vramWriteBlocked = false;
        _cgbPalettesBlocked = false;

        _currentLine = 0;
        _lyForComparison = 0;
        _wyTriggered = false;
    }

    // The PPU's own OAM reads (object search and fetch), which see DMA's transfer.
    private byte OamReadPpu(byte address)
    {
        if (_oamPpuBlocked) return 0xFF;
        if (_dmaCurrentDest <= 0xA0 && _dmaCurrentDest > 0)
        {
            if (_hdmaInProgress) return ReadOam((byte)((_hdmaCurrentSrc & ~1) | (address & 1)));
            if (_dmaCurrentDest != 0xA0) return _oam[(_dmaCurrentDest & ~1) | (address & 1)];
        }
        return _oam[address];
    }

    private void AddObjectFromIndex(int index)
    {
        if (!DmaActive || _halted || _stopped)
        {
            _mode2YBus = OamReadPpu((byte)(index * 4));
            _mode2XBus = OamReadPpu((byte)(index * 4 + 1));
        }

        if (_nVisibleObjs == 10) return;
        if (_oamPpuBlocked) return;

        var height16 = (_io[IoLcdc] & LcdcObjSize) != 0;
        var y = _mode2YBus - 16;
        // Keeps the visible objects reverse-sorted by X (then OAM order).
        if (y <= _currentLine && y + (height16 ? 16 : 8) > _currentLine)
        {
            var j = 0;
            for (; j < _nVisibleObjs; j++)
                if (_objectsX[j] <= _mode2XBus) break;
            var count = _nVisibleObjs - j;
            Array.Copy(_visibleObjs, j, _visibleObjs, j + 1, count);
            Array.Copy(_objectsX, j, _objectsX, j + 1, count);
            Array.Copy(_objectsY, j, _objectsY, j + 1, count);
            _visibleObjs[j] = (byte)index;
            _objectsX[j] = _mode2XBus;
            _objectsY[j] = _mode2YBus;
            _nVisibleObjs++;
        }
    }

    // Pops a pixel (and adjusts for fine scrolling) when the FIFO can output one.
    private void RenderPixelIfPossible()
    {
        // Nothing is output while an object at X=0 is pending.
        if (_nVisibleObjs != 0 && _objectsX[_nVisibleObjs - 1] == 0) return;
        if (_bgFifoSize == 0) return;

        if (_insertBgPixel)
        {
            _insertBgPixel = false;
        }
        else
        {
            _bgFifoReadEnd = (byte)((_bgFifoReadEnd + 1) & 7);
            _bgFifoSize--;
        }
        if (_oamFifoSize != 0)
        {
            _oamFifoReadEnd = (byte)((_oamFifoReadEnd + 1) & 7);
            _oamFifoSize--;
        }

        // (position + 16 < 8) is (position < -8) in unsigned logic.
        if ((byte)(_positionInLine + 16) < 8)
        {
            if (_positionInLine == 0xEF)  // -17
            {
                _positionInLine = 0xF0;
            }
            else if ((_positionInLine & 7) == (_io[IoScx] & 7))
            {
                _positionInLine = 0xF8;
            }
            else if (_windowIsBeingFetched && (_positionInLine & 7) == 6 && (_io[IoScx] & 7) == 7)
            {
                _positionInLine = 0xF8;
            }
            else if (_positionInLine == 0xF7)  // -9
            {
                _positionInLine = 0xF0;
                return;
            }
            else
            {
                _lineHasFractionalScrolling = true;
            }
        }

        _windowIsBeingFetched = false;
        _positionInLine++;
    }

    // Runs the DMA up to the PPU's current point inside a batch, before the PPU reads VRAM or OAM.
    private void DmaSync(ref uint cycles)
    {
        if (!DmaActive) return;
        var offset = cycles - (uint)_displayCycles;
        if (offset == 0) return;
        cycles = (uint)_displayCycles;
        if (!_doubleSpeed) offset >>= 1;
        var old = _dmaCycles;
        _dmaCycles = (ushort)offset;
        DmaRun();
        _dmaCycles = (ushort)(old - offset);
    }

    private byte FetcherYValue() => _wxTriggered ? _windowY : (byte)(_currentLine + _io[IoScy]);

    // The PPU's own VRAM reads, which see DMA and HDMA conflicts.
    private byte VramReadPpu(ushort address)
    {
        if (_vramPpuBlocked) return 0xFF;
        if (_hdmaInProgress)
        {
            _addrForHdmaConflict = address;
            return 0;
        }
        if (_dmaCurrentDest <= 0xA0 && _dmaCurrentDest > 0 && (_dmaCurrentSrc & 0xE000) == 0x8000)
        {
            // DMAing from VRAM.
            var offset = _halted || _stopped ? 0 : 1;
            if (_dmaPpuVramConflict)
            {
                address = (ushort)((_dmaPpuVramConflictAddr & 0x1FFF) | (address & 0x2000));
            }
            else if (_dmaCyclesModulo != 0 && !_halted && !_stopped)
            {
                address &= 0x2000;
                address |= (ushort)((_dmaCurrentSrc - offset) & 0x1FFF);
            }
            else
            {
                address &= (ushort)(0x2000 | ((_dmaCurrentSrc - offset) & 0x1FFF));
                _dmaPpuVramConflictAddr = address;
                _dmaPpuVramConflict = !_halted && !_stopped;
            }
            var value = _vram[(address & 0x1FFF) | (_cgbVramBank ? 0x2000 : 0)];
            // At the very end of a DMA while halted SameBoy writes one past OAM, into the first palette byte.
            if (_dmaCurrentDest - offset < 0xA0) _oam[_dmaCurrentDest - offset] = value;
            else _bgPalettes[0] = value;
        }
        return _vram[address];
    }

    // The tile data a TILE_SEL change mid-fetch makes the fetcher use (Matt Currie's research).
    private byte DataForTileSelGlitch(out bool shouldUse)
    {
        shouldUse = true;
        if (_lastTileset)
        {
            shouldUse = (_currentTile & 0x80) == 0;
            return _currentTile;
        }
        return _dataForSelGlitch;
    }

    private void UpdateWxGlitch()
    {
        if ((_io[IoLcdc] & LcdcWinEnable) == 0 || !_wyTriggered)
        {
            _cgbWxGlitch = false;
            return;
        }
        if (_io[IoWx] == 0)
        {
            _cgbWxGlitch = (byte)(_positionInLine + 16) <= 8 || (_positionInLine == 0xF9 && _lineHasFractionalScrolling);
            return;
        }
        _cgbWxGlitch = (byte)(_positionInLine + 7 + (_windowIsBeingFetched ? 1 : 0)) == _io[IoWx];
    }

    private void AdvanceFetcherStateMachine(ref uint cycles)
    {
        switch (_fetcherState)
        {
            case FetcherGetTileT1:
            {
                UpdateWxGlitch();
                ushort map = 0x1800;
                if ((_io[IoLcdc] & LcdcWinEnable) == 0) _wxTriggered = false;
                if ((_io[IoLcdc] & LcdcBgMap) != 0 && !_wxTriggered) map = 0x1C00;
                else if ((_io[IoLcdc] & LcdcWinMap) != 0 && _wxTriggered) map = 0x1C00;

                var y = FetcherYValue();
                int x;
                if (_wxTriggered) x = _windowTileX;
                else if ((byte)(_positionInLine + 16) < 8) x = _io[IoScx] >> 3;
                else x = ((_io[IoScx] + _positionInLine + 8 - (_duringObjectFetch ? 0 : 1)) / 8) & 0x1F;
                _fetcherY = y;  // cached on the CGB-D and newer
                _lastTileIndexAddress = (ushort)(map + x + y / 8 * 32);
                _fetcherState++;
                break;
            }
            case FetcherGetTileT2:
                if (_cgbWxGlitch)
                {
                    _fetcherState++;
                    break;
                }
                DmaSync(ref cycles);
                // The CGB reads the tile index and its attributes in the same T-cycle.
                _currentTile = VramReadPpu(_lastTileIndexAddress);
                _currentTileAttributes = VramReadPpu((ushort)(_lastTileIndexAddress + 0x2000));
                _fetcherState++;
                break;

            case FetcherGetTileDataLowerT1:
                UpdateWxGlitch();
                _lastTileDataAddress = TileDataAddress(0);
                _fetcherState++;
                break;

            case FetcherGetTileDataLowerT2:
            {
                if (_cgbWxGlitch)
                {
                    _currentTileData0 = _currentTileData1;
                    _fetcherState++;
                    break;
                }
                DmaSync(ref cycles);
                var useGlitched = false;
                if (_tileSelGlitch) _currentTileData0 = DataForTileSelGlitch(out useGlitched);
                if (!useGlitched) _currentTileData0 = VramReadPpu(_lastTileDataAddress);
                if (_lastTileset && _tileSelGlitch) _dataForSelGlitch = VramReadPpu(_lastTileDataAddress);
                _fetcherState++;
                break;
            }

            case FetcherGetTileDataHighT1:
                UpdateWxGlitch();
                _lastTileDataAddress = TileDataAddress(1);
                _fetcherState++;
                break;

            case FetcherGetTileDataHighT2:
            {
                if (_cgbWxGlitch)
                {
                    _currentTileData1 = _currentTileData0;
                    _fetcherState++;
                    if (_wxTriggered)
                    {
                        _windowTileX++;
                        _windowTileX &= 0x1F;
                    }
                    break;
                }
                DmaSync(ref cycles);
                var useGlitched = false;
                if (_tileSelGlitch) _currentTileData1 = DataForTileSelGlitch(out useGlitched);
                if (!useGlitched) _dataForSelGlitch = _currentTileData1 = VramReadPpu(_lastTileDataAddress);
                if (_lastTileset && _tileSelGlitch) _dataForSelGlitch = VramReadPpu(_lastTileDataAddress);
                if (_wxTriggered)
                {
                    _windowTileX++;
                    _windowTileX &= 0x1F;
                }
                goto default;
            }

            default:
                _fetcherState = FetcherPush;
                if (_bgFifoSize > 0) break;
                _bgFifoSize = 8;
                _fetcherState = FetcherGetTileT1;
                break;
        }
    }

    // The address of the low (0) or high (1) byte of the current tile's row; also latches TILE_SEL.
    private ushort TileDataAddress(int high)
    {
        var y = _fetcherY;
        _lastTileset = (_io[IoLcdc] & LcdcTileSel) != 0;
        var tileAddress = _lastTileset ? _currentTile * 0x10 : (sbyte)_currentTile * 0x10 + 0x1000;
        if ((_currentTileAttributes & 8) != 0) tileAddress += 0x2000;
        var yFlip = (_currentTileAttributes & 0x40) != 0 ? 7 : 0;
        return (ushort)(tileAddress + ((y & 7) ^ yFlip) * 2 + high);
    }

    private ushort GetObjectLineAddress(byte y, byte tile, byte flags)
    {
        var height16 = (_io[IoLcdc] & LcdcObjSize) != 0;
        var tileY = (byte)((_currentLine - y) & (height16 ? 0xF : 7));
        if ((flags & 0x40) != 0) tileY ^= (byte)(height16 ? 0xF : 7);  // Y flip
        var lineAddress = (ushort)((height16 ? tile & 0xFE : tile) * 0x10 + tileY * 2);
        if ((flags & 0x8) != 0) lineAddress += 0x2000;  // VRAM bank 1
        return lineAddress;
    }

    // SameBoy renders a whole line at once when mode 3's length is predictable (no objects or window to
    // fetch mid-line, or nothing that could observe the difference); this is its effect on the PPU state.
    private void RenderLine()
    {
        if (_currentLine > 144) return;
        if (_nVisibleObjs != 0 && (_io[IoLcdc] & LcdcObjEn) != 0)
        {
            while (_nVisibleObjs != 0)
            {
                var index = _visibleObjs[_nVisibleObjs - 1];
                _nVisibleObjs--;
                var lineAddress = GetObjectLineAddress(_oam[index * 4], _oam[index * 4 + 2], _oam[index * 4 + 3]);
                if (_nVisibleObjs == 0) _dataForSelGlitch = _vram[lineAddress + 1];
            }
        }

        var pixels = 0;
        var fractionalScroll = _io[IoScx] & 7;
        var checkWindow = _wyTriggered && (_io[IoLcdc] & LcdcWinEnable) != 0;
        var i = fractionalScroll;
    FirstTile:
        for (; i < 8; i++)
        {
            if (checkWindow && _io[IoWx] == pixels + 7)
            {
                checkWindow = false;
                ++_windowY;
                break;
            }
            pixels++;
        }

        while (pixels < 160 - 8)
        {
            for (var p = 0; p < 8; p++)
            {
                if (checkWindow && _io[IoWx] == pixels + 7)
                {
                    i = 8;  // SameBoy jumps back into the first loop's window activation, then leaves it
                    checkWindow = false;
                    ++_windowY;
                    goto FirstTile;
                }
                pixels++;
            }
        }

        _fetcherState = (byte)((160 - pixels) & 7);
        while (pixels < 160)
        {
            if (checkWindow && _io[IoWx] == pixels + 7)
            {
                i = 8;
                checkWindow = false;
                ++_windowY;
                goto FirstTile;
            }
            pixels++;
        }
    }

    private ushort Mode3BatchingLength()
    {
        if (_positionInLine != 0xF0) return 0;
        if (_hdmaOn) return 0;
        if (_stopped) return 0;
        if (DmaActive) return 0;
        if (_wxTriggered) return 0;
        if (_wyTriggered && (_io[IoLcdc] & LcdcWinEnable) != 0
            && (_io[IoWx] < 7 || _io[IoWx] == 166 || _io[IoWx] == 167))
        {
            return 0;
        }

        // No objects or window: the timing is trivial.
        if (_nVisibleObjs == 0 && !(_wyTriggered && (_io[IoLcdc] & LcdcWinEnable) != 0)) return (ushort)(167 + (_io[IoScx] & 7));
        if (_hdmaOnHblank) return 0;
        // 300 is a bit more than the longest mode 3; fine as long as nothing can observe HBlank's start.
        if ((_io[IoStat] & 0x8) == 0) return 300;
        if ((_ie & 2) == 0) return 300;
        return 0;
    }

    private byte XForObjectMatch()
    {
        var ret = (byte)(_positionInLine + 8);
        return ret > 0xF0 ? (byte)0 : ret;
    }

    // DisplayRun(cycles, false) when it would only count the cycles down: no pending WY check or delayed
    // interrupt, the LCD on, no line end and no state change within them.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private void DisplayTick(uint cycles)
    {
        if (!_wyCheckScheduled && !_delayedGlitchHblankInterrupt && (_io[IoLcdc] & LcdcEnable) != 0
            && _displayCycles + (int)cycles <= 0 && _cyclesForLine * 2 + (int)cycles + _displayCycles <= LineLength * 2)
        {
            _cyclesSinceVblankCallback += cycles / 2;
            _wyCheckModulo += (byte)cycles;
            _displayCycles += (int)cycles;
            return;
        }
        DisplayRun(cycles, false);
    }

    /// <summary>Runs the PPU for <paramref name="cycles"/> 8 MHz ticks (GB_display_run). With
    /// <paramref name="force"/> false it may defer the work (batching) until something needs it.</summary>
    private void DisplayRun(uint cycles, bool force)
    {
        if (_wyTriggered)
        {
            _wyCheckScheduled = false;
        }
        else if (_wyCheckScheduled)
        {
            force = true;
            var cyclesToCheck = _doubleSpeed ? (uint)(8 - ((_wyCheckModulo + 6) & 7)) : (uint)(8 - (_wyCheckModulo & 7));
            if (cycles >= cyclesToCheck)
            {
                _wyCheckScheduled = false;
                DisplayRun(cyclesToCheck, true);
                WyCheck();
                if (_displayState == 21 && !_doubleSpeed) _wyJustChecked = true;
                cycles -= cyclesToCheck;
            }
        }

        if ((_io[IoLcdc] & LcdcEnable) != 0 && _cyclesForLine * 2 + (int)cycles + _displayCycles > LineLength * 2)
        {
            var firstBatch = (uint)(LineLength * 2 - _cyclesForLine * 2 + _displayCycles);
            DisplayRun(firstBatch, force);
            cycles -= firstBatch;
            if (_displayState == 22)
            {
                _io[IoStat] &= unchecked((byte)~3);
                _modeForInterrupt = 0;
                StatUpdate();
            }
            _displayState = 9;
            _displayCycles = 0;
        }
        if (_delayedGlitchHblankInterrupt && cycles != 0 && _currentLine < Lines)
        {
            _delayedGlitchHblankInterrupt = false;
            _modeForInterrupt = 0;
            StatUpdate();
            _modeForInterrupt = 3;
        }
        _cyclesSinceVblankCallback += cycles / 2;
        _wyCheckModulo += (byte)cycles;

        var allowBatching = !force;
        _displayCycles += (int)cycles;
        if (_displayCycles <= 0) return;

        switch (_displayState)
        {
            case 1: goto State1;
            case 2: goto State2;
            case 3: goto State3;
            case 4: goto State4;
            case 5: goto State5;
            case 6: goto State6;
            case 7: goto State7;
            case 8: goto State8;
            case 9: goto Display9;
            case 10: goto State10;
            case 11: goto State11;
            case 12: goto State12;
            case 13: goto State13;
            case 14: goto State14;
            case 15: goto State15;
            case 16: goto State16;
            case 17: goto State17;
            case 19: goto State19;
            case 20: goto State20;
            case 21: goto State21;
            case 22: goto State22;
            case 24: goto State24;
            case 26: goto State26;
            case 27: goto State27;
            case 28: goto State28;
            case 29: goto State29;
            case 31: goto State31;
            case 32: goto State32;
            case 33: goto State33;
            case 34: goto State34;
            case 35: goto State35;
            case 36: goto State36;
            case 37: goto State37;
            case 38: goto State38;
            case 39: goto State39;
            case 40: goto State40;
            case 41: goto State41;
            case 43: goto State43;
        }

        _wyCheckModulo = (byte)cycles;
        _wyJustChecked = false;

        if ((_io[IoLcdc] & LcdcEnable) == 0) goto LcdOffLoop;

        // Mode 2 of the very first line 0 after the LCD turns on.
        _currentLine = 0;
        _windowY = 0xFF;
        _wyTriggered = false;
        _positionInLine = 0xF0;
        _lineHasFractionalScrolling = false;

        _lyForComparison = 0;
        _io[IoStat] &= unchecked((byte)~3);
        _modeForInterrupt = 0xFF;
        _oamReadBlocked = _vramReadBlocked = _oamWriteBlocked = _vramWriteBlocked = false;
        _cgbPalettesBlocked = false;
        _cyclesForLine = Mode2Length - 4;
        StatUpdate();
        if (Sleep(2, Mode2Length - 4)) return;
    State2:
        _oamWriteBlocked = true;
        _cyclesForLine += 2;
        StatUpdate();
        if (Sleep(34, 2)) return;
    State34:
        _nVisibleObjs = 0;
        _origNVisibleObjs = 0;
        _cyclesForLine += 8;  // mode 0 is shorter on the first line 0
        _io[IoStat] &= unchecked((byte)~3);
        _io[IoStat] |= 3;
        _modeForInterrupt = 3;
        _oamWriteBlocked = true;
        _oamReadBlocked = true;
        _vramReadBlocked = _doubleSpeed;
        _vramWriteBlocked = _doubleSpeed;
        _cyclesForLine += 2;
        if (Sleep(37, 2)) return;
    State37:
        _cgbPalettesBlocked = true;
        _cyclesForLine += 3;
        if (Sleep(38, 3)) return;
    State38:
        _vramReadBlocked = true;
        _vramWriteBlocked = true;
        _wxTriggered = false;
        goto Mode3Start;

    LcdOffLoop:
        // With the LCD off, SameBoy still produces a frame every LCDC period.
        if (_cyclesSinceVblankCallback < LcdcPeriod && Sleep(1, LcdcPeriod - _cyclesSinceVblankCallback)) return;
    State1:
        DisplayVblank();
        goto LcdOffLoop;

        // Mode 3 cut short by the line ending (state 9).
    Display9:
        _nVisibleObjs = _origNVisibleObjs;
        _currentLine++;
        WyCheck();
        _cyclesForLine = 0;
        if (_currentLine == Lines)
        {
            if (_positionInLine >= 156 && _positionInLine < 0xF0) _delayedGlitchHblankInterrupt = true;
            _positionInLine = 0xF0;
            _lineHasFractionalScrolling = false;
            goto VblankLines;
        }
        _cyclesForLine = 2;
        if (Sleep(28, 2)) return;
    State28:
        _io[IoLy] = _currentLine;
        if (_positionInLine >= 156 && _positionInLine < 0xF0) _delayedGlitchHblankInterrupt = true;
        StatUpdate();
        _positionInLine = 0xF1;
        goto Mode3Start;

        // Lines 0-143.
    VisibleLines:
        if (_currentLine >= Lines) goto VblankLines;
        WyCheck();
        _oamWriteBlocked = !_doubleSpeed;
        if (Sleep(35, 2)) return;
    State35:
        _oamWriteBlocked = true;
        if (Sleep(6, 1)) return;
    State6:
        _io[IoLy] = _currentLine;
        _oamReadBlocked = true;
        _lyForComparison = (ushort)(_currentLine != 0 ? 0xFFFF : 0);
        // The OAM STAT interrupt occurs 1 T-cycle before STAT actually changes, except on line 0.
        if (_currentLine != 0)
        {
            _modeForInterrupt = 2;
            _io[IoStat] &= unchecked((byte)~3);
        }
        StatUpdate();
        if (Sleep(7, 1)) return;
    State7:
        _oamReadBlocked = true;
        _io[IoStat] &= unchecked((byte)~3);
        _io[IoStat] |= 2;
        _modeForInterrupt = 2;
        _oamWriteBlocked = true;
        _lyForComparison = _currentLine;
        WyCheck();
        StatUpdate();
        _modeForInterrupt = 0xFF;
        StatUpdate();
        _nVisibleObjs = 0;
        _origNVisibleObjs = 0;
        if (DmaActive || _oamPpuBlocked) goto OamSearch;
    State5:
        if (allowBatching && _displayCycles < 80 * 2)
        {
            _displayState = 5;
            return;
        }
    OamSearch:
        _oamSearchIndex = 0;
    OamSearchLoop:
        if (_oamSearchIndex >= 40) goto OamSearchDone;
        AddObjectFromIndex(_oamSearchIndex);
        if (Sleep(8, 2)) return;
    State8:
        if (_oamSearchIndex == 37)
        {
            _vramReadBlocked = false;
            _vramWriteBlocked = false;
            _cgbPalettesBlocked = false;
            _oamWriteBlocked = true;
        }
        _oamSearchIndex++;
        goto OamSearchLoop;
    OamSearchDone:
        _cyclesForLine = Mode2Length + 4;
        _origNVisibleObjs = _nVisibleObjs;
        _io[IoStat] &= unchecked((byte)~3);
        _io[IoStat] |= 3;
        _modeForInterrupt = 3;
        _vramReadBlocked = true;
        _vramWriteBlocked = true;
        _cgbPalettesBlocked = false;
        _oamWriteBlocked = true;
        _oamReadBlocked = true;
        StatUpdate();

        _cyclesForLine += 3;
        if (Sleep(10, 3)) return;
    State10:
        _cgbPalettesBlocked = true;
        _cyclesForLine += 2;
        if (Sleep(32, 2)) return;
    State32:
    Mode3Start:
        _disableWindowPixelInsertionGlitch = false;
        _bgFifoReadEnd = _bgFifoSize = 0;
        _oamFifoReadEnd = _oamFifoSize = 0;
        _bgFifoSize = 8;  // 8 pixels of junk, dropped anyway
        _fetcherState = FetcherGetTileT1;
        _mode3BatchingLength = Mode3BatchingLength();
        if (_mode3BatchingLength == 0) goto SlowMode3;
    State3:
        if (allowBatching && _displayCycles < _mode3BatchingLength * 2)
        {
            _displayState = 3;
            return;
        }
        if (_displayCycles / 2 < _mode3BatchingLength) goto SlowMode3;
        // Successfully batched.
        _positionInLine = 160;
        _cyclesForLine += _mode3BatchingLength;
        RenderLine();
        if (Sleep(4, _mode3BatchingLength)) return;
    State4:
        goto SkipSlowMode3;

    SlowMode3:
        _wx166InterruptGlitch = false;
        if (_wyJustChecked)
        {
            _wyJustChecked = false;
        }
        else if (!_wxTriggered && _wyTriggered && (_io[IoLcdc] & LcdcWinEnable) != 0)
        {
            var shouldActivateWindow = false;
            if (_io[IoWx] == 0)
            {
                if (_positionInLine == 0xF9) shouldActivateWindow = true;
                else if (_positionInLine == 0xF0 && (_io[IoScx] & 7) != 0) shouldActivateWindow = true;
                else if (_positionInLine is >= 0xF1 and <= 0xF8) shouldActivateWindow = true;
            }
            else if (_io[IoWx] < 167 && _io[IoWx] == (byte)(_positionInLine + 7))
            {
                shouldActivateWindow = true;
            }

            if (shouldActivateWindow)
            {
                _windowY++;
                _windowTileX = 0;
                _bgFifoReadEnd = _bgFifoSize = 0;
                if (_io[IoWx] == 166) _wx166InterruptGlitch = true;
                _wxTriggered = true;
                _fetcherState = FetcherGetTileT1;
                _windowIsBeingFetched = true;
            }
        }

        if (_io[IoWx] == 0 && _io[IoWx] == (byte)(_positionInLine + 7) && _wxTriggered && !_windowIsBeingFetched
            && _fetcherState == FetcherGetTileT1 && _bgFifoSize == 8)
        {
            // Insert a pixel right at the FIFO's end.
            _insertBgPixel = true;
        }

        // Objects (the CGB fetches them even with objects disabled; the bit is checked when pixels pop).
        while (_nVisibleObjs != 0 && _objectsX[_nVisibleObjs - 1] < XForObjectMatch()) _nVisibleObjs--;

        _duringObjectFetch = true;
    ObjectLoop:
        if (_nVisibleObjs == 0 || _objectsX[_nVisibleObjs - 1] != XForObjectMatch()) goto AbortFetchingObject;
    FetchUntilObject:
        if (_fetcherState >= FetcherGetTileDataHighT2 && _bgFifoSize != 0) goto FetchObject;
        AdvanceFetcherStateMachine(ref cycles);
        _cyclesForLine++;
        if (Sleep(27, 1)) return;
    State27:
        if (_objectFetchAborted) goto AbortFetchingObject;
        goto FetchUntilObject;
    FetchObject:
        AdvanceFetcherStateMachine(ref cycles);
        _cyclesForLine++;
        if (Sleep(41, 1)) return;
    State41:
        if (_objectFetchAborted) goto AbortFetchingObject;

        AdvanceFetcherStateMachine(ref cycles);
        DmaSync(ref cycles);
        _mode2YBus = OamReadPpu((byte)(_visibleObjs[_nVisibleObjs - 1] * 4 + 2));
        ObjectFlags = OamReadPpu((byte)(_visibleObjs[_nVisibleObjs - 1] * 4 + 3));
        _cyclesForLine += 2;
        if (Sleep(20, 2)) return;
    State20:
        if (_objectFetchAborted) goto AbortFetchingObject;

        DmaSync(ref cycles);
        _objectLowLineAddress = GetObjectLineAddress(_objectsY[_nVisibleObjs - 1], _mode2YBus, ObjectFlags);
        _objectTileData0 = VramReadPpu(_objectLowLineAddress);
        _cyclesForLine += 2;
        if (Sleep(39, 2)) return;
    State39:
        if (_objectFetchAborted) goto AbortFetchingObject;

        _duringObjectFetch = false;
        _cyclesForLine++;
        _objectLowLineAddress = GetObjectLineAddress(_objectsY[_nVisibleObjs - 1], _mode2YBus, ObjectFlags);
        DmaSync(ref cycles);
        _objectTileData1 = VramReadPpu((ushort)(_objectLowLineAddress + 1));
        if (Sleep(40, 1)) return;
    State40:
        // The object row overlays the object FIFO, filling it to 8.
        _oamFifoSize = 8;
        _dataForSelGlitch = _vramPpuBlocked ? (byte)0xFF : _vram[_objectLowLineAddress + 1];
        _nVisibleObjs--;
        goto ObjectLoop;

    AbortFetchingObject:
        _objectFetchAborted = false;
        _duringObjectFetch = false;

        RenderPixelIfPossible();
        AdvanceFetcherStateMachine(ref cycles);
        if (_positionInLine == 160) goto SkipSlowMode3;

        _cyclesForLine++;
        if (Sleep(21, 1)) return;
    State21:
        if (_wx166InterruptGlitch)
        {
            _modeForInterrupt = 0;
            StatUpdate();
        }
        goto SlowMode3;

    SkipSlowMode3:
        _positionInLine = 0xF0;
        _lineHasFractionalScrolling = false;
        if (_fetcherState is FetcherGetTileDataHighT1 or FetcherGetTileDataHighT2) _currentTileData1 = _currentTileData0;

        if (_currentLine == 143) _windowY = 0xFF;
        _wxTriggered = false;

        if (!_doubleSpeed)
        {
            _io[IoStat] &= unchecked((byte)~3);
            _modeForInterrupt = 0;
            _oamReadBlocked = true;
            _vramReadBlocked = false;
            _oamWriteBlocked = false;
            _vramWriteBlocked = false;
        }

        _cyclesForLine++;
        if (Sleep(22, 1)) return;
    State22:
        _io[IoStat] &= unchecked((byte)~3);
        _modeForInterrupt = 0;
        _oamReadBlocked = false;
        _vramReadBlocked = false;
        _oamWriteBlocked = false;
        _vramWriteBlocked = false;
        StatUpdate();

        _cyclesForLine += 2;
        if (Sleep(33, 2)) return;
    State33:
        _cgbPalettesBlocked = !_doubleSpeed;
        if (_hdmaOnHblank && !_halted && !_stopped) _hdmaOn = true;

        _cyclesForLine += 2;
        if (Sleep(36, 2)) return;
    State36:
        _cgbPalettesBlocked = false;

        if (_cyclesForLine <= LineLength - 2) goto LineEnd;
        _cyclesForLine = 0;
        if (Sleep(43, LineLength)) return;
    State43:
        goto Display9;

    LineEnd:
    {
        var cyclesForLine = _cyclesForLine;
        _cyclesForLine = 0;
        if (Sleep(11, (uint)(LineLength - cyclesForLine - 2))) return;
    }
    State11:
        _cyclesForLine = 0;
        if (Sleep(31, 2)) return;
    State31:
        if (_currentLine != Lines - 1) _modeForInterrupt = 2;
        _currentLine++;
        goto VisibleLines;

        // Lines 144-152.
    VblankLines:
        if (_currentLine >= VirtualLines - 1) goto Line153;
        _lyForComparison = 0xFFFF;
        StatUpdate();
        if (Sleep(26, 2)) return;
    State26:
        _io[IoLy] = _currentLine;
        if (_currentLine == Lines && !_statInterruptLine && (_io[IoStat] & 0x20) != 0) _io[IoIf] |= 2;
        if (Sleep(12, 2)) return;
    State12:
        if (_delayedGlitchHblankInterrupt)
        {
            _delayedGlitchHblankInterrupt = false;
            _modeForInterrupt = 0;
        }
        _lyForComparison = _currentLine;
        StatUpdate();
        if (Sleep(24, 1)) return;
    State24:
        if (_currentLine == Lines)
        {
            // Entering VBlank also triggers the OAM STAT interrupt.
            _io[IoStat] &= unchecked((byte)~3);
            _io[IoStat] |= 1;
            _io[IoIf] |= 1;
            if (!_statInterruptLine && (_io[IoStat] & 0x20) != 0) _io[IoIf] |= 2;
            _modeForInterrupt = 1;
            StatUpdate();

            DisplayVblank();
        }
        if (Sleep(13, LineLength - 5)) return;
    State13:
        _currentLine++;
        goto VblankLines;

        // Line 153.
    Line153:
        _lyForComparison = 0xFFFF;
        StatUpdate();
        if (Sleep(19, 2)) return;
    State19:
        _io[IoLy] = 153;
        if (Sleep(14, 2)) return;
    State14:
        _lyForComparison = 153;
        StatUpdate();
        if (Sleep(15, 4)) return;
    State15:
        _io[IoLy] = 0;
        _lyForComparison = 153;
        StatUpdate();
        if (Sleep(16, 4)) return;
    State16:
        _lyForComparison = 0;
        StatUpdate();
        if (Sleep(29, 12)) return;  // writing LYC in this window has side effects on a CGB
    State29:
        if (Sleep(17, LineLength - 24)) return;
    State17:
        _currentLine = 0;
        _wyTriggered = false;
        goto VisibleLines;
    }

    // GB_SLEEP: spends `cycles` T-cycles (2 ticks each); true when the PPU must suspend in `state`.
    private bool Sleep(int state, uint cycles)
    {
        _displayCycles -= (int)(cycles * 2);
        if (_displayCycles > 0) return false;
        _displayState = state;
        return true;
    }
}
