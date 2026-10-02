namespace PlattaPlayer.Visualizations.PSP.Common;

/// <summary>
/// A small decoder for baseline (sequential, Huffman, 8-bit) JPEG, standing in for paf::Image's JPEG codec.
/// The only inputs are the two images embedded in visualizer_plugin.prx, both baseline JFIF with 4:2:0
/// chroma, so progressive and arithmetic-coded files are rejected. Chroma is upsampled by replication and
/// converted with the JFIF YCbCr equations; output is paf's ToBuffer layout (R in the low byte, pitch =
/// width, alpha 0xff).
/// </summary>
public static class BaselineJpeg
{
    private static readonly byte[] ZigZag =
    [
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21,
        28, 35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61,
        54, 47, 55, 62, 63,
    ];

    private static readonly float[] IdctTable = BuildIdctTable();

    /// <summary>Decodes <paramref name="data"/>, or returns null if it is not a baseline JPEG this can read.</summary>
    public static PafImage? Decode(ReadOnlyMemory<byte> data)
    {
        try
        {
            return new Decoder(data.Span.ToArray()).Run();
        }
        catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException or ArgumentException)
        {
            return null;
        }
    }

    // cos((2x+1) u pi / 16) * C(u), indexed [x * 8 + u]
    private static float[] BuildIdctTable()
    {
        var t = new float[64];
        for (var x = 0; x < 8; x++)
        for (var u = 0; u < 8; u++)
        {
            var c = u == 0 ? 1.0 / Math.Sqrt(2) : 1.0;
            t[x * 8 + u] = (float)(c * Math.Cos((2 * x + 1) * u * Math.PI / 16));
        }
        return t;
    }

    private sealed class Huffman
    {
        // code -> value lookup by (length, code)
        public readonly int[] MaxCode = new int[18];
        public readonly int[] ValPtr = new int[17];
        public readonly int[] MinCode = new int[17];
        public byte[] Values = [];

        public void Build(ReadOnlySpan<byte> counts, byte[] values)
        {
            Values = values;
            int code = 0, k = 0;
            for (var len = 1; len <= 16; len++)
            {
                ValPtr[len] = k;
                MinCode[len] = code;
                code += counts[len - 1];
                k += counts[len - 1];
                MaxCode[len] = counts[len - 1] == 0 ? -1 : code - 1;
                code <<= 1;
            }
            MaxCode[17] = int.MaxValue;
        }
    }

    private sealed class Component
    {
        public int Id, H, V, Tq, Td, Ta, Pred;
        public int BlocksW, BlocksH;
        public byte[] Plane = [];
    }

    private sealed class Decoder(byte[] d)
    {
        private readonly int[][] _quant = new int[4][];
        private readonly Huffman?[] _dc = new Huffman?[4];
        private readonly Huffman?[] _ac = new Huffman?[4];
        private Component[] _components = [];
        private int _width, _height, _hMax, _vMax, _restart;
        private int _pos, _bitBuf, _bitCount;

        public PafImage Run()
        {
            if (d.Length < 4 || d[0] != 0xFF || d[1] != 0xD8) throw new InvalidDataException("not a JPEG");
            _pos = 2;
            while (true)
            {
                var marker = NextMarker();
                if (marker == 0xD9) throw new InvalidDataException("no scan");
                var len = d[_pos] << 8 | d[_pos + 1];
                var seg = _pos + 2;
                var end = _pos + len;
                switch (marker)
                {
                    case 0xDB: ReadQuant(seg, end); break;
                    case 0xC4: ReadHuffman(seg, end); break;
                    case 0xC0: case 0xC1: ReadFrame(seg); break;
                    case 0xDD: _restart = d[seg] << 8 | d[seg + 1]; break;
                    case 0xDA:
                        ReadScan(seg);
                        _pos = end;
                        DecodeScan();
                        return Output();
                    default:
                        if (marker is >= 0xC2 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                            throw new InvalidDataException("only baseline JPEG is supported");
                        break;
                }
                _pos = end;
            }
        }

        private int NextMarker()
        {
            while (d[_pos] != 0xFF) _pos++;
            while (d[_pos] == 0xFF) _pos++;
            return d[_pos++];
        }

        private void ReadQuant(int p, int end)
        {
            while (p < end)
            {
                int pq = d[p] >> 4, tq = d[p] & 15;
                p++;
                var q = new int[64];
                for (var i = 0; i < 64; i++)
                {
                    q[ZigZag[i]] = pq == 0 ? d[p] : d[p] << 8 | d[p + 1];
                    p += pq == 0 ? 1 : 2;
                }
                _quant[tq] = q;
            }
        }

        private void ReadHuffman(int p, int end)
        {
            while (p < end)
            {
                int tc = d[p] >> 4, th = d[p] & 15;
                var counts = d.AsSpan(p + 1, 16);
                var total = 0;
                foreach (var c in counts) total += c;
                var values = d.AsSpan(p + 17, total).ToArray();
                var h = new Huffman();
                h.Build(counts, values);
                if (tc == 0) _dc[th] = h; else _ac[th] = h;
                p += 17 + total;
            }
        }

        private void ReadFrame(int p)
        {
            if (d[p] != 8) throw new InvalidDataException("only 8-bit JPEG is supported");
            _height = d[p + 1] << 8 | d[p + 2];
            _width = d[p + 3] << 8 | d[p + 4];
            var n = d[p + 5];
            _components = new Component[n];
            for (var i = 0; i < n; i++)
            {
                var q = p + 6 + i * 3;
                _components[i] = new Component { Id = d[q], H = d[q + 1] >> 4, V = d[q + 1] & 15, Tq = d[q + 2] };
            }
            _hMax = _components.Max(c => c.H);
            _vMax = _components.Max(c => c.V);
            var mcuW = (_width + 8 * _hMax - 1) / (8 * _hMax);
            var mcuH = (_height + 8 * _vMax - 1) / (8 * _vMax);
            foreach (var c in _components)
            {
                c.BlocksW = mcuW * c.H;
                c.BlocksH = mcuH * c.V;
                c.Plane = new byte[c.BlocksW * 8 * c.BlocksH * 8];
            }
        }

        private void ReadScan(int p)
        {
            var n = d[p];
            if (n != _components.Length) throw new InvalidDataException("only interleaved scans are supported");
            for (var i = 0; i < n; i++)
            {
                int id = d[p + 1 + i * 2], t = d[p + 2 + i * 2];
                var c = _components.First(x => x.Id == id);
                c.Td = t >> 4;
                c.Ta = t & 15;
            }
        }

        private void DecodeScan()
        {
            var mcuW = (_width + 8 * _hMax - 1) / (8 * _hMax);
            var mcuH = (_height + 8 * _vMax - 1) / (8 * _vMax);
            Span<int> coef = stackalloc int[64];
            var mcus = 0;
            for (var my = 0; my < mcuH; my++)
            for (var mx = 0; mx < mcuW; mx++)
            {
                if (_restart != 0 && mcus > 0 && mcus % _restart == 0) Restart();
                mcus++;
                foreach (var c in _components)
                {
                    for (var by = 0; by < c.V; by++)
                    for (var bx = 0; bx < c.H; bx++)
                    {
                        DecodeBlock(c, coef);
                        StoreBlock(c, coef, (mx * c.H + bx) * 8, (my * c.V + by) * 8);
                    }
                }
            }
        }

        private void Restart()
        {
            _bitBuf = _bitCount = 0;
            // skip to and over the RSTn marker
            while (_pos + 1 < d.Length && !(d[_pos] == 0xFF && d[_pos + 1] is >= 0xD0 and <= 0xD7)) _pos++;
            _pos += 2;
            foreach (var c in _components) c.Pred = 0;
        }

        private void DecodeBlock(Component c, Span<int> coef)
        {
            coef.Clear();
            var q = _quant[c.Tq] ?? throw new InvalidDataException("missing quantisation table");
            var dc = _dc[c.Td] ?? throw new InvalidDataException("missing DC table");
            var ac = _ac[c.Ta] ?? throw new InvalidDataException("missing AC table");

            var t = DecodeHuffman(dc);
            var diff = t == 0 ? 0 : Extend(ReadBits(t), t);
            c.Pred += diff;
            coef[0] = c.Pred * q[0];
            for (var k = 1; k < 64;)
            {
                var rs = DecodeHuffman(ac);
                int r = rs >> 4, s = rs & 15;
                if (s == 0)
                {
                    if (r != 15) break; // EOB
                    k += 16;
                    continue;
                }
                k += r;
                if (k > 63) break;
                coef[ZigZag[k]] = Extend(ReadBits(s), s) * q[ZigZag[k]];
                k++;
            }
        }

        private void StoreBlock(Component c, ReadOnlySpan<int> coef, int x0, int y0)
        {
            Span<float> tmp = stackalloc float[64];
            // rows: tmp[y][x] = sum_u coef[y][u] * T[x][u]
            for (var y = 0; y < 8; y++)
            for (var x = 0; x < 8; x++)
            {
                float s = 0;
                for (var u = 0; u < 8; u++) s += coef[y * 8 + u] * IdctTable[x * 8 + u];
                tmp[y * 8 + x] = s;
            }
            var stride = c.BlocksW * 8;
            for (var x = 0; x < 8; x++)
            for (var y = 0; y < 8; y++)
            {
                float s = 0;
                for (var v = 0; v < 8; v++) s += tmp[v * 8 + x] * IdctTable[y * 8 + v];
                var value = (int)MathF.Round(s / 4f + 128f);
                c.Plane[(y0 + y) * stride + x0 + x] = (byte)Math.Clamp(value, 0, 255);
            }
        }

        private int DecodeHuffman(Huffman h)
        {
            var code = 0;
            for (var len = 1; len <= 16; len++)
            {
                code = code << 1 | ReadBit();
                if (code <= h.MaxCode[len]) return h.Values[h.ValPtr[len] + code - h.MinCode[len]];
            }
            throw new InvalidDataException("bad Huffman code");
        }

        private int ReadBits(int n)
        {
            var v = 0;
            for (var i = 0; i < n; i++) v = v << 1 | ReadBit();
            return v;
        }

        private int ReadBit()
        {
            if (_bitCount == 0)
            {
                if (_pos >= d.Length) throw new InvalidDataException("truncated scan");
                var b = d[_pos++];
                if (b == 0xFF)
                {
                    var next = d[_pos];
                    if (next == 0x00) _pos++;
                    else if (next is >= 0xD0 and <= 0xD7) { /* restart handled by the caller */ }
                    else b = 0; // a marker inside the scan: pad with zeros
                }
                _bitBuf = b;
                _bitCount = 8;
            }
            _bitCount--;
            return _bitBuf >> _bitCount & 1;
        }

        private static int Extend(int v, int t) => v < 1 << (t - 1) ? v - (1 << t) + 1 : v;

        private PafImage Output()
        {
            var pixels = new uint[_width * _height];
            for (var y = 0; y < _height; y++)
            for (var x = 0; x < _width; x++)
            {
                if (_components.Length == 1)
                {
                    uint g = Sample(_components[0], x, y);
                    pixels[y * _width + x] = g | g << 8 | g << 16 | 0xff000000u;
                    continue;
                }
                float yy = Sample(_components[0], x, y);
                float cb = Sample(_components[1], x, y) - 128f;
                float cr = Sample(_components[2], x, y) - 128f;
                var r = Clamp(yy + 1.402f * cr);
                var g2 = Clamp(yy - 0.344136f * cb - 0.714136f * cr);
                var b = Clamp(yy + 1.772f * cb);
                pixels[y * _width + x] = r | g2 << 8 | b << 16 | 0xff000000u;
            }
            return new PafImage(_width, _height, pixels);
        }

        private byte Sample(Component c, int x, int y)
        {
            var sx = x * c.H / _hMax;
            var sy = y * c.V / _vMax;
            return c.Plane[sy * c.BlocksW * 8 + sx];
        }

        private static uint Clamp(float v) => (uint)Math.Clamp((int)MathF.Round(v), 0, 255);
    }
}
