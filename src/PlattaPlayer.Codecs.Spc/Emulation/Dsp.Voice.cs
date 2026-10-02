namespace PlattaPlayer.Codecs.Spc.Emulation;

// Voice pipeline, envelope and BRR decoding. Each voice is processed in nine steps spread over three
// clocks of its own and overlapping its neighbours, exactly as the hardware schedules its memory reads.
internal sealed partial class Dsp
{
    private void VoiceOutput(Voice v, int channel)
    {
        // Apply left/right volume.
        var amp = (_latchOutput * (channel == 0 ? v.Volume0 : v.Volume1)) >> 7;

        // Add to the output total.
        _mainOutput[channel] = Clamp16(Wrap17(_mainOutput[channel] + amp));

        // Optionally add to the echo total.
        if (v.EchoLatch)
            _echoOutput[channel] = Clamp16(Wrap17(_echoOutput[channel] + amp));
    }

    private void Voice1(Voice v)
    {
        _brrAddress = ((_brrBankLatch << 8) + (_brrSource << 2)) & 0xffff;
        _brrSource = v.Source;
    }

    private void Voice2(Voice v)
    {
        // Read the sample pointer (ignored if not needed).
        var address = _brrAddress;
        if (v.KeyOnDelay == 0) address = (address + 2) & 0xffff;
        var lo = Ram[address];
        var hi = Ram[(address + 1) & 0xffff];
        _brrNextAddress = lo | (hi << 8);
        _latchAdsr0 = v.Adsr0;

        // Read pitch, spread over two clocks.
        _latchPitch = v.Pitch & 0xff;
    }

    private void Voice3(Voice v)
    {
        Voice3a(v);
        Voice3b(v);
        Voice3c(v);
    }

    private void Voice3a(Voice v) => _latchPitch |= v.Pitch & ~0xff;

    private void Voice3b(Voice v)
    {
        _brrByte = Ram[(v.BrrAddress + v.BrrOffset) & 0xffff];
        _brrHeader = Ram[v.BrrAddress];
    }

    private void Voice3c(Voice v)
    {
        // Pitch modulation using the previous voice's output.
        if (v.ModulateLatch)
            _latchPitch = (_latchPitch + (((_latchOutput >> 5) * _latchPitch) >> 10)) & 0x7fff;

        if (v.KeyOnDelay != 0)
        {
            // Get ready to start BRR decoding on the next sample.
            if (v.KeyOnDelay == 5)
            {
                v.BrrAddress = _brrNextAddress;
                v.BrrOffset = 1;
                v.BufferOffset = 0;
                _brrHeader = 0;  // the header is ignored on this sample
            }

            // The envelope is never run during KON.
            v.Envelope = 0;
            v.EnvelopeLatch = 0;

            // Disable BRR decoding until the last three samples.
            v.GaussianOffset = 0;
            v.KeyOnDelay--;
            if ((v.KeyOnDelay & 3) != 0) v.GaussianOffset = 0x4000;

            // Pitch is never added during KON.
            _latchPitch = 0;
        }

        // Gaussian interpolation.
        var output = GaussianInterpolate(v);

        // Noise.
        if (v.NoiseLatch) output = (short)(_noiseLfsr << 1);

        // Apply the envelope.
        _latchOutput = (short)(((output * v.Envelope) >> 11) & ~1);
        v.Envx = (v.Envelope >> 4) & 0xff;

        // Immediate silence due to the end of the sample or a soft reset.
        if (_reset || (_brrHeader & 3) == 1)
        {
            v.EnvelopeMode = Release;
            v.Envelope = 0;
        }

        if (_sample)
        {
            if (v.KeyOffLatch) v.EnvelopeMode = Release;

            if (v.KeyOnLatch)
            {
                v.KeyOnDelay = 5;
                v.EnvelopeMode = Attack;
            }
        }

        // Run the envelope for the next sample.
        if (v.KeyOnDelay == 0) EnvelopeRun(v);
    }

    private void Voice4(Voice v)
    {
        // Decode BRR.
        v.Looped = false;
        if (v.GaussianOffset >= 0x4000)
        {
            BrrDecode(v);
            v.BrrOffset += 2;
            if (v.BrrOffset >= 9)
            {
                // Start decoding the next BRR block.
                v.BrrAddress = (v.BrrAddress + 9) & 0xffff;
                if ((_brrHeader & 1) != 0)
                {
                    v.BrrAddress = _brrNextAddress;
                    v.Looped = true;
                }
                v.BrrOffset = 1;
            }
        }

        // Apply pitch.
        v.GaussianOffset = ((v.GaussianOffset & 0x3fff) + _latchPitch) & 0xffff;

        // Keep from getting too far ahead (when using pitch modulation).
        if (v.GaussianOffset > 0x7fff) v.GaussianOffset = 0x7fff;

        VoiceOutput(v, 0);
    }

    private void Voice5(Voice v)
    {
        VoiceOutput(v, 1);

        // ENDX, OUTX and ENVX won't update if they were written 1-2 clocks earlier. For ENDX that is because
        // the new value is built here from the register as it stands and only stored two clocks later
        // (Voice7), so a write in between is overwritten. ares keeps a flag per voice instead, which loses
        // the quirk; this follows blargg's SPC_DSP (snes_spc), whose behaviour was tested on hardware.
        var endx = Registers[0x7c] | (v.Looped ? v.Bit : 0);

        // Clear the bit if KON just began.
        if (v.KeyOnDelay == 5) endx &= ~v.Bit;
        _endxBuffer = endx;
    }

    private void Voice6(Voice v) => _latchOutx = (_latchOutput >> 8) & 0xff;

    private void Voice7(Voice v)
    {
        Registers[0x7c] = (byte)_endxBuffer;
        _latchEnvx = v.Envx;
    }

    private void Voice8(Voice v) => Registers[v.Index | 0x09] = (byte)_latchOutx;

    private void Voice9(Voice v) => Registers[v.Index | 0x08] = (byte)_latchEnvx;

    // ----- Envelope ------------------------------------------------------------------------------------

    private void EnvelopeRun(Voice v)
    {
        var envelope = v.Envelope;

        if (v.EnvelopeMode == Release)
        {
            envelope -= 0x8;
            if (envelope < 0) envelope = 0;
            v.Envelope = envelope;
            return;
        }

        int rate;
        var envelopeData = v.Adsr1;
        if ((_latchAdsr0 & 0x80) != 0)
        {
            // ADSR
            if (v.EnvelopeMode >= Decay)
            {
                envelope--;
                envelope -= envelope >> 8;
                rate = envelopeData & 0x1f;
                if (v.EnvelopeMode == Decay) rate = ((_latchAdsr0 >> 4) & 7) * 2 + 16;
            }
            else
            {
                // Attack
                rate = (_latchAdsr0 & 0x0f) * 2 + 1;
                envelope += rate < 31 ? 0x20 : 0x400;
            }
        }
        else
        {
            // GAIN
            envelopeData = v.Gain;
            var mode = envelopeData >> 5;
            if (mode < 4)
            {
                // Direct
                envelope = envelopeData << 4;
                rate = 31;
            }
            else
            {
                rate = envelopeData & 0x1f;
                if (mode == 4)
                {
                    // Linear decrease
                    envelope -= 0x20;
                }
                else if (mode < 6)
                {
                    // Exponential decrease
                    envelope--;
                    envelope -= envelope >> 8;
                }
                else
                {
                    // Linear increase
                    envelope += 0x20;
                    if (mode > 6 && (uint)v.EnvelopeLatch >= 0x600)
                        envelope += 0x8 - 0x20;  // mode 7: two-slope linear increase
                }
            }
        }

        // Sustain level.
        if ((envelope >> 8) == (envelopeData >> 5) && v.EnvelopeMode == Decay)
            v.EnvelopeMode = Sustain;
        v.EnvelopeLatch = envelope;

        // The unsigned compare catches a linear decrease underflowing too.
        if ((uint)envelope > 0x7ff)
        {
            envelope = envelope < 0 ? 0 : 0x7ff;
            if (v.EnvelopeMode == Attack) v.EnvelopeMode = Decay;
        }

        if (CounterPoll(rate)) v.Envelope = envelope;
    }

    // ----- BRR -----------------------------------------------------------------------------------------

    private void BrrDecode(Voice v)
    {
        // _brrByte = RAM[BrrAddress + BrrOffset] was cached on the previous clock.
        var nybbles = (_brrByte << 8) | Ram[(v.BrrAddress + v.BrrOffset + 1) & 0xffff];

        var filter = (_brrHeader >> 2) & 3;
        var scale = (_brrHeader >> 4) & 0x0f;
        var buffer = v.Buffer;

        // Decode four samples.
        for (var n = 0; n < 4; n++)
        {
            // Bits 12-15 are the current nybble: sign-extend it to a 4-bit sample.
            int s = (short)nybbles >> 12;
            nybbles <<= 4;

            if (scale <= 12)
            {
                s <<= scale;
                s >>= 1;
            }
            else
            {
                s &= ~0x7ff;
            }

            // Apply the IIR filter (2 is the most commonly used).
            var offset = v.BufferOffset;
            if (--offset < 0) offset = 11;
            int p1 = buffer[offset];
            if (--offset < 0) offset = 11;
            int p2 = buffer[offset] >> 1;

            switch (filter)
            {
                case 1:
                    // s += p1 * 0.46875
                    s += p1 >> 1;
                    s += -p1 >> 5;
                    break;
                case 2:
                    // s += p1 * 0.953125 - p2 * 0.46875
                    s += p1;
                    s -= p2;
                    s += p2 >> 4;
                    s += (p1 * -3) >> 6;
                    break;
                case 3:
                    // s += p1 * 0.8984375 - p2 * 0.40625
                    s += p1;
                    s -= p2;
                    s += (p1 * -13) >> 7;
                    s += (p2 * 3) >> 4;
                    break;
            }

            // Clamp, then store doubled (the hardware's 15-bit sample in a 16-bit word).
            s = Clamp16(s);
            s = (short)(s << 1);
            buffer[v.BufferOffset] = (short)s;
            if (++v.BufferOffset >= 12) v.BufferOffset = 0;
        }
    }
}
