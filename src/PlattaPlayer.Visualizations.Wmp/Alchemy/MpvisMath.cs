namespace PlattaPlayer.Visualizations.Wmp.Alchemy;

/// <summary>
/// Constants that must match mpvis.DLL's bit for bit rather than being the mathematically correct value.
/// </summary>
public static class MpvisMath
{
    /// <summary>
    /// The pi mpvis actually uses: <c>0x400921FB60000000</c>, which is SINGLE-precision pi promoted to a
    /// double (3.14159274…), not <see cref="System.Math.PI"/> (3.14159265…). The original was written
    /// with a float literal and every effect inherits it.
    ///
    /// The difference is in the eighth significant figure and looks far too small to matter. It is not:
    /// these kernels multiply an angle by a radius of several hundred pixels and then TRUNCATE to an
    /// integer, so a value sitting near a whole-number boundary lands on the other side of it. Diffing
    /// StretchShift against the real kernel over every pixel (harness <c>verify-warps</c>) showed exactly
    /// this — about a dozen coordinates in 1.8 million, off by one, and only ever in the parameter sets
    /// where the sine term was active. Substituting .NET's sin/cos into the DLL changed nothing, which is
    /// what ruled out a library rounding difference and pointed here.
    /// </summary>
    public const double Pi = 3.1415927410125732;

    /// <summary>
    /// The same pi as a genuine SINGLE, stored separately at <c>0x18002377c</c>. Where a routine keeps its
    /// whole angle computation in single precision it multiplies by THIS, not by the promoted
    /// <see cref="Pi"/> — see <c>LinearShift</c>, whose sine shear divides, scales and multiplies entirely
    /// in float and only widens to call <c>sin</c>. Numerically it promotes to <see cref="Pi"/>; it exists
    /// as its own constant so the float-ness of an expression stays visible at the point of use.
    /// </summary>
    public const float PiSingle = (float)Pi;

    /// <summary>
    /// Half of <see cref="Pi"/>, stored in the DLL as its own literal at <c>0x180023718</c>
    /// (<c>0x3FF921FB60000000</c> = 1.5707963705062866). Halving is exact, so this is the same value —
    /// but it is worth naming, because the soft disc and the spline both reach for it directly.
    /// </summary>
    public const double HalfPi = Pi / 2;

    /// <summary>
    /// The soft disc's rim-band half-width at <c>0x180023738</c> / <c>0x1800237f0</c>:
    /// <c>0x40091EB860000000</c>, which is single-precision <b>3.14</b> promoted — deliberately NOT pi,
    /// and not <see cref="Pi"/> either. It reads like a rounded pi and is a hair smaller, which is exactly
    /// the sort of thing that gets "corrected" by someone tidying up. Do not.
    /// </summary>
    public const double DiscRimLimit = 3.140000104904175;

    /// <summary>
    /// The beat threshold multiplier from <c>FUN_18000ac48</c>: <c>0x3FF19999A0000000</c>, single-precision
    /// 1.1 promoted to a double. Its neighbour <c>0.9</c> in the same function is an exact double
    /// (<c>0x3FECCCCCCCCCCCCD</c>), so the float-ness is not a blanket property of the file — the
    /// constants have to be read one at a time.
    ///
    /// A beat is decided by <c>avg * this &lt; bass</c>. When the two sides are within an ULP the
    /// comparison tips the other way, the beat fires a frame early or late, and every effect re-randomizes
    /// at the wrong moment — a desynchronisation from the music that nothing in a rendered frame reveals.
    /// </summary>
    public const double BeatMultiplier = 1.1000000238418579;

    /// <summary>
    /// The bass-hit threshold multiplier, <c>0x3FF3333340000000</c> — single-precision 1.2 promoted.
    /// See <see cref="BeatMultiplier"/>.
    /// </summary>
    public const double BassHitMultiplier = 1.2000000476837158;

    /// <summary>
    /// <c>FUN_18000a29c</c>: the MINIMUM of <paramref name="k"/> uniform draws from <c>[lo, hi]</c>, a
    /// strong bias toward <c>lo</c>. Used both by the scheduler's slot counts and by the chord renderer's
    /// ScalePct, and it matters for draw accounting as much as for the value — it consumes
    /// <paramref name="k"/> values, not one, so getting it wrong desynchronises everything drawn after it.
    /// </summary>
    public static int MinOfK(System.Random random, int lo, int hi, int k)
    {
        var best = hi;
        for (var i = 0; i < k; i++)
        {
            var v = RandLong(random, lo, hi);
            if (v < best) best = v;
        }
        return best;
    }

    /// <summary>
    /// <c>RandLong</c> (<c>0x18000a260</c>): <c>lo + rand() % (hi - lo + 1)</c>. Both ends are INCLUSIVE,
    /// and the draw is taken even when the range is empty (the result is then <c>lo</c>), so it always
    /// consumes exactly one value.
    /// </summary>
    public static int RandLong(System.Random random, int lo, int hi)
    {
        var span = hi - lo + 1;
        var v = random.Next();
        return lo + (span > 0 ? v % span : 0);
    }
}
