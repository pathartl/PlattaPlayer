namespace PlattaPlayer.Visualizations.Wmp.BarsAndWaves;

/// <summary>
/// Maps the 1024 TimedLevel frequency bytes onto logarithmically spaced bars, exactly as wmp.dll's
/// <c>FUN_18041ce44</c> (band edges) and <c>FUN_18041cf78</c> (accumulation) do.
/// </summary>
internal sealed class SpectrumBands
{
    /// <summary>
    /// Hz per FFT bin, taken as the literal from the binary. It is NOT 44100/2048 (= 21.5332): the
    /// constant compiled in is 21.513671875, i.e. 44060/2048. The band edges meanwhile top out at
    /// 20 * 1102.5 = 22050, which implies 44.1 kHz — so the original is internally inconsistent.
    /// Reproducing the literal is what matches the shipped effect; "correcting" it shifts every band.
    /// </summary>
    public const float BinHz = 21.513672f;

    private float[] _edges = [];
    private int _tableKey = -1;

    public int BandCount { get; private set; }

    /// <summary>
    /// Rebuilds the edge table when needed.
    ///
    /// The staleness check reproduces a quirk in the original: it compares the stored key against
    /// <paramref name="maxBars"/> but stores <paramref name="bars"/>. When the two differ the table is
    /// rebuilt every frame (harmless), and when they are equal a later change in bar count can leave a
    /// stale table in place. Both behaviours are visible on resize, so they are kept rather than tidied.
    /// </summary>
    public void Ensure(int bars, int maxBars)
    {
        if (_tableKey == maxBars) return;
        Build(bars);
        _tableKey = bars;
    }

    private void Build(int bars)
    {
        BandCount = bars;
        if (_edges.Length < bars + 1) _edges = new float[bars + 1];

        // ratio = exp(log(1102.5) / bars) in double, then applied as a repeated FLOAT multiply — not
        // pow() per index. The accumulation order is part of the result.
        var ratio = (float)Math.Exp(Math.Log(1102.5) / bars);
        var edge = 20.0f;
        for (var i = 0; i <= bars; i++)
        {
            _edges[i] = edge;
            edge *= ratio;
        }
    }

    /// <summary>
    /// Accumulates one channel's frequency bytes into <paramref name="destination"/> bar levels.
    ///
    /// This is a single streaming pass over the FFT bins, not an independent sum per band: the current
    /// bin index and the unconsumed fraction of that bin carry across band boundaries, so a bin
    /// straddling two bands contributes proportionally to each. Energies are summed in the expanded
    /// (linear) domain and compressed back to a byte at the end of each band.
    /// </summary>
    public void Accumulate(ReadOnlySpan<byte> frequency, Span<byte> destination, int bars)
    {
        var remainingInBin = 1.0f;
        var bin = 0;
        var energy = BarsLevelTables.Expand[frequency[0]];

        for (var band = 0; band < bars; band++)
        {
            if (bin > 0x3FF) return;

            var sum = 0.0f;
            var width = (_edges[band + 1] - _edges[band]) / BinHz;

            while (width > 0.0f)
            {
                var take = Math.Min(remainingInBin, width);
                remainingInBin -= take;
                width -= take;
                sum += take * energy;

                if (remainingInBin <= 0.0f)
                {
                    bin++;
                    if (bin > 0x3FF) break;
                    remainingInBin = 1.0f;
                    energy = BarsLevelTables.Expand[frequency[bin]];
                }
            }

            destination[band] = Compress(sum);
        }
    }

    /// <summary>
    /// The table-driven log from <c>FUN_18041cf78</c>: the exponent byte of the IEEE-754 value supplies
    /// the integer part and the top mantissa bits the fraction, giving 255/8 * log10(energy) clamped to
    /// a byte. Both tables were extracted from wmp.dll and verified to invert
    /// <see cref="BarsLevelTables.Expand"/> exactly.
    /// </summary>
    private static byte Compress(float energy)
    {
        var bits = BitConverter.SingleToUInt32Bits(energy);
        var v = BarsLevelTables.Mantissa[(int)((bits >> 12) & 0x7FF)]
              + BarsLevelTables.Exponent[(int)((bits >> 23) & 0xFF)];
        return v >= 0x100 ? (byte)0xFF : v < 0 ? (byte)0 : (byte)v;
    }
}
