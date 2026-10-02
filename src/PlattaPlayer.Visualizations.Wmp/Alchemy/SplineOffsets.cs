using System;

namespace PlattaPlayer.Visualizations.Wmp.Alchemy;

/// <summary>
/// The spline's per-point displacement, <c>FUN_18000b3cc</c> — verified point-for-point against the real
/// function by the harness (<c>verify-offsets</c>, 17,856 combinations of source, envelope, mirror,
/// amplitude, step count and lobe count, all exact).
///
/// THE CHORD RENDERERS ARE OSCILLOSCOPES. This displaces each point of a chord by the LIVE PCM WAVEFORM,
/// which is what gives the original's "flower" its shape and its scale. The port previously modelled it as
/// a parametric sine with an amplitude of about three pixels, calibrated by eye — where the real amplitude
/// is <c>(int)(Spin x fieldHeight)</c>, roughly 48 to 204 pixels. That is the single reason the flower came
/// out at the wrong size.
///
/// Ghidra is why it stayed hidden: it reports <c>FUN_18000b3cc</c> as returning <c>void</c> and drops the
/// float, so the body reads as nothing but a sine. An earlier survey also concluded the waveform buffer was
/// never touched anywhere in the DLL — it searched for channel 1 at <c>+0xa00</c>, but it is at
/// <c>+0xC00</c>, and the reads are in this helper rather than in anything named "render".
/// </summary>
public static class SplineOffsets
{
    /// <summary>Waveform source: which channel supplies the sample.</summary>
    public const int SourceChannel0 = 0;

    public const int SourceChannel1 = 1;

    public const int SourceMean = 2;

    /// <summary>
    /// Fills <paramref name="destination"/> with <c>steps + 1</c> displacements in pixels.
    /// </summary>
    /// <param name="envelopeMode">
    /// How the raw deflection is tapered along the chord: 0 applies <c>|sin(lobes * i/N * PI)|</c>,
    /// 1 a triangular ramp peaking at the midpoint, 2 nothing at all. In the chord renderer this comes
    /// straight from ScalePct — which is why that field is a 0..2 minimum-of-three draw and NOT the
    /// subdivision count it was once taken for.
    /// </param>
    /// <param name="mirror">Folds the SAMPLE index back past the midpoint; the envelope still uses the
    /// original index.</param>
    public static void Fill(Span<double> destination, byte[] waveform0, byte[] waveform1,
                            int steps, int amplitude, int source, int envelopeMode, int lobes, bool mirror)
    {
        if (steps < 1) return;
        var half = steps / 2;

        for (var i = 0; i <= steps && i < destination.Length; i++)
        {
            var sampleIndex = i;
            if (mirror && half < i) sampleIndex = half * 2 - i;

            // Everything is FLOAT except the sine's argument (CTLevelScale::GetLevel, 0x18000b3cc). This used
            // to run in double and round once at the end, which parts from the float product by an ulp now
            // and then. An ulp is enough to flip the stroke's half-away rounding at an exact .5, moving one
            // point a pixel, which is what verify-alchemy caught.
            float sample = source switch
            {
                SourceChannel0 => Sample(waveform0, sampleIndex),
                SourceChannel1 => Sample(waveform1, sampleIndex),
                SourceMean => (float)(Sample(waveform1, sampleIndex) + Sample(waveform0, sampleIndex)) * 0.5f,
                // Any other value leaves the sample register cleared, giving full negative deflection.
                _ => 0f,
            };

            // amplitude/128 * (sample - 128): the byte is centred and normalised to +/-1.
            var value = (float)amplitude * 0.0078125f * (sample - 128f);

            switch (envelopeMode)
            {
                case 0:
                    value *= (float)Math.Abs(Math.Sin(lobes * ((double)i / steps) * MpvisMath.Pi));
                    break;
                case 1:
                    // A triangle peaking at half = N >> 1, descending as 2*half - i (not N - i: the two
                    // differ for odd N). The envelope uses the UNFOLDED index.
                    var peak = steps >> 1;
                    value *= (float)(i < peak ? i : 2 * peak - i) / peak;
                    break;
            }

            destination[i] = value;
        }
    }

    private static byte Sample(byte[] waveform, int index) =>
        waveform.Length == 0 ? (byte)128 : waveform[index % waveform.Length];
}
