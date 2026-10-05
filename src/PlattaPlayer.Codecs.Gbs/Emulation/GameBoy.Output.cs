namespace PlattaPlayer.Codecs.Gbs.Emulation;

/// <summary>How the output's DC offset is removed (SameBoy's GB_highpass_mode_t).</summary>
internal enum HighpassMode
{
    /// <summary>No filter: the DC offset of the DACs stays in.</summary>
    Off,

    /// <summary>A high-pass filter like the console's own output capacitor (SameBoy's default).</summary>
    Accurate,

    /// <summary>Removes the DC offset without filtering the waveform.</summary>
    RemoveDcOffset,
}

// SameBoy's audio output path (apu.c's render, band_limited_*): each channel's level changes are fed into a
// band-limited step synthesiser at sub-sample phase, read out at the output rate, scaled by a model of the DAC
// fading in and out, mixed, and high-pass filtered. The integer and floating-point arithmetic, including its
// wrap-arounds and truncations, follows SameBoy's exactly, so the output matches it sample for sample.
internal sealed partial class GameBoy
{
    private const int BandLimitedWidth = 64;
    private const int BandLimitedPhases = 256;
    private const int BandLimitedOne = 0x10000;  // fixed-point 1
    private const int BandLimitedBufferLength = BandLimitedWidth * 2;
    private const int QuickMultiplyCount = 64;
    private const int ChannelStep = 0xFF0 / 0xF / 8;  // the output of one volume step at volume 1
    private const double DacDecaySpeed = 20000, DacAttackSpeed = 20000;

    /// <summary>SameBoy's band-limited step table: for each of 256 sub-sample phases, 64 taps of a
    /// Blackman-windowed sinc step (cut off at 15/16 of Nyquist) in 16.16 fixed point, summing to exactly 1.</summary>
    internal static readonly int[] BandLimitedSteps = BuildBandLimitedSteps();

    private uint _sampleRate;
    private uint _sampleCycles;  // counts up by the sample rate until it reaches the clock rate
    private uint _maxCyclesPerSample = 0x400;
    private uint _cyclesSinceRender;
    private uint _sampleFraction;  // 4.28 fixed point, in samples
    private uint[] _quickFractionMultiply = new uint[QuickMultiplyCount];
    private double[] _dacDischarge = new double[4];
    private HighpassMode _highpassMode = HighpassMode.Accurate;
    private double _highpassRate;
    private double _highpassDiffLeft, _highpassDiffRight;

    // Per channel: the step ring buffers (left and right), the running output, read position, last input and
    // output, and the silence detector's counter.
    private int[] _blLeft = new int[4 * BandLimitedBufferLength];
    private int[] _blRight = new int[4 * BandLimitedBufferLength];
    private int[] _blOutput = new int[4 * 2];
    private byte[] _blPos = new byte[4];
    private short[] _blInput = new short[4 * 2];
    private short[] _blLastOutput = new short[4 * 2];
    private uint[] _blSilence = new uint[4];

    // Rendered frames not yet handed out: interleaved left/right.
    private short[] _output = new short[2048];
    private int _outputCount;

    internal HighpassMode Highpass
    {
        get => _highpassMode;
        set => _highpassMode = value;
    }

    private void CloneOutput(GameBoy source)
    {
        _quickFractionMultiply = (uint[])source._quickFractionMultiply.Clone();
        _dacDischarge = (double[])source._dacDischarge.Clone();
        _blLeft = (int[])source._blLeft.Clone();
        _blRight = (int[])source._blRight.Clone();
        _blOutput = (int[])source._blOutput.Clone();
        _blPos = (byte[])source._blPos.Clone();
        _blInput = (short[])source._blInput.Clone();
        _blLastOutput = (short[])source._blLastOutput.Clone();
        _blSilence = (uint[])source._blSilence.Clone();
        _output = (short[])source._output.Clone();
    }

    private void SetSampleRate(int sampleRate)
    {
        _sampleRate = (uint)sampleRate;
        _highpassRate = Math.Pow(0.999958, ClockRate / (double)sampleRate);
        _maxCyclesPerSample = (uint)Math.Ceiling(ClockRate / 2.0 / sampleRate);
        _quickFractionMultiply[0] = (uint)Math.Round(sampleRate * 2.0 / ClockRate * (1 << 28), MidpointRounding.AwayFromZero);
        for (var i = 1; i < QuickMultiplyCount; i++) _quickFractionMultiply[i] = _quickFractionMultiply[0] * (uint)(i + 1);
    }

    /// <summary>Test only: the filter coefficient derived from the sample rate.</summary>
    internal double HighpassRate => _highpassRate;

    private static int[] BuildBandLimitedSteps()
    {
        const int masterSize = BandLimitedWidth * BandLimitedPhases;
        var master = new double[masterSize];
        const double lowpass = 15.0 / 16.0;
        const double toAngle = Math.PI / BandLimitedPhases * lowpass;
        var sum = 0.0;
        for (var i = 0; i < masterSize; i++)
        {
            // Exact Blackman window.
            const double a0 = 7938 / 18608.0;
            const double a1 = 9240 / 18608.0;
            const double a2 = 1430 / 18608.0;
            var windowAngle = 2.0 * Math.PI * i / masterSize;
            var window = a0 - a1 * Math.Cos(windowAngle) + a2 * Math.Cos(2 * windowAngle);
            var angle = (i - masterSize / 2) * toAngle;
            sum += master[i] = (angle == 0 ? 1 : Math.Sin(angle) / angle) * window;
        }
        for (var i = 0; i < masterSize; i++) master[i] /= sum;

        var steps = new int[BandLimitedPhases * BandLimitedWidth];
        for (var phase = 0; phase < BandLimitedPhases; phase++)
        {
            var error = BandLimitedOne;
            for (var i = 0; i < BandLimitedWidth; i++)
            {
                var tapSum = 0.0;
                for (var j = 0; j < BandLimitedPhases; j++)
                {
                    var index = i * BandLimitedPhases - phase + j;
                    if (index >= 0) tapSum += master[index];
                }
                var current = (int)(tapSum * BandLimitedOne);
                error -= current;
                steps[phase * BandLimitedWidth + i] = current;
            }
            // Make sure the deltas sum to 1.0.
            steps[phase * BandLimitedWidth + BandLimitedWidth / 2] += error;
        }
        return steps;
    }

    private uint SampleFractionMultiply(uint multiplier)
    {
        if (multiplier == 0) return 0;
        if (multiplier < QuickMultiplyCount + 1) return _quickFractionMultiply[multiplier - 1];
        return _quickFractionMultiply[0] * multiplier;
    }

    // A channel's output level changes: record the step for the band-limited synthesiser.
    private void UpdateSample(int index, sbyte value, uint cyclesOffset)
    {
        if (value == 0 && _apu.Samples[index] == 0) return;

        if (!IsDacEnabled(index)) value = (sbyte)_apu.Samples[index];
        else _apu.Samples[index] = (byte)value;

        if (_sampleRate == 0) return;
        var rightVolume = (_io[IoNr51] & (1 << index)) != 0 ? (_io[IoNr50] & 7) + 1 : 0;
        var leftVolume = (_io[IoNr51] & (0x10 << index)) != 0 ? ((_io[IoNr50] >> 4) & 7) + 1 : 0;
        var left = (short)((0xF - value * 2) * leftVolume);
        var right = (short)((0xF - value * 2) * rightVolume);
        if (_maxCyclesPerSample == 1)
            BandLimitedUpdateUnfiltered(index, left, right, cyclesOffset);
        else
            BandLimitedUpdate(index, left, right,
                (((_sampleFraction + SampleFractionMultiply(cyclesOffset)) >> 8) * BandLimitedPhases) >> 20);
    }

    private void BandLimitedUpdate(int channel, short left, short right, uint phase)
    {
        var inputIndex = channel * 2;
        if (left == _blInput[inputIndex] && right == _blInput[inputIndex + 1]) return;
        var delay = phase / BandLimitedPhases;
        phase &= BandLimitedPhases - 1;

        var deltaLeft = left - _blInput[inputIndex];
        var deltaRight = right - _blInput[inputIndex + 1];
        _blInput[inputIndex] = left;
        _blInput[inputIndex + 1] = right;

        // The 64 taps land in the 128-entry ring at pos + delay onwards: in at most two contiguous runs.
        var steps = BandLimitedSteps.AsSpan((int)phase * BandLimitedWidth, BandLimitedWidth);
        var bufferLeft = _blLeft.AsSpan(channel * BandLimitedBufferLength, BandLimitedBufferLength);
        var bufferRight = _blRight.AsSpan(channel * BandLimitedBufferLength, BandLimitedBufferLength);
        var start = (int)((_blPos[channel] + delay) & (BandLimitedBufferLength - 1));
        var first = Math.Min(BandLimitedWidth, BandLimitedBufferLength - start);
        AddScaled(bufferLeft.Slice(start, first), steps[..first], deltaLeft);
        AddScaled(bufferRight.Slice(start, first), steps[..first], deltaRight);
        if (first < BandLimitedWidth)
        {
            AddScaled(bufferLeft[..(BandLimitedWidth - first)], steps[first..], deltaLeft);
            AddScaled(bufferRight[..(BandLimitedWidth - first)], steps[first..], deltaRight);
        }
    }

    // target[i] += steps[i] * delta (wrapping 32-bit arithmetic, as in SameBoy).
    private static void AddScaled(Span<int> target, ReadOnlySpan<int> steps, int delta)
    {
        var i = 0;
        if (System.Numerics.Vector.IsHardwareAccelerated && target.Length >= System.Numerics.Vector<int>.Count)
        {
            var factor = new System.Numerics.Vector<int>(delta);
            var width = System.Numerics.Vector<int>.Count;
            for (; i <= target.Length - width; i += width)
            {
                var sum = new System.Numerics.Vector<int>(target[i..]) + new System.Numerics.Vector<int>(steps[i..]) * factor;
                sum.CopyTo(target[i..]);
            }
        }
        for (; i < target.Length; i++) target[i] += steps[i] * delta;
    }

    private void BandLimitedUpdateUnfiltered(int channel, short left, short right, uint delay)
    {
        var inputIndex = channel * 2;
        if (left == _blInput[inputIndex] && right == _blInput[inputIndex + 1]) return;
        var deltaLeft = left - _blInput[inputIndex];
        var deltaRight = right - _blInput[inputIndex + 1];
        _blInput[inputIndex] = left;
        _blInput[inputIndex + 1] = right;
        var offset = channel * BandLimitedBufferLength + (int)((_blPos[channel] + delay) & (BandLimitedBufferLength - 1));
        _blLeft[offset] += deltaLeft * BandLimitedOne;
        _blRight[offset] += deltaRight * BandLimitedOne;
    }

    private (short Left, short Right) BandLimitedRead(int channel, uint multiplier)
    {
        var buffer = channel * BandLimitedBufferLength + _blPos[channel];
        _blOutput[channel * 2] += _blLeft[buffer];
        _blOutput[channel * 2 + 1] += _blRight[buffer];
        _blLeft[buffer] = _blRight[buffer] = 0;
        _blPos[channel] = (byte)((_blPos[channel] + 1) & (BandLimitedBufferLength - 1));

        // SameBoy multiplies the signed sum by the unsigned multiplier in unsigned arithmetic, so a negative sum
        // divides as a large unsigned number; only the low 16 bits are kept.
        var left = (short)(((uint)_blOutput[channel * 2] * multiplier) / BandLimitedOne);
        var right = (short)(((uint)_blOutput[channel * 2 + 1] * multiplier) / BandLimitedOne);

        // Mutes a channel that only plays an amplitude of 1 or 2 units for a long time, usually rounding noise
        // from a frequency above Nyquist.
        var last = channel * 2;
        if (Math.Abs(left - _blLastOutput[last]) > 4 || Math.Abs(right - _blLastOutput[last + 1]) > 4)
        {
            _blSilence[channel] = 0;
            _blLastOutput[last] = left;
            _blLastOutput[last + 1] = right;
            return (left, right);
        }
        if (_blSilence[channel] == 4000) return (_blLastOutput[last], _blLastOutput[last + 1]);
        _blSilence[channel]++;
        return (left, right);
    }

    private static double Smooth(double x) => 3 * x * x - 2 * x * x * x;

    // Produces one output frame.
    private void Render()
    {
        short outLeft = 0, outRight = 0;
        for (var i = 0; i < 4; i++)
        {
            double multiplier = ChannelStep;
            if (!IsDacEnabled(i))
            {
                _dacDischarge[i] -= DacDecaySpeed / _sampleRate;
                if (_dacDischarge[i] < 0)
                {
                    multiplier = 0;
                    _dacDischarge[i] = 0;
                }
                else
                {
                    multiplier *= Smooth(_dacDischarge[i]);
                }
            }
            else
            {
                _dacDischarge[i] += DacAttackSpeed / _sampleRate;
                if (_dacDischarge[i] > 1) _dacDischarge[i] = 1;
                else multiplier *= Smooth(_dacDischarge[i]);
            }

            var (left, right) = BandLimitedRead(i, (uint)multiplier);
            outLeft += left;
            outRight += right;
        }
        _cyclesSinceRender = 0;
        if (_sampleFraction < 1 << 28) _sampleFraction = 0;
        else _sampleFraction -= 1 << 28;

        short filteredLeft = outLeft, filteredRight = outRight;
        if (_highpassMode != HighpassMode.Off)
        {
            filteredLeft = (short)(outLeft - (short)(int)_highpassDiffLeft);
            filteredRight = (short)(outRight - (short)(int)_highpassDiffRight);
        }

        switch (_highpassMode)
        {
            case HighpassMode.Off:
                _highpassDiffLeft = _highpassDiffRight = 0;
                break;
            case HighpassMode.Accurate:
                _highpassDiffLeft = outLeft - (outLeft - _highpassDiffLeft) * _highpassRate;
                _highpassDiffRight = outRight - (outRight - _highpassDiffRight) * _highpassRate;
                break;
            case HighpassMode.RemoveDcOffset:
            {
                var mask = (uint)_io[IoNr51];
                uint leftVolume = 0, rightVolume = 0;
                for (var i = 4; i-- > 0;)
                {
                    if (IsDacEnabled(i))
                    {
                        if ((mask & 1) != 0) leftVolume += (uint)(((_io[IoNr50] & 7) + 1) * ChannelStep * 0xF);
                        if ((mask & 0x10) != 0) rightVolume += (uint)((((_io[IoNr50] >> 4) & 7) + 1) * ChannelStep * 0xF);
                    }
                    mask >>= 1;
                }
                _highpassDiffLeft = leftVolume * (1 - _highpassRate) + _highpassDiffLeft * _highpassRate;
                _highpassDiffRight = rightVolume * (1 - _highpassRate) + _highpassDiffRight * _highpassRate;
                break;
            }
        }

        if (_outputCount + 2 > _output.Length) Array.Resize(ref _output, _output.Length * 2);
        _output[_outputCount++] = filteredLeft;
        _output[_outputCount++] = filteredRight;
    }
}
