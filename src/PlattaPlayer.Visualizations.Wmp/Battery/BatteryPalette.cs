namespace PlattaPlayer.Visualizations.Wmp.Battery;

/// <summary>
/// The palette half of <c>CRenderData</c>: three 256-entry PALETTEENTRY blocks (source, current,
/// target), and the transition machine that walks current from source to target. Entries are stored the
/// way the DWORD reads in memory: <c>R | G &lt;&lt; 8 | B &lt;&lt; 16 | flags &lt;&lt; 24</c>.
///
/// Randomization keeps making new gradients forever: idle <c>rand() % 600</c> frames, then transition
/// over <c>rand() % 250</c>. A locked preset fades its stored palette in over 25 frames and then freezes.
/// Every transition stops one step short (t = (d-1)/d), so a locked palette is shown as
/// lerp(previous, stored, 0.96), not as the stored bytes. All of this is single precision with
/// truncation. Derivation: <c>Code\battery\01_control.md</c> §2.6–2.11.
/// </summary>
public sealed class BatteryPalette
{
    /// <summary>+0xdc.</summary>
    public uint[] Source { get; } = new uint[256];

    /// <summary>+0x4dc: what DirectDraw is given.</summary>
    public uint[] Current { get; } = new uint[256];

    /// <summary>+0x8dc.</summary>
    public uint[] Target { get; } = new uint[256];

    /// <summary>+0xc8.</summary>
    public bool Locked { get; set; }

    /// <summary>+0xc9: set by the constructor and never cleared.</summary>
    public bool RandomTransitions { get; set; } = true;

    /// <summary>+0xcc: frames until the next RandomTransition.</summary>
    public int Countdown { get; private set; }

    /// <summary>+0xd0: start the 25-frame locked fade on the next frame.</summary>
    public bool PendingLocked { get; set; }

    public bool TransitionActive { get; private set; }

    /// <summary>+0xd2: lets a locked palette fade in.</summary>
    public bool Forced { get; private set; }

    public int TransTotal { get; private set; }

    public int TransRemaining { get; private set; }

    /// <summary><c>InterpolateRGB2</c> (<c>0x180411c58</c>): per channel
    /// <c>c1 + (int)((float)(c2 - c1) * t)</c>, truncated toward zero. Byte 3 comes out 0.</summary>
    public static uint InterpolateRgb2(uint c1, uint c2, float t)
    {
        uint Channel(int shift)
        {
            int a = (byte)(c1 >> shift), b = (byte)(c2 >> shift);
            return (byte)(a + BatteryMath.TruncF((float)(b - a) * t));
        }
        return Channel(0) | Channel(8) << 8 | Channel(16) << 16;
    }

    private static void SetRgb(uint[] palette, int i, uint c) =>
        palette[i] = (palette[i] & 0xFF000000u) | (c & 0x00FFFFFFu);

    /// <summary><c>SetColorTransition</c> (<c>0x180413138</c>): a linear ramp into target, with
    /// t = k / count, so the last entry stops short of c2. peFlags are left alone.</summary>
    public void SetColorTransition(int start, int end, uint c1, uint c2)
    {
        if (start > end) return;
        var cnt = end - start + 1;
        var fc = (float)cnt;
        for (var k = 0; k < cnt; k++) SetRgb(Target, start + k, InterpolateRgb2(c1, c2, k / fc));
    }

    /// <summary><c>StartPaletteTransition</c> (<c>0x180413838</c>). Segments share their boundary entry,
    /// and the later segment wins.</summary>
    public void StartPaletteTransition(int duration, int n, ReadOnlySpan<uint> colors)
    {
        if (n <= 1) return;
        var step = 256 / (n - 1);
        var s = 0;
        for (var i = 0; i < n - 1; i++)
        {
            var e = i == n - 2 ? 255 : s + step;
            SetColorTransition(s, e, colors[i], colors[i + 1]);
            s = e;
        }
        Current.CopyTo(Source, 0);
        TransTotal = TransRemaining = duration;
        TransitionActive = true;
    }

    /// <summary><c>RandomTransition</c> (<c>0x180412790</c>). It draws 10 + 3(n-2) times. x64 assigns each
    /// colour's three draws to G, B, R, which differs from Win7's B, G, R.</summary>
    public void RandomTransition(CrtRand rand)
    {
        var r0 = rand.Next();
        var r1 = rand.Next();
        var r2 = rand.Next();
        var n = r2 % (r0 % 10 != 0 ? 4 : 10) + 2;
        Span<uint> col = stackalloc uint[12];
        Span<int> sum = stackalloc int[12];

        uint Draw(int mod, bool invert)
        {
            uint g = (byte)(rand.Next() % mod), b = (byte)(rand.Next() % mod), r = (byte)(rand.Next() % mod);
            if (invert) { g = (byte)~g; b = (byte)~b; r = (byte)~r; }
            return r | g << 8 | b << 16;
        }

        col[0] = Draw(32, false);  // dark
        col[1] = Draw(32, true);   // bright
        for (var i = 2; i < n; i++) col[i] = Draw(256, false);
        for (var i = 0; i < n; i++) sum[i] = (int)(col[i] & 0xFF) + (int)((col[i] >> 8) & 0xFF) + (int)((col[i] >> 16) & 0xFF);

        var descending = r1 % 10 == 0;
        for (var i = 0; i < n - 1; i++)
        for (var j = i + 1; j < n; j++)
        {
            var swap = descending ? sum[j] > sum[i] : sum[j] < sum[i];
            if (!swap) continue;
            (sum[i], sum[j]) = (sum[j], sum[i]);
            (col[i], col[j]) = (col[j], col[i]);
        }

        StartPaletteTransition(rand.Next() % 250, n, col[..n]);
    }

    /// <summary><c>PerformTransition</c> (<c>0x180412510</c>). True when the palette changed this frame
    /// and must be uploaded.</summary>
    public bool PerformTransition(CrtRand rand)
    {
        if (PendingLocked)
        {
            PendingLocked = false;
            Forced = true;
            Current.CopyTo(Source, 0);
            TransTotal = TransRemaining = 25;
            TransitionActive = true;
        }

        if (Locked && !Forced) return false;

        if (TransitionActive)
        {
            var t = TransTotal > 0 ? (float)(TransTotal - TransRemaining) / TransTotal : 1.0f;
            for (var i = 0; i < 256; i++) SetRgb(Current, i, InterpolateRgb2(Source[i], Target[i], t));
            if (--TransRemaining < 1)
            {
                TransitionActive = false;
                Forced = false;
                if (RandomTransitions) Countdown = rand.Next() % 600;
            }
            return true;
        }

        if (RandomTransitions && --Countdown < 1) RandomTransition(rand);
        return false;
    }
}
