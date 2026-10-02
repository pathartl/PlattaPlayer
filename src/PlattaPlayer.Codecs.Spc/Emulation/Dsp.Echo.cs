namespace PlattaPlayer.Codecs.Spc.Emulation;

// The echo unit: an 8-tap FIR over a ring buffer in audio RAM, read and written on fixed clocks.
internal sealed partial class Dsp
{
    private int CalculateFir(int channel, int index)
    {
        var sample = _history[channel, (_historyOffset + index + 1) & 7];
        return (sample * _fir[index]) >> 6;
    }

    private int EchoOutputFor(int channel)
    {
        var main = (short)((_mainOutput[channel] * _mainVolume[channel]) >> 7);
        var echo = (short)((_echoInput[channel] * _echoVolume[channel]) >> 7);
        return Clamp16(main + echo);
    }

    private void EchoRead(int channel)
    {
        var address = (_echoAddress + channel * 2) & 0xffff;
        var lo = Ram[address];
        var hi = Ram[(address + 1) & 0xffff];
        int s = (short)((hi << 8) + lo);
        _history[channel, _historyOffset] = s >> 1;
    }

    private void EchoWrite(int channel)
    {
        if (!_echoReadonlyLatch)
        {
            var address = (_echoAddress + channel * 2) & 0xffff;
            var sample = _echoOutput[channel];
            Ram[address] = (byte)sample;
            Ram[(address + 1) & 0xffff] = (byte)(sample >> 8);
        }
        _echoOutput[channel] = 0;
    }

    private void Echo22()
    {
        // History.
        _historyOffset = (_historyOffset + 1) & 7;

        _echoAddress = ((_echoPageLatch << 8) + _echoOffset) & 0xffff;
        EchoRead(0);

        // FIR.
        _echoInput[0] = Wrap17(CalculateFir(0, 0));
        _echoInput[1] = Wrap17(CalculateFir(1, 0));
    }

    private void Echo23()
    {
        var l = CalculateFir(0, 1) + CalculateFir(0, 2);
        var r = CalculateFir(1, 1) + CalculateFir(1, 2);

        _echoInput[0] = Wrap17(_echoInput[0] + l);
        _echoInput[1] = Wrap17(_echoInput[1] + r);

        EchoRead(1);
    }

    private void Echo24()
    {
        var l = CalculateFir(0, 3) + CalculateFir(0, 4) + CalculateFir(0, 5);
        var r = CalculateFir(1, 3) + CalculateFir(1, 4) + CalculateFir(1, 5);

        _echoInput[0] = Wrap17(_echoInput[0] + l);
        _echoInput[1] = Wrap17(_echoInput[1] + r);
    }

    private void Echo25()
    {
        int l = _echoInput[0] + CalculateFir(0, 6);
        int r = _echoInput[1] + CalculateFir(1, 6);

        l = (short)l;
        r = (short)r;

        l += (short)CalculateFir(0, 7);
        r += (short)CalculateFir(1, 7);

        _echoInput[0] = Clamp16(l) & ~1;
        _echoInput[1] = Clamp16(r) & ~1;
    }

    private void Echo26()
    {
        // Left output volumes (saved for the next clock so both channels are output together).
        _mainOutput[0] = EchoOutputFor(0);

        // Echo feedback.
        var l = _echoOutput[0] + (short)((_echoInput[0] * _echoFeedback) >> 7);
        var r = _echoOutput[1] + (short)((_echoInput[1] * _echoFeedback) >> 7);

        _echoOutput[0] = Clamp16(l) & ~1;
        _echoOutput[1] = Clamp16(r) & ~1;
    }

    private void Echo27()
    {
        var outl = _mainOutput[0];
        var outr = EchoOutputFor(1);
        _mainOutput[0] = 0;
        _mainOutput[1] = 0;

        // ares notes that global muting isn't this simple on hardware (it switches the DAC, causing a short
        // pulse when first muted), but nobody has characterised it further.
        if (_mute)
        {
            outl = 0;
            outr = 0;
        }

        EmitSample(outl, outr);
    }

    private void Echo28() => _echoReadonlyLatch = _echoReadonly;

    private void Echo29()
    {
        _echoPageLatch = _echoPage;

        if (_echoOffset == 0) _echoLength = (_echoDelay << 11) & 0xffff;

        _echoOffset = (_echoOffset + 4) & 0xffff;
        if (_echoOffset >= _echoLength) _echoOffset = 0;

        // Write the left echo.
        EchoWrite(0);

        _echoReadonlyLatch = _echoReadonly;
    }

    // Write the right echo.
    private void Echo30() => EchoWrite(1);
}
