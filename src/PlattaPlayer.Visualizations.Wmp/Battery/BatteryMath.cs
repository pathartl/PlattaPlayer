namespace PlattaPlayer.Visualizations.Wmp.Battery;

/// <summary>
/// Battery's arithmetic primitives and literals. Each literal is recorded with its real precision, read
/// from the bits in wmp.dll 12.0.26100.9278 x64 (see <c>Code\battery\03_shifts.md</c>).
/// </summary>
public static class BatteryMath
{
    /// <summary>The zero guard <c>(double)1e-4f</c> (0x18088c2d8). It is a promoted FLOAT, not 1e-4.</summary>
    public const double Epsilon = 9.999999747378752e-05;

    /// <summary>Single-precision pi promoted to double. CTwirlocity and CShiitake use it.</summary>
    public const double PiSingle = 3.1415927410125732;

    /// <summary>The float pi CShiitake::Randomize multiplies by in single precision (0x18088c4c8).</summary>
    public const float PiF = 3.1415927f;

    /// <summary>The genuine double literals the shifts use in place of pi-derived values: 1.57, 3.14 and
    /// 6.28. They are not pi/2, pi and 2pi, and replacing them changes the warp.</summary>
    public const double HalfPiLiteral = 1.57;

    public const double PiLiteral = 3.14;

    public const double TwoPiLiteral = 6.28;

    /// <summary>1/19, the step of the 18-table warp transition (0x18088c350).</summary>
    public const double TransitionStep = 0.05263157894736842;

    /// <summary>
    /// <c>cvttsd2si</c>: truncate toward zero, with int.MinValue for NaN or out-of-range input. C#'s
    /// <c>(int)</c> cast saturates and maps NaN to 0, so it differs exactly where the hardware returns
    /// its 0x80000000 "integer indefinite".
    /// </summary>
    public static int Trunc(double d) =>
        d is >= -2147483648.0 and < 2147483648.0 ? (int)d : int.MinValue;

    /// <summary><c>cvttss2si</c>: the single-precision twin of <see cref="Trunc"/>.</summary>
    public static int TruncF(float f) =>
        f is >= -2147483648f and < 2147483648f ? (int)f : int.MinValue;

    /// <summary>Single-precision 2pi promoted to double (0x401921FB60000000). Every angle wrap uses it.</summary>
    public const double TwoPiSingle = 6.2831854820251465;

    /// <summary>The CRT maths. Battery calls ucrt's sin/cos/atan2/sqrt through <c>_o_*</c> thunks.
    /// .NET on Windows reaches the same ucrt routines; the harness's MathRedirect classifies any
    /// residual.</summary>
    public static double Sin(double x) => Math.Sin(x);

    public static double Cos(double x) => Math.Cos(x);

    public static double Atan2(double y, double x) => Math.Atan2(y, x);

    public static double Sqrt(double x) => Math.Sqrt(x);
}
