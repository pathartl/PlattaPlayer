namespace PlattaPlayer.Visualizations.Wmp.Battery;

/// <summary>
/// <c>CPlusBlur</c>: the end-of-frame pass, which is also the ONLY fade. It is a plus-shaped five-tap
/// sum looked up in a 1276-entry table, and that table bakes in the decay:
/// <c>blur[s] = max(floor(s / 5) - 1, 1)</c>, capped at 254. This comes from
/// <c>SetupFadeTable(0, 255, 1)</c> (x64 has folded it to that one instance), then
/// <c>SetupBlurTable(true)</c>.
///
/// Edges: the walk is flat, so a pixel's left/right neighbours at the row ends are the adjacent row's
/// last/first pixels. Up/down wrap top to bottom. The two corner pixels the code special-cases take
/// their horizontal neighbour from the SAME row instead. Derivation: <c>Code\battery\02_pipeline.md</c> §4.
/// </summary>
public static class PlusBlur
{
    /// <summary>The normal-frame lookup table, indexed by the five-tap sum 0..1275.</summary>
    public static readonly byte[] Table = BuildTable();

    private static byte[] BuildTable()
    {
        // SetupFadeTable(0, 255, 1): fade[i] = clamp(i - 1, 1, 255).
        var fade = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            var v = i - 1;
            fade[i] = (byte)(v < 1 ? 1 : v < 255 ? v : 255);
        }

        // SetupBlurTable(true).
        var blur = new byte[0x500];
        for (var s = 0; s < 0x500; s++)
        {
            var v = fade[s / 5];
            if (v == 0) v = 1;
            else if (v == 255) v = 254;
            blur[s] = v;
        }
        return blur;
    }

    /// <summary><c>Perform</c> (<c>0x180412250</c>): reads <paramref name="s"/>, writes every pixel of
    /// <paramref name="d"/> exactly once. The caller swaps the surfaces first.</summary>
    public static void Perform(byte[] s, byte[] d, int w, int h)
    {
        var t = Table;
        var last = (h - 1) * w;

        for (var p = w; p < last; p++)
            d[p] = t[s[p - w] + s[p - 1] + s[p] + s[p + 1] + s[p + w]];

        d[0] = t[s[1] + s[w] + s[last] + s[w - 1] + s[0]];
        for (var x = 1; x < w; x++)
            d[x] = t[s[x + 1] + s[x + w] + s[x + last] + s[x - 1] + s[x]];

        for (var p = last; p < h * w - 1; p++)
            d[p] = t[s[p - w] + s[p] + s[p - last] + s[p + 1] + s[p - 1]];

        var q = h * w - 1;
        d[q] = t[s[q] + s[q - 1] + s[q - (w - 1)] + s[q - last] + s[q - w]];
    }
}
