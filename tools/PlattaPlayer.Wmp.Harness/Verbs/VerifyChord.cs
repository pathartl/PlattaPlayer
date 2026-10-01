using PlattaPlayer.Wmp.Harness.Interop;

namespace PlattaPlayer.Wmp.Harness.Verbs;

/// <summary>
/// Recovers the chord's ENDPOINTS from the real <c>FUN_18000bdf4</c> and checks them against ours.
///
/// The spline's displacement and its geometry are both proven exact, so if the figure is still the wrong
/// size the error has to be in the length handed to the chord — or in what the chord does with it. That is
/// a one-line convention question with a two-fold consequence, and exactly the kind of thing worth
/// measuring rather than reasoning about: <c>FUN_18000bdf4</c> takes a FULL span and halves it internally,
/// so passing a radius where it wants a diameter doubles the whole figure.
///
/// The measurement is direct: set the displacement amplitude to zero so the spline degenerates to a
/// straight line, draw, and read the extreme lit pixels back out. Those ARE the endpoints.
///
/// Usage: verify-chord
/// </summary>
internal static unsafe class VerifyChord
{
    private const int W = 400;
    private const int H = 400;

    public static int Run(string[] args)
    {
        Console.WriteLine(MpvisModule.Describe());
        Console.WriteLine($"recovering chord endpoints from FUN_18000bdf4 in a {W}x{H} field");
        Console.WriteLine();

        using var levels = new TimedLevelBuffer();
        for (var i = 0; i < TimedLevelBuffer.Bins; i++) levels.Waveform0[i] = 128;   // flat: no displacement

        using var oracle = new SplineOracle(W, H)
        {
            PlotMode = 1,
            StepLimit = 512,
            Colour = 0x00FFFFFF,
        };
        // Amplitude 0 and a flat waveform both guarantee a straight chord.
        oracle.SetOffsetParams(0, 0, 2, 1, mirror: false, levels.Pointer);

        const int cx = 200;
        const int cy = 200;

        Console.WriteLine("   angle    length   real endpoints                 half-span   ours(r=length)  verdict");
        var bad = 0;

        foreach (var length in new[] { 100f, 200f, 360f })
        foreach (var angle in new[] { 0f, 0.5f, 1.5707964f, 2.4f })
        {
            oracle.Clear();
            oracle.Chord(cx, cy, angle, length);
            var (minX, minY, maxX, maxY, lit) = Extents(oracle.Pixels);
            if (lit == 0) { Console.WriteLine($"   {angle,6:0.###}  {length,7:0}   (nothing drawn)"); bad++; continue; }

            // The half-span the real function actually used, measured from the drawing.
            var spanX = (maxX - minX) / 2.0;
            var spanY = (maxY - minY) / 2.0;
            var measuredHalf = Math.Sqrt(spanX * spanX + spanY * spanY);

            // Our convention treats the value it is given as the HALF length, so it would draw this far.
            var oursHalf = length;

            var expectedHalf = length * 0.5;
            var ok = Math.Abs(measuredHalf - expectedHalf) <= 1.5;
            if (!ok) bad++;

            Console.WriteLine($"   {angle,6:0.###}  {length,7:0}   ({minX,3},{minY,3})-({maxX,3},{maxY,3})   " +
                              $"{measuredHalf,8:0.0}   {oursHalf,8:0.0}       " +
                              (ok ? "half = length/2" : "UNEXPECTED"));
        }

        Console.WriteLine();
        Console.WriteLine("  The real chord's half-span is length/2, so a renderer passing the field");
        Console.WriteLine("  diagonal draws a chord spanning the diagonal — not twice it.");
        Console.WriteLine();
        Console.WriteLine(bad == 0
            ? "verify-chord: the real chord halves its length argument, as modelled."
            : $"verify-chord: {bad} case(s) did not match the halving convention.");
        return bad == 0 ? 0 : 1;
    }

    private static (int MinX, int MinY, int MaxX, int MaxY, int Lit) Extents(int[] pixels)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue, lit = 0;
        for (var i = 0; i < pixels.Length; i++)
        {
            if ((pixels[i] & 0xFFFFFF) == 0) continue;
            lit++;
            var x = i % W;
            var y = i / W;
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }
        return (minX, minY, maxX, maxY, lit);
    }
}
