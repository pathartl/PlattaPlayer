using PlattaPlayer.Visualizations.Wmp.Alchemy;
using PlattaPlayer.Visualizations.Wmp.Framebuffer;
using PlattaPlayer.Wmp.Harness.Imaging;
using PlattaPlayer.Wmp.Harness.Interop;

namespace PlattaPlayer.Wmp.Harness.Verbs;

/// <summary>
/// Draws the same stroke with the REAL spline and with ours, into buffers of the same size, and diffs
/// them pixel-for-pixel.
///
/// The per-point displacement is already proven exact (<c>verify-offsets</c>), so anything left is in how
/// the spline USES it — and the leading suspect is direction: the original's first point is displaced
/// along <c>(cos theta, sin theta)</c> of the chord angle, where ours moves perpendicular to it. That
/// changes the shape of every petal, which is why the flower can be the right size and still look wrong.
///
/// The palette is pinned (the line renderer's hold byte) so this measures geometry alone.
///
/// Usage: verify-spline [--png] [--mode N]
/// </summary>
internal static unsafe class VerifySpline
{
    private const int W = 200;
    private const int H = 200;

    private static readonly (int X0, int Y0, int X1, int Y1, string Name)[] Strokes =
    [
        (20, 100, 180, 100, "horizontal"),
        (100, 20, 100, 180, "vertical"),
        (20, 20, 180, 180, "diagonal down"),
        (180, 20, 20, 180, "diagonal up"),
        (30, 60, 170, 140, "shallow"),
    ];

    public static int Run(string[] args)
    {
        var writePng = args.Any(a => string.Equals(a, "--png", StringComparison.OrdinalIgnoreCase));
        var mode = ArgInt(args, "--mode", 1);

        Console.WriteLine(MpvisModule.Describe());
        Console.WriteLine($"drawing {Strokes.Length} strokes into {W}x{H}, plot mode {mode}, palette pinned");
        Console.WriteLine();

        using var levels = new TimedLevelBuffer();
        for (var i = 0; i < TimedLevelBuffer.Bins; i++)
        {
            levels.Waveform0[i] = (byte)(128 + 100 * Math.Sin(i * 0.35));
            levels.Waveform1[i] = (byte)(128 - 80 * Math.Sin(i * 0.2));
        }

        const int amplitude = 40;
        const int source = 0;
        const int envelope = 2;   // no taper, so the raw displacement is under test
        const int lobes = 3;
        const int stepLimit = 64;
        const uint colour = 0x00FF8040;

        using var oracle = new SplineOracle(W, H)
        {
            PlotMode = mode,
            NeighbourAlpha = 0.5f,
            StepLimit = stepLimit,
            Colour = colour,
        };
        oracle.SetOffsetParams(amplitude, source, envelope, lobes, mirror: false, levels.Pointer);

        var ours = new PixelBuffer();
        ours.Resize(W, H);
        var draw = new DrawPrimitives { NeighbourAlpha = 0.5f };
        var stroke = new WaveformStroke
        {
            MaxSteps = stepLimit, Amplitude = amplitude, Source = source, Envelope = envelope, Lobes = lobes,
            SampleMirror = false, PlotMode = mode,
        };
        var wave0 = levels.Waveform0.ToArray();
        var wave1 = levels.Waveform1.ToArray();

        var dir = Path.Combine(HarnessPaths.ReportDir, "spline");
        if (writePng) Directory.CreateDirectory(dir);

        // Is the colour we set the colour that gets drawn, and does it survive the stroke? If the field
        // changes, the palette advanced (the hold byte is in the wrong place); if it does not change but
        // the drawn colour still differs, we are writing to the wrong field.
        oracle.Clear();
        oracle.Draw(20, 100, 180, 100);
        var colourAfter = oracle.Peek(0x94);
        var drawn = oracle.Pixels.FirstOrDefault(p => (p & 0xFFFFFF) != 0) & 0xFFFFFF;
        Console.WriteLine($"  colour probe: set #{colour:X6}, field after stroke #{colourAfter & 0xFFFFFF:X6}, " +
                          $"first drawn pixel #{drawn:X6}");
        Console.WriteLine();

        var failures = 0;
        foreach (var (x0, y0, x1, y1, name) in Strokes)
        {
            oracle.Clear();
            oracle.Draw(x0, y0, x1, y1);
            var steps = oracle.LastSteps;

            Array.Clear(ours.Pixels);
            draw.Spline(ours, x0, y0, x1, y1, stroke, wave0, wave1, unchecked((int)(0xFF000000u | colour)));

            var (same, firstIndex) = Compare(oracle.Pixels, ours.Pixels);
            var total = W * H;
            var lit = CountLit(oracle.Pixels);
            var litOurs = CountLit(ours.Pixels);
            Console.WriteLine($"  {name,-14} steps {steps,3}   identical {same,6}/{total}  ({(double)same / total:P2})   " +
                              $"lit real {lit,5} ours {litOurs,5}");
            if (same != total)
            {
                failures++;
                if (firstIndex >= 0)
                    Console.WriteLine($"       first differing pixel ({firstIndex % W},{firstIndex / W}): " +
                                      $"real #{oracle.Pixels[firstIndex] & 0xFFFFFF:X6} ours #{ours.Pixels[firstIndex] & 0xFFFFFF:X6}");
            }

            if (writePng)
            {
                PngWriter.WriteBgra(Path.Combine(dir, $"{name.Replace(' ', '-')}-real.png"), ToBgra(oracle.Pixels), W, H);
                PngWriter.WriteBgra(Path.Combine(dir, $"{name.Replace(' ', '-')}-ours.png"), ToBgra(ours.Pixels), W, H);
            }
        }

        Console.WriteLine();
        if (writePng) Console.WriteLine($"wrote side-by-side PNGs to {dir}");
        Console.WriteLine(failures == 0
            ? "verify-spline: our spline draws exactly what the real one draws."
            : $"verify-spline: {failures} of {Strokes.Length} strokes DIVERGE.");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Compares COVERAGE — which pixels the stroke touches — not the colours in them.
    ///
    /// The two cannot be compared here: the real line renderer advances its colour transition on every
    /// plotted pixel and does so regardless of the hold byte we set, so its stroke is painted in a walking
    /// colour we are not driving. That behaviour has its own oracle (<c>verify-color</c>'s multi-step
    /// check, which runs both sides off one rand() script), leaving this verb to answer the question it is
    /// actually good at: does our spline put ink in the same places?
    /// </summary>
    private static (int Same, int FirstDiff) Compare(int[] a, int[] b)
    {
        var same = 0;
        var first = -1;
        for (var i = 0; i < a.Length; i++)
        {
            if (((a[i] & 0xFFFFFF) != 0) == ((b[i] & 0xFFFFFF) != 0)) { same++; continue; }
            if (first < 0) first = i;
        }
        return (same, first);
    }

    private static int CountLit(int[] pixels)
    {
        var n = 0;
        foreach (var p in pixels) if ((p & 0xFFFFFF) != 0) n++;
        return n;
    }

    private static byte[] ToBgra(int[] pixels)
    {
        var bytes = new byte[pixels.Length * 4];
        Buffer.BlockCopy(pixels, 0, bytes, 0, bytes.Length);
        for (var i = 3; i < bytes.Length; i += 4) bytes[i] = 0xFF;
        return bytes;
    }

    private static int ArgInt(string[] args, string name, int fallback)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(args[i + 1], out var v)) return v;
        return fallback;
    }
}
