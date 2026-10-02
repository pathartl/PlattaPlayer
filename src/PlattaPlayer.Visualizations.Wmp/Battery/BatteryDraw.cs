namespace PlattaPlayer.Visualizations.Wmp.Battery;

/// <summary>
/// <c>CBatterySurface</c>'s composite primitives, written over <see cref="IBatteryCanvas"/> so the CPU field
/// and the GPU recorder share them. Derivation: <c>Code\battery\02_pipeline.md</c> §5.
/// </summary>
public static class BatteryDraw
{
    /// <summary><c>DrawClippedLine</c> (<c>0x180413c20</c>): clamps each ENDPOINT into the field, which
    /// changes the slope rather than clipping the segment.</summary>
    public static void DrawClippedLine(this IBatteryCanvas s, int x1, int y1, int x2, int y2, byte c) =>
        s.Line(Clamp(x1, s.W), Clamp(y1, s.H), Clamp(x2, s.W), Clamp(y2, s.H), c);

    private static int Clamp(int v, int lim) => v < 0 ? 0 : v >= lim ? lim - 1 : v;

    /// <summary>
    /// <c>DrawJCurve</c> (<c>0x180413ca0</c>): a polar arc from (x1,y1) to (x2,y2) about (cx,cy) in
    /// <paramref name="n"/> steps. The radius eases along a quarter sine. The colour walks linearly from
    /// c0 to c1. The steps are joined by clamped lines (<paramref name="connect"/>) or plotted as clipped
    /// dots.
    /// </summary>
    public static void DrawJCurve(this IBatteryCanvas s, int x1, int y1, int x2, int y2, int cx, int cy, int n, byte c0, byte c1,
        bool connect, byte dirMode)
    {
        if (n == 0) return;
        int dx1 = x1 - cx, dy1 = y1 - cy, dx2 = x2 - cx, dy2 = y2 - cy;
        var cstep = (double)(int)((uint)c1 - (uint)c0) / n;
        double col = c0;
        var r1 = BatteryMath.Sqrt(dx1 * dx1 + dy1 * dy1);
        var r2 = BatteryMath.Sqrt(dx2 * dx2 + dy2 * dy2);
        var dr = r2 - r1;
        // dx == 0 gives angle 0 even when dy != 0: a quirk, kept.
        var a1 = dx1 != 0 ? BatteryMath.Atan2(dy1, dx1) : 0.0;
        var a2 = dx2 != 0 ? BatteryMath.Atan2(dy2, dx2) : 0.0;
        const double twoPiF = 6.2831854820251465; // (double)(float)2pi
        const double piF = BatteryMath.PiSingle;
        if (0.0 > a1) a1 += twoPiF;
        if (0.0 > a2) a2 += twoPiF;
        var da = a2 - a1;
        switch (dirMode)
        {
            case 1:
                da = -(twoPiF - da);
                break;
            case 2:
                break;
            case 4:
                if (!(da >= piF)) da = -(twoPiF - da);
                break;
            default:
                if (!(da < piF)) da = -(twoPiF - da);
                break;
        }

        double phstep = BatteryMath.PiF / ((float)n + (float)n); // SINGLE precision
        da /= n;
        if (n < 1) return;

        double ph = 0.0, a = a1;
        int px = x1, py = y1;
        for (var i = 0; i < n; i++)
        {
            var r = BatteryMath.Sin(ph) * dr + r1;
            var x = BatteryMath.Trunc(BatteryMath.Cos(a) * r + cx);
            var y = BatteryMath.Trunc(BatteryMath.Sin(a) * r + cy);
            var cc = (byte)BatteryMath.Trunc(col);
            if (connect)
            {
                s.DrawClippedLine(px, py, x, y, cc);
                px = x;
                py = y;
            }
            else if (x >= 0 && x < s.W && y >= 0 && y < s.H)
            {
                s.Pixel(x, y, cc);
            }

            ph += phstep;
            a += da;
            col += cstep;
        }
    }
}
