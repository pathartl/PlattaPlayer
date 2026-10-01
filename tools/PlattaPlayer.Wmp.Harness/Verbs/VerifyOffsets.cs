using PlattaPlayer.Visualizations.Wmp.Alchemy;
using PlattaPlayer.Wmp.Harness.Interop;

namespace PlattaPlayer.Wmp.Harness.Verbs;

/// <summary>
/// Characterises and then verifies <c>FUN_18000b3cc</c>, the spline's per-point displacement.
///
/// This is the function that decides the SHAPE of every chord the effects draw, and it was the last thing
/// still being guessed at: Ghidra reports it as returning <c>void</c>, so the port modelled it as a bare
/// parametric sine with an amplitude someone had calibrated by eye. The disassembly says it actually
/// samples the raw PCM waveform. Rather than argue from the disassembly, this drives the real function
/// with inputs we control and prints what it returns.
///
/// Run it with <c>--characterise</c> to dump the real curve for a range of parameters (what the model
/// should be built from), or plain to diff our implementation against it point by point.
///
/// Usage: verify-offsets [--characterise]
/// </summary>
internal static unsafe class VerifyOffsets
{
    /// <summary>A recognisable waveform: a ramp in channel 0 and a square in channel 1.</summary>
    private static void FillWaveform(TimedLevelBuffer buffer)
    {
        for (var i = 0; i < TimedLevelBuffer.Bins; i++)
        {
            buffer.Waveform0[i] = (byte)i;                      // 0..255 ramp, wrapping
            buffer.Waveform1[i] = (byte)((i / 16) % 2 == 0 ? 200 : 40);
        }
    }

    public static int Run(string[] args)
    {
        var characterise = args.Any(a => string.Equals(a, "--characterise", StringComparison.OrdinalIgnoreCase));

        Console.WriteLine(MpvisModule.Describe());
        Console.WriteLine();

        using var levels = new TimedLevelBuffer();
        FillWaveform(levels);

        using var oracle = new SplineOffsetOracle
        {
            TimedLevel = levels.Pointer,
            Steps = 16,
            Amplitude = 128,
            SinLoops = 3,
        };

        if (characterise)
        {
            Characterise(oracle);
            return 0;
        }

        return Verify(oracle);
    }

    /// <summary>
    /// Dumps the real curve for each source mode so the model can be read off the numbers instead of
    /// inferred. The waveform is a known ramp/square, so the mapping from sample to offset is legible.
    /// </summary>
    private static void Characterise(SplineOffsetOracle oracle)
    {
        Console.WriteLine("Steps=16 Amplitude=128 SinLoops=3, waveform ch0 = ramp i, ch1 = square 200/40");
        Console.WriteLine("(expectation from the disassembly: offset = Amplitude * (sample - 128) / 128)");
        Console.WriteLine();

        foreach (var source in new[] { 0, 1, 2, 3 })
        {
            oracle.Source = source;
            oracle.Mirror = false;
            oracle.IndexMode = 0;
            Console.Write($"  source {source}: ");
            for (var i = 0; i <= 16; i++) Console.Write($"{oracle.At(i),8:0.##}");
            Console.WriteLine();
        }

        Console.WriteLine();
        Console.WriteLine("  mirror on (source 0, should fold the index past the midpoint):");
        oracle.Source = 0;
        oracle.Mirror = true;
        Console.Write("            ");
        for (var i = 0; i <= 16; i++) Console.Write($"{oracle.At(i),8:0.##}");
        Console.WriteLine();

        oracle.Mirror = false;
        foreach (var mode in new[] { 0, 1, 2 })
        {
            oracle.IndexMode = mode;
            Console.Write($"  indexMode {mode} (source 0): ");
            for (var i = 0; i <= 16; i++) Console.Write($"{oracle.At(i),8:0.##}");
            Console.WriteLine();
        }

        Console.WriteLine();
        Console.WriteLine("  amplitude scaling (source 0, indexMode 0, i=4):");
        oracle.IndexMode = 0;
        foreach (var amp in new[] { 1, 16, 64, 128, 204 })
        {
            oracle.Amplitude = amp;
            Console.WriteLine($"    Amplitude {amp,4} -> {oracle.At(4):0.####}");
        }
    }

    /// <summary>
    /// Diffs our model against the real function across a grid of parameters and sample indices.
    /// </summary>
    private static int Verify(SplineOffsetOracle oracle)
    {
        var bad = 0;
        var total = 0;
        string? first = null;

        foreach (var source in new[] { 0, 1, 2, 3 })
        foreach (var indexMode in new[] { 0, 1, 2 })
        foreach (var mirror in new[] { false, true })
        foreach (var amplitude in new[] { 8, 48, 128, 204 })
        foreach (var steps in new[] { 5, 7, 10, 16, 49, 50, 64, 521 })
        foreach (var sinLoops in new[] { 3, 17 })
        {
            oracle.Source = source;
            oracle.Amplitude = amplitude;
            oracle.Steps = steps;
            oracle.Mirror = mirror;
            oracle.IndexMode = indexMode;
            oracle.SinLoops = sinLoops;

            // The PRODUCTION model, compared bit for bit. i runs to N-1: RenderWaveform never asks for N.
            var ours = new double[steps];
            SplineOffsets.Fill(ours, Wave0, Wave1, steps, amplitude, source, indexMode, sinLoops, mirror);
            for (var i = 0; i < steps; i++)
            {
                var real = oracle.At(i);
                total++;
                if (BitConverter.SingleToInt32Bits(real) == BitConverter.SingleToInt32Bits((float)ours[i])) continue;
                bad++;
                first ??= $"source={source} idx={indexMode} mirror={mirror} amp={amplitude} " +
                          $"steps={steps} loops={sinLoops} i={i}: real {real:R}, ours {(float)ours[i]:R}";
            }
        }

        Console.WriteLine($"  offsets identical: {total - bad}/{total}");
        if (first is not null) Console.WriteLine($"  first divergence: {first}");
        Console.WriteLine();
        Console.WriteLine(bad == 0
            ? "verify-offsets: our spline offset matches the real one."
            : "verify-offsets: DIVERGES from the real spline offset.");
        return bad == 0 ? 0 : 1;
    }

    private static readonly byte[] Wave0 = Enumerable.Range(0, TimedLevelBuffer.Bins).Select(i => Ch0(i)).ToArray();

    private static readonly byte[] Wave1 = Enumerable.Range(0, TimedLevelBuffer.Bins).Select(i => Ch1(i)).ToArray();

    private static byte Ch0(int i) => (byte)i;

    private static byte Ch1(int i) => (byte)((i / 16) % 2 == 0 ? 200 : 40);
}
