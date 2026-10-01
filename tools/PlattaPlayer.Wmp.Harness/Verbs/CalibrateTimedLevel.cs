using ManagedBass;
using PlattaPlayer.Wmp.Harness.Synth;

namespace PlattaPlayer.Wmp.Harness.Verbs;

/// <summary>
/// Calibrates our float tap against WMP's real TimedLevel bytes. For every frame
/// <see cref="CaptureTimedLevel"/> recorded, it decodes the same test WAV with BASS at that frame's media
/// timestamp and takes <c>FFT2048</c>, which is exactly what the app's tap (<c>BassPlaybackEngine</c>)
/// reads. Then, per bin, it relates WMP's byte to <c>log10</c> of BASS's magnitude:
/// <list type="bullet">
/// <item>a free least-squares fit <c>v = a·log10(m) + b</c> over the bins well above the floor, which
/// says whether the slope is magnitude (31.9 per decade) or power (63.75);</item>
/// <item>the offset <c>b</c> with the slope pinned to 255/4, the value to put in the adapter;</item>
/// <item>side-by-side peak shapes, which show whether WMP's analysis window differs from BASS's.</item>
/// </list>
/// Usage: calibrate-timedlevel
/// </summary>
internal static class CalibrateTimedLevel
{
    private const double Guard = 0.4;
    private const double PowerSlope = 255.0 / 4.0;

    public static int Run(string[] args)
    {
        var framesPath = Path.Combine(CaptureTimedLevel.Dir, "frames.bin");
        var wav = Path.Combine(CaptureTimedLevel.Dir, "test.wav");
        if (!File.Exists(framesPath) || !File.Exists(wav))
        {
            Console.Error.WriteLine("calibrate-timedlevel: no capture — run 'capture-timedlevel' first.");
            return 2;
        }

        if (!Bass.Init(0, TestSignal.Rate) && Bass.LastError != Errors.Already)
            throw new InvalidOperationException($"BASS init failed: {Bass.LastError}");
        var stream = Bass.CreateStream(wav, 0, 0, BassFlags.Decode);
        if (stream == 0) throw new InvalidOperationException($"BASS could not open the WAV: {Bass.LastError}");

        var captured = AudioFrameSet.Read(framesPath).Where(f => f.State == 2)
            .GroupBy(f => f.TimeStamp).Select(g => g.First()).ToList();

        var fft = new float[1024];
        var samples = new List<(int Segment, byte[] Wmp, float[] Bass)>();
        foreach (var f in captured)
        {
            var t = f.TimeStamp / 1e7;
            var s = (int)(t / TestSignal.SegmentSeconds);
            var within = t - s * TestSignal.SegmentSeconds;
            if (s < 0 || s >= TestSignal.Segments.Length || within < Guard || within > TestSignal.SegmentSeconds - Guard) continue;
            Bass.ChannelSetPosition(stream, Bass.ChannelSeconds2Bytes(stream, t));
            if (Bass.ChannelGetData(stream, fft, (int)DataFlags.FFT2048) < 0) continue;
            samples.Add((s, (byte[])f.Frequency0.Clone(), (float[])fft.Clone()));
        }
        Bass.StreamFree(stream);
        Console.WriteLine($"{samples.Count} frames matched to BASS FFT2048");

        // Bins clearly above WMP's floor, where the log relation is not clipped at 0.
        var points = new List<(double LogM, double V, int Segment, int Bin)>();
        foreach (var (s, wmp, bass) in samples)
            for (var k = 1; k < 1024; k++)
                if (wmp[k] >= 40 && bass[k] > 0)
                    points.Add((Math.Log10(bass[k]), wmp[k], s, k));

        var (a, b) = Fit(points.Select(p => (p.LogM, p.V)));
        var pinned = points.Average(p => p.V - PowerSlope * p.LogM);
        var rms = Math.Sqrt(points.Average(p => Math.Pow(p.V - (PowerSlope * p.LogM + pinned), 2)));
        Console.WriteLine($"free fit:   v = {a:0.00} * log10(m) + {b:0.00}   over {points.Count} bin samples");
        Console.WriteLine($"power fit:  v = 63.75 * log10(m) + {pinned:0.00}   (rms residual {rms:0.00})");

        // Per segment: mean residual of the pinned fit, separately at the peak and on the skirts.
        Console.WriteLine();
        Console.WriteLine("segment            peak: wmp / ours(pinned)      skirts mean residual   bass sum wmp / ours");
        for (var s = 0; s < TestSignal.Segments.Length; s++)
        {
            var set = samples.Where(x => x.Segment == s).ToList();
            if (set.Count == 0) continue;
            var wmp = MeanBytes(set.Select(x => x.Wmp));
            var ours = MeanBytes(set.Select(x => Encode(x.Bass, pinned)));
            var peak = Array.IndexOf(wmp, wmp.Max());
            var skirt = Enumerable.Range(1, 1023).Where(k => wmp[k] >= 40 && Math.Abs(k - peak) > 2).ToList();
            var skirtRes = skirt.Count == 0 ? 0 : skirt.Average(k => (double)wmp[k] - ours[k]);
            Console.WriteLine($"{TestSignal.Segments[s].Name,-18} bin {peak,4}: {wmp[peak],3} / {ours[peak],3}" +
                              $"            {skirtRes,6:0.0} over {skirt.Count,4} bins" +
                              $"     {BassSum(wmp),4} / {BassSum(ours),4}");
        }

        foreach (var (name, lo) in new[] { ("1k 0dB", 40), ("100Hz -12dB", 0), ("60Hz -12dB", 0) })
        {
            var s = Array.FindIndex(TestSignal.Segments, x => x.Name == name);
            var set = samples.Where(x => x.Segment == s).ToList();
            if (set.Count == 0) continue;
            var wmp = MeanBytes(set.Select(x => x.Wmp));
            var ours = MeanBytes(set.Select(x => Encode(x.Bass, pinned)));
            Console.WriteLine();
            Console.WriteLine($"{name} bins {lo}..{lo + 13}:");
            Console.WriteLine("  wmp  " + string.Join(" ", Enumerable.Range(lo, 14).Select(k => $"{wmp[k],3}")));
            Console.WriteLine("  ours " + string.Join(" ", Enumerable.Range(lo, 14).Select(k => $"{ours[k],3}")));
        }
        return 0;
    }

    private static byte[] Encode(float[] m, double offset)
    {
        var v = new byte[1024];
        for (var k = 0; k < 1024; k++)
            v[k] = m[k] <= 0 ? (byte)0 : (byte)Math.Clamp(Math.Round(PowerSlope * Math.Log10(m[k]) + offset), 0, 255);
        return v;
    }

    private static int BassSum(byte[] v) => v[1] + v[3] + v[5] + v[2] + v[4] + v[6];

    private static byte[] MeanBytes(IEnumerable<byte[]> rows)
    {
        var list = rows.ToList();
        var mean = new byte[1024];
        for (var k = 0; k < 1024; k++) mean[k] = (byte)Math.Round(list.Average(r => (double)r[k]));
        return mean;
    }

    private static (double A, double B) Fit(IEnumerable<(double X, double Y)> data)
    {
        var d = data.ToList();
        var mx = d.Average(p => p.X);
        var my = d.Average(p => p.Y);
        var sxy = d.Sum(p => (p.X - mx) * (p.Y - my));
        var sxx = d.Sum(p => (p.X - mx) * (p.X - mx));
        var a = sxy / sxx;
        return (a, my - a * mx);
    }
}
