namespace PlattaPlayer.Visualizations.PSP.Firmware;

/// <summary>
/// Decoder for Sony's "LZR" (RLZ) range-coded LZ format used by RCO files and "2RLZ" modules. A port of
/// LZRDecompress from libLZR 0.11 by BenHur (http://www.psp-programming.com/benhur, CC BY-SA 3.0), as
/// shipped with pspdecrypt and rcomage; cross-checked with the project's tools/rco_rlz2zlib.py.
/// </summary>
internal static class Lzr
{
    /// <summary>
    /// Decompresses an LZR stream (starting with its type byte, i.e. after any "2RLZ" magic) into at most
    /// <paramref name="capacity"/> bytes.
    /// </summary>
    public static byte[] Decompress(ReadOnlySpan<byte> input, int capacity)
    {
        var d = new Decoder(input);
        var output = new byte[capacity];
        int n = d.Run(output);
        return n == capacity ? output : output.AsSpan(0, n).ToArray();
    }

    private ref struct Decoder
    {
        private readonly ReadOnlySpan<byte> _in;
        private int _pos;
        private uint _mask;
        private uint _buffer;
        private readonly byte[] _buf;

        public Decoder(ReadOnlySpan<byte> input)
        {
            _in = input;
            _buf = new byte[2800];
            _mask = 0xFFFFFFFF;
            _buffer = 0;
            _pos = 0;
        }

        private byte NextIn() => _pos < _in.Length ? _in[_pos++] : (byte)0;

        /// <summary>LZRFillBuffer with test_mask == mask.</summary>
        private void Fill()
        {
            if (_mask <= 0x00FFFFFFu)
            {
                _buffer = (_buffer << 8) + NextIn();
                _mask <<= 8;
            }
        }

        /// <summary>LZRNextBit with test_mask == mask; <paramref name="number"/> accumulates the bit.</summary>
        private int NextBit(int p, ref int number)
        {
            Fill();
            uint value = (_mask >> 8) * _buf[p];
            _buf[p] -= (byte)(_buf[p] >> 3);
            number <<= 1;
            if (_buffer < value)
            {
                _mask = value;
                _buf[p] += 31;
                number++;
                return 1;
            }
            _buffer -= value;
            _mask -= value;
            return 0;
        }

        private int NextBit(int p)
        {
            int dummy = 0;
            return NextBit(p, ref dummy);
        }

        /// <summary>LZRNextBit with a separate test mask (sequence-length probing).</summary>
        private int NextBitTest(int p, ref uint testMask)
        {
            if (testMask <= 0x00FFFFFFu)
            {
                _buffer = (_buffer << 8) + NextIn();
                _mask = testMask << 8;
            }
            uint value = (_mask >> 8) * _buf[p];
            testMask = value;
            _buf[p] -= (byte)(_buf[p] >> 3);
            if (_buffer < value)
            {
                _mask = value;
                _buf[p] += 31;
                return 1;
            }
            _buffer -= value;
            _mask -= value;
            return 0;
        }

        /// <summary>LZRGetNumber</summary>
        private int GetNumber(int nBits, int p, int inc, out int flag)
        {
            int number = 1;
            if (nBits >= 3)
            {
                NextBit(p + 3 * inc, ref number);
                if (nBits >= 4)
                {
                    NextBit(p + 3 * inc, ref number);
                    if (nBits >= 5)
                    {
                        Fill();
                        for (; nBits >= 5; nBits--)
                        {
                            number <<= 1;
                            _mask >>= 1;
                            if (_buffer < _mask) number++;
                            else _buffer -= _mask;
                        }
                    }
                }
            }
            flag = NextBit(p, ref number);
            if (nBits >= 1)
            {
                NextBit(p + inc, ref number);
                if (nBits >= 2) NextBit(p + 2 * inc, ref number);
            }
            return number;
        }

        public int Run(byte[] output)
        {
            if (_in.Length < 5) throw new InvalidDataException("LZR stream is truncated.");
            sbyte type = (sbyte)_in[0];
            _buffer = (uint)(_in[1] << 24 | _in[2] << 16 | _in[3] << 8 | _in[4]);
            _pos = 5;
            int outPos = 0;

            if (type < 0)
            {
                // stored without compression
                if (_buffer > (uint)output.Length || _pos + _buffer > (uint)_in.Length)
                    throw new InvalidDataException("LZR stored block does not fit.");
                _in.Slice(_pos, (int)_buffer).CopyTo(output);
                return (int)_buffer;
            }

            Array.Fill(_buf, (byte)0x80);
            int bufOff = 0;
            byte lastChar = 0;

            while (true)
            {
                int p1 = bufOff + 2488;
                if (NextBit(p1) == 0)
                {
                    // single new char
                    if (bufOff > 0) bufOff--;
                    if (outPos == output.Length) throw new InvalidDataException("LZR output overflow.");
                    p1 = (((((outPos & 7) << 8) + lastChar) >> type) & 7) * 0xFF - 1;
                    int j = 1;
                    while (j <= 0xFF) NextBit(p1 + j, ref j);
                    output[outPos++] = (byte)j;
                }
                else
                {
                    // sequence of chars that exists in the output: number of bits of the length
                    uint testMask = _mask;
                    int nBits = -1;
                    int flag;
                    do
                    {
                        p1 += 8;
                        flag = NextBitTest(p1, ref testMask);
                        nBits += flag;
                    } while (flag != 0 && nBits < 6);

                    int p2 = nBits + 2033;
                    int j = 64;
                    int seqLen;
                    if (flag != 0 || nBits >= 0)
                    {
                        p1 = (nBits << 5) + (((outPos << nBits) & 3) << 3) + bufOff + 2552;
                        seqLen = GetNumber(nBits, p1, 8, out flag);
                        if (seqLen == 0xFF) return outPos; // end of stream
                        if (flag != 0 || nBits > 0)
                        {
                            p2 += 56;
                            j = 352;
                        }
                    }
                    else
                    {
                        seqLen = 1;
                    }

                    // number of bits of the offset
                    int i = 1;
                    do
                    {
                        nBits = (i << 4) - j;
                        flag = NextBit(p2 + (i << 3), ref i);
                    } while (nBits < 0);

                    int seqOff;
                    if (flag != 0 || nBits > 0)
                    {
                        if (flag == 0) nBits -= 8;
                        seqOff = GetNumber(nBits / 8, nBits + 2344, 1, out flag);
                    }
                    else
                    {
                        seqOff = 1;
                    }

                    int src = outPos - seqOff;
                    if (src < 0) throw new InvalidDataException("LZR back-reference before start of output.");
                    int seqEnd = outPos + seqLen + 1;
                    if (seqEnd > output.Length) throw new InvalidDataException("LZR output overflow.");
                    bufOff = ((seqEnd + 1) & 1) + 6;
                    while (outPos < seqEnd) output[outPos++] = output[src++];
                }
                lastChar = output[outPos - 1];
            }
        }
    }
}
