namespace PlattaPlayer.Visualizations.PSP.Common;

/// <summary>
/// The fast scalar math library statically linked into visualizer_plugin.prx (src/common/vis_math.cpp).
/// These are approximations; the visualizers use them instead of libm, so the port must too to reproduce
/// the original output. Addresses are in visualizer_plugin.prx.
/// </summary>
public static class VisMath
{
    private const float Pi = 3.14159274f;       // 0x40490fdb
    private const float HalfPi = 1.57079637f;   // 0x3fc90fdb
    private const float TwoPi = 6.28318548f;    // 0x40c90fdb

    public static uint FloatBits(float f) => BitConverter.SingleToUInt32Bits(f);

    public static float BitsFloat(uint u) => BitConverter.UInt32BitsToSingle(u);

    /// <summary>
    /// The compiler's float -> unsigned idiom (<c>c.le.s 2^31 / sub / trunc.w.s / or 0x80000000</c>).
    /// Write this wherever the decompile shows the 2.1474836e+09 subtraction.
    /// </summary>
    public static uint FloatToU32(float f)
    {
        if (2147483648.0f <= f) return (uint)(int)(f - 2147483648.0f) | 0x80000000u;
        return (uint)(int)f;
    }

    // Shared polynomial kernels of Sin/Cos (both call the same two).
    private static float SinPoly(float x)
    {
        var x2 = x * x;
        return ((x2 * -0.000194189f + 0.00833132f) * x2 + -0.166666f) * x2 * x + x;
    }

    private static float CosPoly(float x)
    {
        var x2 = x * x;
        return ((x2 * -0.00137199f + 0.0416632f) * x2 + -0.5f) * x2 + 1.0f;
    }

    // Applies the sign bit of signSrc to a non-negative result; a zero result stays +0.
    private static float WithSign(float r, uint signSrc)
    {
        var b = FloatBits(r);
        return b == 0 ? 0.0f : BitsFloat(b | (signSrc & 0x80000000u));
    }

    /// <summary>0xf888</summary>
    public static float Fabs(float x) => MathF.Abs(x);

    /// <summary>0xf890</summary>
    public static float Fmax(float a, float b) => b < a ? a : b;

    /// <summary>0xf8ac</summary>
    public static float Rsqrt(float x) => 1.0f / MathF.Sqrt(x);

    /// <summary>0xf8c0 (sqrt.s)</summary>
    public static float Sqrt(float x) => MathF.Sqrt(x);

    /// <summary>0xf8c8: 0 for +-0, else -1 / +1 (sign of x).</summary>
    public static int SignBits(float x)
    {
        var b = (int)FloatBits(x);
        return ((uint)b << 1) != 0 ? 0 : ((b >> 30) | 1);
    }

    /// <summary>0xf8e4: natural log, k*ln2 + 2*atanh(f) with f = (m-1)/(m+1) on [0.707, 1.414).</summary>
    public static float Log(float x)
    {
        var bits = FloatBits(x);
        if ((int)bits <= 0)
            return (bits & 0x7fffffffu) != 0 ? BitsFloat(0x7fffffffu) : float.NegativeInfinity;
        var m = (int)(bits & 0x7fffff);
        int num = m - 0x800000, den = m;
        var e = (int)bits;
        if (m <= 0x3504f3)
        {
            // mantissa < sqrt(2): use exponent-1
            num = m;
            den = m - 0x800000;
            e = (int)(bits - 0x800000);
        }
        var f = (float)num / (float)(den + 0x1800000);
        var f2 = f * f;
        var k = (float)(unchecked((e >> 23) * 0xb17218 + (int)0xa8a9d830u)) * 5.96046448e-08f;
        return k + f * ((f2 * 0.413975f + 0.666477f) * f2 + 2.0000005f);
    }

    /// <summary>0xf9e4: log10 (same structure as <see cref="Log"/> with log10 constants).</summary>
    public static float Log10(float x)
    {
        var bits = FloatBits(x);
        if ((int)bits <= 0)
            return (bits & 0x7fffffffu) != 0 ? BitsFloat(0x7fffffffu) : float.NegativeInfinity;
        var m = (int)(bits & 0x7fffff);
        int num = m - 0x800000, den = m;
        var e = (int)bits;
        if (m <= 0x3504f3)
        {
            num = m;
            den = m - 0x800000;
            e = (int)(bits - 0x800000);
        }
        var f = (float)num / (float)(den + 0x1800000);
        var f2 = f * f;
        var k = (float)(unchecked((e >> 23) * 0x4d104d - 0x25ee05e6)) * 5.96046448e-08f;
        return k + f * ((f2 * 0.17978694f + 0.28944743f) * f2 + 0.86858916f);
    }

    /// <summary>0xfae4</summary>
    public static float Sin(float x)
    {
        var sign = FloatBits(x);
        var a = MathF.Abs(x);
        if (TwoPi < a) a -= (float)(int)(a / TwoPi) * TwoPi;
        if (Pi < a)
        {
            a -= Pi;
            sign = 0u - sign; // negu on the raw bits
        }
        if (HalfPi < a) a = Pi - a;
        var r = (0.917345f < a) ? CosPoly(HalfPi - a) : SinPoly(a);
        return WithSign(r, sign);
    }

    /// <summary>0xfc28</summary>
    public static float Cos(float x)
    {
        var a = MathF.Abs(x);
        var sign = 1;
        if (TwoPi < a) a -= (float)(int)(a / TwoPi) * TwoPi;
        if (Pi < a)
        {
            a -= Pi;
            sign = -1;
        }
        if (HalfPi < a)
        {
            a = Pi - a;
            sign = -sign;
        }
        var r = (0.653451f < a) ? SinPoly(HalfPi - a) : CosPoly(a);
        return WithSign(r, (uint)sign);
    }

    /// <summary>0xfe08: atan on [0, 1] with range reduction around tan(pi/8) and 1.</summary>
    public static float AtanCore(float x)
    {
        if (x < 0.1989f)
        {
            var x2 = x * x;
            return ((x2 * -0.14f + 0.2f) * x2 + -0.3333333f) * x2 * x + x;
        }
        float t, off;
        if (0.668106f <= x)
        {
            t = (x - 1.0f) / (x + 1.0f);
            off = 0.7853982f;
        }
        else
        {
            t = (x - 0.41421357f) / (x * 0.41421357f + 1.0f);
            off = 0.3926991f;
        }
        var t2 = t * t;
        return t * (((t2 * -0.14f + 0.2f) * t2 + -0.3333333f) * t2 + 1.0f) + off;
    }

    /// <summary>0xfd6c: |y| &lt; |x| is an integer compare of the magnitude bits.</summary>
    public static float Atan2(float y, float x)
    {
        uint ay = FloatBits(y) & 0x7fffffffu, ax = FloatBits(x) & 0x7fffffffu;
        float r;
        if (ay < ax) r = AtanCore(BitsFloat(ay) / BitsFloat(ax));
        else r = HalfPi - AtanCore(BitsFloat(ax) / BitsFloat(ay));
        if ((int)FloatBits(x) < 0) r = Pi - r;
        return BitsFloat(FloatBits(r) | (FloatBits(y) & 0x80000000u));
    }

    /// <summary>0xff60</summary>
    public static float Atan(float x)
    {
        var a = MathF.Abs(x);
        var r = (1.0f <= a) ? HalfPi - AtanCore(1.0f / a) : AtanCore(a);
        return BitsFloat(FloatBits(r) | (FloatBits(x) & 0x80000000u));
    }

    /// <summary>0xffdc</summary>
    public static float Asin(float x) => Atan2(x, MathF.Sqrt(1.0f - x * x));

    /// <summary>0x10008: 2^n * ((6 + f*ln2 + 0.0300163 f^2) / (6 - f*ln2/2))^4</summary>
    public static float Exp(float x)
    {
        if (0x42b00f33 < (int)(FloatBits(x) & 0x7fffffffu))
        {
            if (float.IsNaN(x)) return x;
            return x < 0.0f ? 0.0f : float.PositiveInfinity;
        }
        var t = MathF.Abs(x) * 1.44269502f;
        var n = (float)(int)MathF.Round(t); // round.w.s (round half to even)
        var ni = (int)n;
        var f = t - n;
        if (x < 0.0f)
        {
            f = -f;
            ni = -ni;
        }
        var q = (f * 0.693147f + 6.0f + f * 0.0300163f * f) / (6.0f - f * 0.346574f);
        q *= q;
        return q * q * BitsFloat((uint)(ni + 0x7f) << 23);
    }

    /// <summary>0x100f8</summary>
    public static float Pow(float x, float y)
    {
        if (y == 0.0f) return 1.0f;
        if (x == 0.0f) return 0.0f;
        var a = MathF.Abs(x);
        if (a != 1.0f) a = Exp(y * Log(a));
        if (x < 0.0f)
        {
            if ((int)MathF.Ceiling(y) != (int)MathF.Floor(y)) return BitsFloat(0x7fffffffu);
            if (((int)y & 1) != 0) a = -a;
        }
        return a;
    }

    /// <summary>0x101c8: Pow(10, y)</summary>
    public static float Pow10(float y) => Pow(10.0f, y);
}
