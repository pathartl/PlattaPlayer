namespace PlattaPlayer.Visualizations.Wmp.Battery.Shifts;

// The 14 Battery warp classes. Each Randomize and FormShift is transcribed from wmp.dll 12.0.26100.9278
// x64 (VAs in each summary; the derivation is in Code\battery\03_shifts.md). Precision notes:
//   * Randomize works in SINGLE precision: (float)rand()/32767f, times a float, minus a float. Where the
//     source wrote a double literal (0.2, 0.8, 0.3, 0.1) the product is formed in double and rounded to
//     float before the float subtraction. The casts below are spelled out so C# cannot widen anything.
//   * FormShift is double throughout, with genuine-double literals 1.57 / 3.14 / 6.28 (not pi-derived)
//     and two promoted floats (BatteryMath.Epsilon, BatteryMath.PiSingle).

/// <summary><c>CLinearShift</c>: a constant integer scroll. Randomize <c>0x18041ba10</c>, FormShift
/// <c>0x18041a850</c>. It is in the random pool twice, so it comes up twice as often as any other.</summary>
public sealed class LinearShift : ShiftTable
{
    public override string Name => "CLinearShift";

    public override void Randomize(CrtRand rand)
    {
        P[0] = rand.Next() % 6 - 3; // -3..2
        P[1] = rand.Next() % 6 - 3;
    }

    public override void FormShift(ref int x, ref int y, CrtRand rand)
    {
        x += BatteryMath.Trunc(P[0]);
        y += BatteryMath.Trunc(P[1]);
    }

    /// <summary>The field's whole-pixel scroll in device pixels. Its fraction
    /// (<see cref="DeviceScrollRemainder"/>) is carried frame to frame by the renderer.</summary>
    protected override void ScaleForDevice(double s)
    {
        P[0] = BatteryMath.Trunc(P[0]) * s;
        P[1] = BatteryMath.Trunc(P[1]) * s;
    }

    /// <summary>What a device table's whole-pixel scroll leaves out per frame, per axis.</summary>
    public static (double X, double Y) DeviceScrollRemainder(ShiftTable deviceClone) =>
        (deviceClone.P[0] - BatteryMath.Trunc(deviceClone.P[0]), deviceClone.P[1] - BatteryMath.Trunc(deviceClone.P[1]));
}

/// <summary><c>CThingusShift</c>: a radius and angle push that fades out at a quarter of the width.
/// Randomize <c>0x18041bff0</c>, FormShift <c>0x18041b0f0</c>.</summary>
public sealed class ThingusShift : ShiftTable
{
    public override string Name => "CThingusShift";

    public override void Randomize(CrtRand rand)
    {
        // 0.2f here, where CTileShift uses the double 0.2.
        P[0] = (float)((float)(rand.Unit() * 0.8f) - 0.4f);
        P[1] = (float)(rand.Unit() * 0.2f);
    }

    public override void FormShift(ref int x, ref int y, CrtRand rand)
    {
        var dx = Cx - x;
        var dy = Cy - y;
        var r = BatteryMath.Sqrt(dy * dy + dx * dx); // before atan2 in this one
        var t = ((double)(W >> 2) - r) / ((W >> 1) + (W >> 2));
        var rr = ((double)W * P[1]) * t + r;
        var theta = BatteryMath.Atan2(dy, dx) + t * P[0];
        SetCenterData(ref x, ref y, theta, rr);
    }
}

/// <summary><c>CZoomShift</c>: a zoom proportional to the radius, plus a constant spin. Randomize
/// <c>0x18041c330</c>, FormShift <c>0x18041b850</c>.</summary>
public sealed class ZoomShift : ShiftTable
{
    public ZoomShift() => KeepOutOfRange = false;

    public override string Name => "CZoomShift";

    public override void Randomize(CrtRand rand)
    {
        P[0] = (float)((float)(rand.Unit() * 0.1f) - 0.05f);
        P[1] = (float)((float)((double)rand.Unit() * 0.2) - 0.1f);
    }

    public override void FormShift(ref int x, ref int y, CrtRand rand)
    {
        var hd = BatteryMath.Sqrt(W * W + H * H) * 0.5;
        if (hd == 0.0) hd = BatteryMath.Epsilon;
        GetCenterData(x, y, out var theta, out var r);
        var rr = r - ((double)W * P[1]) * (r / hd);
        SetCenterData(ref x, ref y, theta + P[0], rr);
    }
}

/// <summary><c>CRingSpinShift</c>: folds the radius inside concentric rings, plus a constant spin.
/// Randomize <c>0x18041baa0</c>, FormShift <c>0x18041a870</c>.</summary>
public sealed class RingSpinShift : ShiftTable
{
    public RingSpinShift() => KeepOutOfRange = false;

    public override string Name => "CRingSpinShift";

    public override void Randomize(CrtRand rand)
    {
        P[0] = (float)((float)(rand.Unit() * 0.1f) - 0.05f);
        P[1] = (float)((double)rand.Unit() * 0.8);
    }

    public override void FormShift(ref int x, ref int y, CrtRand rand)
    {
        GetCenterData(x, y, out var theta, out var r);
        var ring = (double)(H >> 1) * P[1];
        if (ring == 0.0) ring = BatteryMath.Epsilon;
        var k = BatteryMath.Trunc(r / ring);
        var fr = r - k * ring;
        var rr = r - (fr / ring) * fr;
        SetCenterData(ref x, ref y, theta + P[0], rr);
    }
}

/// <summary><c>CStretchShift</c>: a cubic radial pull with a radius-weighted twist. Randomize
/// <c>0x18041bea0</c>, FormShift <c>0x18041adc0</c>.</summary>
public sealed class StretchShift : ShiftTable
{
    public StretchShift() => KeepOutOfRange = false;

    public override string Name => "CStretchShift";

    public override void Randomize(CrtRand rand)
    {
        P[0] = (float)((float)(rand.Unit() * 0.1f) - 0.05f);
        P[1] = (float)((double)rand.Unit() * 0.3);
    }

    public override void FormShift(ref int x, ref int y, CrtRand rand)
    {
        GetCenterData(x, y, out var theta, out var r);
        var k = BatteryMath.Trunc(H * P[1]);
        var t = W >= 2 ? r / (W >> 1) : 0.0;
        var rr = r - ((t * t) * t) * k;
        SetCenterData(ref x, ref y, t * P[0] + theta, rr);
    }
}

/// <summary><c>CTileShift</c>: a per-axis cubic pinch inside tiles of <c>H * p1</c> pixels. It is the only
/// shift with a flag (1), which suppresses pre-effects that carry bit 0. Randomize <c>0x18041c070</c>,
/// FormShift <c>0x18041b230</c>.</summary>
public sealed class TileShift : ShiftTable
{
    public TileShift() => Flags = 1;

    public override string Name => "CTileShift";

    public override void Randomize(CrtRand rand) => P[0] = (float)((double)rand.Unit() * 0.2);

    public override void FormShift(ref int x, ref int y, CrtRand rand)
    {
        var tile = H * P[0]; // H for both axes
        if (tile == 0.0) tile = BatteryMath.Epsilon;
        y = Pinch(y, tile);
        x = Pinch(x, tile);
    }

    private static int Pinch(int v, double tile)
    {
        double d = v;
        var k = BatteryMath.Trunc(d / tile);
        var f = d - k * tile;
        var q = f / tile;
        return v - BatteryMath.Trunc(((q * q) * q) * f);
    }
}

/// <summary><c>CTrigShift</c>: a zoom modulated by the cosine or sine of the angle, plus a spin.
/// Randomize <c>0x18041c0c0</c>, FormShift <c>0x18041b2f0</c>.</summary>
public sealed class TrigShift : ShiftTable
{
    public override string Name => "CTrigShift";

    public override void Randomize(CrtRand rand)
    {
        P[0] = (float)((float)(rand.Unit() * 0.1f) - 0.05f);
        P[1] = (float)((float)((double)rand.Unit() * 0.1) - 0.05f);
        P[2] = rand.Next() % 3;
    }

    public override void FormShift(ref int x, ref int y, CrtRand rand)
    {
        var hd = BatteryMath.Sqrt(W * W + H * H) * 0.5;
        if (hd == 0.0) hd = BatteryMath.Epsilon;
        var col = x;
        GetCenterData(x, y, out var theta, out var r);
        var g = Gain(BatteryMath.Trunc(P[2]), theta, col);
        var rr = r - (((double)W * P[1]) * (r / hd)) * g;
        SetCenterData(ref x, ref y, theta + P[0], rr);
    }

    /// <summary>The angular gain shared with <see cref="TrigStretchShift"/>. Mode 2 alternates by the low
    /// bit of the ORIGINAL destination column. It uses the unshifted angle.</summary>
    internal static double Gain(int mode, double theta, int col) => mode switch
    {
        0 => BatteryMath.Cos(theta) / BatteryMath.HalfPiLiteral,
        1 => BatteryMath.Sin(theta) / BatteryMath.HalfPiLiteral,
        2 => ((col & 1) != 0 ? BatteryMath.Sin(theta) : BatteryMath.Cos(theta)) / BatteryMath.HalfPiLiteral,
        _ => 0.0,
    };
}

/// <summary><c>CSinShimmerShift</c>: a sine ripple along one axis. Randomize <c>0x18041bd10</c>, FormShift
/// <c>0x18041ab00</c>.</summary>
public sealed class SinShimmerShift : ShiftTable
{
    public override string Name => "CSinShimmerShift";

    public override void Randomize(CrtRand rand)
    {
        P[0] = (float)((float)(rand.Unit() * 10.0f) - 5.0f);
        var u = rand.Unit();
        P[1] = (float)(u + u); // ONE draw, doubled; the Win7 decompile reads like two
        P[2] = rand.Next() % 2;
    }

    public override void FormShift(ref int x, ref int y, CrtRand rand)
    {
        double fx = x, fy = y;
        switch (BatteryMath.Trunc(P[2]))
        {
            case 0:
                y = BatteryMath.Trunc(fy - (BatteryMath.Sin(fx * P[1]) + BatteryMath.Sin(fy * P[1])) * P[0]);
                break;
            case 1:
                x = BatteryMath.Trunc(fx - (BatteryMath.Sin(fy * P[1]) + BatteryMath.Sin(P[1] * fx)) * P[0]);
                break;
        }
    }

    /// <summary>p1 is an amplitude in pixels, p2 a frequency per pixel.</summary>
    protected override void ScaleForDevice(double s)
    {
        P[0] *= s;
        P[1] /= s;
    }
}

/// <summary><c>CEdgeFalloffShift</c>: scales one axis away from one edge. Randomize <c>0x18041b9a0</c>,
/// FormShift <c>0x18041a770</c>.</summary>
public sealed class EdgeFalloffShift : ShiftTable
{
    public EdgeFalloffShift() => KeepOutOfRange = false;

    public override string Name => "CEdgeFalloffShift";

    public override void Randomize(CrtRand rand)
    {
        P[0] = (float)(rand.Unit() * 0.1f);
        P[1] = rand.Next() % 4;
    }

    public override void FormShift(ref int x, ref int y, CrtRand rand)
    {
        var s = P[0] + 1.0;
        switch (BatteryMath.Trunc(P[1]))
        {
            case 0:
                x = BatteryMath.Trunc(s * x);
                break;
            case 1:
                y = BatteryMath.Trunc(s * y);
                break;
            case 2:
            {
                var a = W - x - 1;
                x -= BatteryMath.Trunc(s * a - a);
                break;
            }
            case 3:
            {
                var a = H - y - 1;
                y -= BatteryMath.Trunc(s * a - a);
                break;
            }
        }
    }
}

/// <summary><c>CStarburstShift</c>: petals (a sine of the angle) or alternating sectors, each pushing the
/// radius by a cubic. Randomize <c>0x18041bdb0</c>, FormShift <c>0x18041abf0</c>.</summary>
public sealed class StarburstShift : ShiftTable
{
    public override string Name => "CStarburstShift";

    public override void Randomize(CrtRand rand)
    {
        P[0] = (float)((float)(rand.Unit() * 0.1f) - 0.05f);
        P[1] = (float)((double)rand.Unit() * 0.3);
        var m = rand.Next() % 40;
        P[2] = (float)(m + m % 2); // even 0..40
        P[3] = (float)(rand.Next() % 2);
    }

    public override void FormShift(ref int x, ref int y, CrtRand rand)
    {
        GetCenterData(x, y, out var theta, out var r);
        var k = BatteryMath.Trunc(H * P[1]);
        var t = r / (W >> 1); // no W < 2 guard in this one

        double rr;
        if (P[3] == 0.0)
        {
            var n = BatteryMath.Trunc(P[2]);
            t *= BatteryMath.Sin((float)n * theta); // t is REPLACED, and the twist below uses it
            rr = r + ((t * t) * t) * k;
        }
        else
        {
            if (P[2] == 0.0) P[2] = 1.0; // written back into the object, so it persists
            var sector = BatteryMath.TwoPiLiteral / P[2];
            var c = ((t * t) * t) * k;
            var m = BatteryMath.Trunc(theta / sector);
            rr = (m & 1) != 0 ? r + c : r - c;
        }

        SetCenterData(ref x, ref y, t * P[0] + theta, rr);
    }
}

/// <summary><c>CSwirlShift</c>: a sine/cosine ripple, then a polar wobble, then a random jitter.
/// FormShift calls <c>rand()</c> ONCE PER PIXEL, so building its table advances the shared stream by the
/// field's pixel count. Randomize <c>0x18041bf20</c>, FormShift <c>0x18041af10</c>.</summary>
public sealed class SwirlShift : ShiftTable
{
    public override string Name => "CSwirlShift";

    public override void Randomize(CrtRand rand)
    {
        P[0] = (float)((float)(rand.Unit() * 0.1f) - 0.05f);
        P[1] = rand.Next() % 20 - 10.0;
        P[2] = rand.Next() % 24 - 12.0;
    }

    public override void FormShift(ref int x, ref int y, CrtRand rand)
    {
        int x0 = x, y0 = y;
        // The phase is ((p3+p3)*3.14)/H*y, left to right, with the 3.14 literal. The x ripple divides by
        // H and the y ripple by W.
        x = x0 + BatteryMath.Trunc(BatteryMath.Sin((P[2] + P[2]) * BatteryMath.PiLiteral / H * y0) * P[1]);
        y = y0 + BatteryMath.Trunc(BatteryMath.Cos((P[2] + P[2]) * BatteryMath.PiLiteral / W * x0) * P[1]);

        var dx = Cx - x;
        var dy = Cy - y;
        var theta = BatteryMath.Atan2(dy, dx) + P[0];
        var r = BatteryMath.Sqrt(dy * dy + dx * dx);
        var rr = r - BatteryMath.Sin(theta * P[2]) * P[1];
        SetCenterData(ref x, ref y, theta, rr);

        var j = DeviceScale > 0.0 ? DeviceJitter(x0, y0) : rand.Next() % 4 - 2;
        y += y0 < Cy ? -j : j;
        x += x0 < Cx ? -j : j;
    }

    /// <summary>p2 is an amplitude in pixels (of the ripple and the wobble).</summary>
    protected override void ScaleForDevice(double s) => P[1] *= s;

    /// <summary>
    /// A device build cannot draw from the shared rand() stream (the field's own build does, and the two
    /// must stay in lock-step), so its jitter is a fixed hash of the FIELD pixel the device pixel lies
    /// in, with the same -2..1 range, scaled to device pixels.
    /// </summary>
    private int DeviceJitter(int x, int y)
    {
        var fx = (uint)(int)(x / DeviceScale);
        var fy = (uint)(int)(y / DeviceScale);
        var h = fx * 0x9E3779B1u ^ fy * 0x85EBCA77u;
        h ^= h >> 15;
        h *= 0x2C1B3C6Du;
        h ^= h >> 12;
        return (int)Math.Round(((int)(h % 4) - 2) * DeviceScale);
    }
}

/// <summary><c>CTrigStretchShift</c>: two parts <see cref="TrigShift"/> to one part
/// <see cref="StretchShift"/>, in both radius and angle. Randomize <c>0x18041c180</c>, FormShift
/// <c>0x18041b4a0</c>.</summary>
public sealed class TrigStretchShift : ShiftTable
{
    public TrigStretchShift() => KeepOutOfRange = false;

    public override string Name => "CTrigStretchShift";

    public override void Randomize(CrtRand rand)
    {
        P[0] = (float)((float)(rand.Unit() * 0.1f) - 0.05f);
        P[1] = (float)((float)((double)rand.Unit() * 0.1) - 0.05f);
        P[2] = rand.Next() % 3;
        P[3] = (float)((double)rand.Unit() * 0.3);
    }

    public override void FormShift(ref int x, ref int y, CrtRand rand)
    {
        var hd = BatteryMath.Sqrt(W * W + H * H) * 0.5; // no zero guard, unlike CTrigShift
        var col = x;
        GetCenterData(x, y, out var theta, out var r);
        var g = TrigShift.Gain(BatteryMath.Trunc(P[2]), theta, col);
        var t = r / (W >> 1);
        var r1 = r - (((double)W * P[1]) * (r / hd)) * g;
        var k = BatteryMath.Trunc(H * P[3]);
        var r2 = r - k * ((t * t) * t);
        var rr = (r2 + (r1 + r1)) / 3.0;
        var th1 = P[0] + theta;
        var thetaOut = ((theta + P[0] * t) + (th1 + th1)) / 3.0;
        SetCenterData(ref x, ref y, thetaOut, rr);
    }
}

/// <summary><c>CTwirlocity</c>: an angular twist that is a cosine of the radius ratio. Nine times in ten
/// it is the INVERSE ratio <c>hh / r</c>. Randomize <c>0x18041c270</c>, FormShift <c>0x18041b700</c>.</summary>
public sealed class Twirlocity : ShiftTable
{
    public override string Name => "CTwirlocity";

    public override void Randomize(CrtRand rand)
    {
        P[0] = rand.Next() % 50 + 1;
        P[1] = (float)(rand.Unit() * 0.6f);
        P[2] = (float)(rand.Next() % 10);
    }

    public override void FormShift(ref int x, ref int y, CrtRand rand)
    {
        GetCenterData(x, y, out var theta, out var r);
        double hh = (float)((float)H * 0.5f);
        double v;
        if (P[2] == 0.0) v = hh != 0.0 ? r / hh : 0.0;
        else v = r != 0.0 ? hh / r : 0.0;
        var thetaOut = BatteryMath.Cos((v * BatteryMath.PiSingle) * P[0]) * P[1] + theta;
        SetCenterData(ref x, ref y, thetaOut, r);
    }
}

/// <summary><c>CShiitake</c>: a radial petal ripple plus a swirl, and the most common preset shift.
/// Randomize <c>0x18041bb20</c> always draws 7 times (its branches choose formulas, not draw counts).
/// FormShift <c>0x18041a9b0</c>.</summary>
public sealed class Shiitake : ShiftTable
{
    public override string Name => "CShiitake";

    public override void Randomize(CrtRand rand)
    {
        P[0] = (float)((float)(rand.Unit() * 11.0f) - 1.0f);
        var a = rand.Next() % 100 == 0 ? (float)(rand.Unit() * 16.0f) : (float)(rand.Unit() * 0.05f);
        P[1] = (float)(a * BatteryMath.PiF);
        P[2] = rand.Next() % 100 == 0 ? (float)(rand.Unit() * 8.0f) : (float)(rand.Unit() * 0.05f);
        var s = rand.Next() % 10;
        P[3] = s == 0 ? (float)(rand.Next() % 100 + 1)
            : s < 4 ? (float)(rand.Next() % 5 + 1)
            : (float)(rand.Next() % 2 + 1);
    }

    public override void FormShift(ref int x, ref int y, CrtRand rand)
    {
        GetCenterData(x, y, out var theta, out var r);
        var rr = r + P[0];
        var v = Cy != 0 ? ((rr + rr) * BatteryMath.PiSingle) / Cy : rr;
        var thetaOut = (BatteryMath.Cos(v * P[3]) * P[1] + theta) + v * P[2];
        SetCenterData(ref x, ref y, thetaOut, rr);
    }

    /// <summary>p1 is a radius offset in pixels.</summary>
    protected override void ScaleForDevice(double s) => P[0] *= s;
}
