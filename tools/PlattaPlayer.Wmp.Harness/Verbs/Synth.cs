using PlattaPlayer.Wmp.Harness.Synth;

namespace PlattaPlayer.Wmp.Harness.Verbs;

/// <summary>Generates the deterministic audio sequence and proves it is reproducible.</summary>
internal static class SynthVerb
{
    public static int Run(string[] args)
    {
        var path = HarnessPaths.SynthFile;

        var a = SyntheticSequence.Generate();
        AudioFrameSet.Write(path, a);
        var firstBytes = File.ReadAllBytes(path);

        // Regenerate into a scratch file and compare. If the generator ever picks up ambient state
        // (a clock, an unseeded RNG) this catches it before it silently corrupts every comparison.
        var scratch = path + ".verify";
        AudioFrameSet.Write(scratch, SyntheticSequence.Generate());
        var secondBytes = File.ReadAllBytes(scratch);
        File.Delete(scratch);

        var identical = firstBytes.AsSpan().SequenceEqual(secondBytes);
        var sizeOk = firstBytes.LongLength == AudioFrameSet.ExpectedSize(SyntheticSequence.FrameCount);

        var roundTrip = AudioFrameSet.Read(path);
        var roundTripOk = roundTrip.Count == a.Count
            && roundTrip[123].Frequency0.AsSpan().SequenceEqual(a[123].Frequency0)
            && roundTrip[470].Waveform0.AsSpan().SequenceEqual(a[470].Waveform0)
            && roundTrip[400].State == a[400].State;

        Console.WriteLine($"wrote {path}");
        Console.WriteLine($"  {a.Count} frames, {firstBytes.LongLength:N0} bytes");
        Console.WriteLine();
        foreach (var (start, end, name) in SyntheticSequence.Segments)
        {
            var states = a.Skip(start).Take(end - start + 1).Select(f => f.State).Distinct().Order();
            Console.WriteLine($"  frames {start,3}-{end,3}  state {{{string.Join(",", states)}}}  {name}");
        }
        Console.WriteLine();

        var failures = 0;
        failures += Check("generating twice produces identical bytes", identical, "files differ");
        failures += Check("file size matches the declared frame count", sizeOk,
            $"{firstBytes.LongLength} vs {AudioFrameSet.ExpectedSize(SyntheticSequence.FrameCount)}");
        failures += Check("round-trips through the reader", roundTripOk, "readback mismatch");
        failures += Check("paused frames reuse the previous buffers",
            a[400].Frequency0.AsSpan().SequenceEqual(a[389].Frequency0), "frame 400 != frame 389");
        failures += Check("silent frames are actually zero",
            a[10].Frequency0.All(b => b == 0) && a[10].Waveform0.All(b => b == 0), "not zeroed");

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "synth: PASS" : $"synth: FAIL ({failures} check(s))");
        return failures == 0 ? 0 : 1;
    }

    private static int Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ok" : "FAIL")}] {what}{(ok ? "" : $" — {detail}")}");
        return ok ? 0 : 1;
    }
}
