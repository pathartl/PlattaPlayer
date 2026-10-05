using PlattaPlayer.Visualizations.Wmp;
using PlattaPlayer.Visualizations.Wmp.Alchemy;
using PlattaPlayer.Visualizations.Wmp.Alchemy.Gpu;
using PlattaPlayer.Visualizations.Wmp.Alchemy.Plugin;
using PlattaPlayer.Visualizations.Wmp.Audio;
using PlattaPlayer.Wmp.Harness.Imaging;
using PlattaPlayer.Wmp.Harness.Interop;
using PlattaPlayer.Wmp.Harness.Synth;

namespace PlattaPlayer.Wmp.Harness.Verbs;

/// <summary>
/// Checks the window-resolution GPU Alchemy against the exact CPU port.
///
/// <c>verify-gpu</c> runs <see cref="AlchemyCore"/> and <see cref="AlchemyGpuEngine"/> in lock-step on one
/// seed and one audio stream, at 640x480, where the GPU path runs the original's exact arithmetic:
/// <list type="number">
/// <item><b>Engine:</b> the two must make the same choices on every frame and end on the same random
/// draw. The GPU engine runs the renderers' own code into a recorder, so any divergence is a bug.</item>
/// <item><b>Feedback, one step:</b> every frame the CPU's previous field is uploaded and the GPU's
/// gather + diffusion runs on it with no overlay. The result is compared with replaying the CPU's own
/// warp map through <see cref="FeedbackPass"/>. Float trig on the GPU against double on the CPU can
/// move the odd pixel at a truncation boundary, and nothing more should differ.</item>
/// <item><b>Overlay, one step:</b> the same, with the strokes and discs, against the CPU's actual next
/// field. The GPU draws strokes as continuous bands rather than chains of plots, so this is measured,
/// not required to match.</item>
/// </list>
///
/// <c>render-gpu --size WxH</c> runs the GPU path from black at any size and writes PNGs, with the CPU
/// field at 640x480 alongside (the same frames when the size is 4:3) and running coverage statistics.
///
/// Usage: verify-gpu [--frames N] [--seed N] [--pin-warp K] [--pin-draw K]
///        render-gpu [--size WxH] [--frames N] [--seed N] [--every N]
/// </summary>
internal static class VerifyGpu
{
    private const double Fps = 1000.0 / WmpFrameRate.WindowedIntervalMs;

    public static int Run(string[] args)
    {
        if (!TryLoadAudio("verify-gpu", out var audio)) return 2;
        var frames = ArgInt(args, "--frames", 1500);
        var seed = ArgInt(args, "--seed", 1);
        int? pinWarp = ArgOpt(args, "--pin-warp");
        int? pinDraw = ArgOpt(args, "--pin-draw");
        var sources = args.Contains("--sources");
        const int w = 640, h = 480;

        var cpuRandom = new Random(seed);
        var gpuRandom = new Random(seed);
        var core = new AlchemyCore(cpuRandom);
        core.Resize(w, h);
        var engine = new AlchemyGpuEngine(gpuRandom);
        engine.Resize(w, h);
        if (pinWarp is not null || pinDraw is not null)
        {
            core.Pin(pinWarp, pinDraw);
            engine.Pin(pinWarp, pinDraw);
        }

        using var gl = new WglContext();
        var renderer = new AlchemyGlRenderer(new GlBindings(gl.GetProcAddress), isGles: false);
        renderer.Init();
        var scale = AlchemyFieldScale.For(w, h);
        renderer.EnsureSize(scale);
        if (!scale.IsExact) throw new InvalidOperationException("640x480 must run exact.");

        var frame = new AlchemyGpuFrame();
        var scaler = new GpuWarpScaler();
        var levels = new TimedLevels();
        var prev = new int[w * h];
        var expected = new int[w * h];
        var scratch = new int[w * h];
        var upload = new byte[w * h * 4];
        var readback = new byte[w * h * 4];

        var nameMismatches = 0;
        var feedbackFramesOff = 0;
        long feedbackPixelsOff = 0;
        var worstFeedback = (Frame: -1, Pixels: 0);
        var overlayDiff = new List<double>();
        var overlayFar = new List<double>();
        var reported = 0;
        long sourcesOff = 0;
        var sourceFrames = 0;
        var sourceReports = 0;
        var maxSourceDelta = 0;

        for (var f = 0; f < frames; f++)
        {
            Load(levels, audio[f % audio.Count]);
            Array.Copy(core.Pixels, prev, prev.Length);

            core.Update(levels, Fps);
            var advanced = engine.Update(levels, Fps, frame);
            if (!advanced) continue;
            scaler.Update(frame, scale.Scale);

            if (core.CurrentName != frame.CurrentName)
            {
                if (nameMismatches++ < 5)
                    Console.WriteLine($"  frame {f}: CPU '{core.CurrentName}' vs GPU '{frame.CurrentName}'");
            }

            // The warp itself: the GPU's source pixel for every destination against the CPU's map.
            if (sources && core.LastMap is { } cpuMap)
            {
                renderer.ReadWarpSources(frame, scaler, readback);
                var offHere = 0;
                for (var i = 0; i < cpuMap.Length; i++)
                {
                    var gx = readback[i * 4] * 256 + readback[i * 4 + 1];
                    var gy = readback[i * 4 + 2] * 256 + readback[i * 4 + 3];
                    var (cx, cy) = (cpuMap[i] % w, cpuMap[i] / w);
                    if (gx == cx && gy == cy) continue;
                    offHere++;
                    maxSourceDelta = Math.Max(maxSourceDelta, Math.Max(Math.Abs(gx - cx), Math.Abs(gy - cy)));
                    if (sourceReports++ < 12)
                        Console.WriteLine($"  frame {f}: dest ({i % w},{i / w}) cpu src ({cx},{cy}) gpu src ({gx},{gy}); " +
                                          $"warp {Describe(frame.Warp)}{(frame.Morphing ? $" morph {frame.MorphIndex} from {Describe(scaler.From)}" : "")}");
                }
                if (offHere > 0) sourceFrames++;
                sourcesOff += offHere;
            }

            // Feedback alone.
            Array.Copy(prev, expected, prev.Length);
            if (core.LastMap is { } map)
                FeedbackPass.GatherAndDecay(expected, scratch, map, w, h, unchecked((int)0xFF000000));
            ToRgba(prev, upload);
            renderer.UploadFrame(upload);
            renderer.Step(frame, scaler, drawOverlay: false);
            renderer.ReadFrame(readback);
            var off = CountOff(expected, readback, w, out var firstOff);
            if (off > 0)
            {
                feedbackFramesOff++;
                feedbackPixelsOff += off;
                if (off > worstFeedback.Pixels) worstFeedback = (f, off);
                if (reported++ < 8)
                {
                    var (x, y) = (firstOff % w, firstOff / w);
                    Console.WriteLine($"  frame {f}: {off} feedback pixel(s) differ, first at ({x},{y}) " +
                                      $"cpu {expected[firstOff] & 0xFFFFFF:X6} gpu {Rgb(readback, firstOff):X6}; " +
                                      $"warp {Describe(frame.Warp)}{(frame.Morphing ? $" morph {frame.MorphIndex}" : "")}");
                }
            }

            // With the overlay, against the CPU's real next field.
            renderer.UploadFrame(upload);
            renderer.Step(frame, scaler);
            renderer.ReadFrame(readback);
            var (mean, far) = Difference(core.Pixels, readback);
            overlayDiff.Add(mean);
            overlayFar.Add(far);
        }

        var cpuNext = cpuRandom.Next();
        var gpuNext = gpuRandom.Next();
        renderer.Dispose();

        Console.WriteLine();
        Console.WriteLine($"verify-gpu: {frames} frames, seed {seed}, 640x480 (exact)");
        Console.WriteLine($"  engine lock-step   : {(nameMismatches == 0 && cpuNext == gpuNext ? "OK" : "DIVERGED")} " +
                          $"(name mismatches {nameMismatches}, next draw cpu {cpuNext} gpu {gpuNext})");
        if (sources)
            Console.WriteLine($"  warp sources       : {sourceFrames} frame(s) with differences, {sourcesOff:N0} pixel(s), largest offset {maxSourceDelta}");
        Console.WriteLine($"  feedback one-step  : {feedbackFramesOff} frame(s) with differences, {feedbackPixelsOff:N0} pixel(s) total" +
                          (worstFeedback.Frame >= 0 ? $", worst frame {worstFeedback.Frame} ({worstFeedback.Pixels} px)" : ""));
        if (overlayDiff.Count > 0)
        {
            overlayDiff.Sort();
            overlayFar.Sort();
            Console.WriteLine($"  overlay one-step   : mean |diff| per channel median {overlayDiff[overlayDiff.Count / 2]:0.000}, " +
                              $"p95 {overlayDiff[(int)(overlayDiff.Count * 0.95)]:0.000}; pixels off by >16: median " +
                              $"{overlayFar[overlayFar.Count / 2] * 100:0.00}%, p95 {overlayFar[(int)(overlayFar.Count * 0.95)] * 100:0.00}%");
        }
        return nameMismatches == 0 && cpuNext == gpuNext ? 0 : 1;
    }

    public static int Render(string[] args)
    {
        if (!TryLoadAudio("render-gpu", out var audio)) return 2;
        var (w, h) = Capture.ArgSize(args, "--size", 1920, 1440);
        var frames = ArgInt(args, "--frames", 600);
        var seed = ArgInt(args, "--seed", 1);
        var every = ArgInt(args, "--every", 100);
        int? pinWarp = ArgOpt(args, "--pin-warp");
        int? pinDraw = ArgOpt(args, "--pin-draw");

        var core = new AlchemyCore(new Random(seed));
        core.Resize(640, 480);
        var engine = new AlchemyGpuEngine(new Random(seed));
        var scale = AlchemyFieldScale.For(w, h);
        engine.Resize(scale.FieldWidth, scale.FieldHeight);
        if (pinWarp is not null || pinDraw is not null)
        {
            core.Pin(pinWarp, pinDraw);
            engine.Pin(pinWarp, pinDraw);
        }

        using var gl = new WglContext();
        var renderer = new AlchemyGlRenderer(new GlBindings(gl.GetProcAddress), isGles: false);
        renderer.Init();
        renderer.EnsureSize(scale);

        var tag = (pinWarp is null ? "" : $"-w{pinWarp}") + (pinDraw is null ? "" : $"-d{pinDraw}");
        var dir = Path.Combine(HarnessPaths.Root, "gpu", $"{w}x{h}-s{seed}{tag}");
        Directory.CreateDirectory(dir);
        Console.WriteLine($"render-gpu: field {scale.FieldWidth}x{scale.FieldHeight} at scale {scale.Scale:0.###} -> {w}x{h}; writing to {dir}");
        Console.WriteLine("  frame   gpu ink%  cpu ink%   gpu mean  cpu mean   name");

        var frame = new AlchemyGpuFrame();
        var scaler = new GpuWarpScaler();
        var levels = new TimedLevels();
        var rgba = new byte[w * h * 4];
        var cpuBgra = new byte[640 * 480 * 4];
        var times = new List<double>();
        var sw = new System.Diagnostics.Stopwatch();

        for (var f = 1; f <= frames; f++)
        {
            Load(levels, audio[(f - 1) % audio.Count]);
            core.Update(levels, Fps);
            if (engine.Update(levels, Fps, frame))
            {
                scaler.Update(frame, scale.Scale);
                sw.Restart();
                renderer.Step(frame, scaler);
                renderer.ReadFrame(rgba); // forces the frame to finish, so the time is real
                sw.Stop();
                times.Add(sw.Elapsed.TotalMilliseconds);
            }

            if (f % every != 0) continue;
            renderer.ReadFrame(rgba);
            var (gpuInk, gpuMean) = Coverage(rgba);
            Buffer.BlockCopy(core.Pixels, 0, cpuBgra, 0, cpuBgra.Length);
            var (cpuInk, cpuMean) = CoverageBgra(cpuBgra);
            Console.WriteLine($"  {f,5}   {gpuInk * 100,7:0.0}  {cpuInk * 100,8:0.0}   {gpuMean,8:0.0}  {cpuMean,8:0.0}   {frame.CurrentName} [{Describe(frame.Warp)}]");

            for (var i = 0; i < rgba.Length; i += 4) (rgba[i], rgba[i + 2]) = (rgba[i + 2], rgba[i]);
            PngWriter.WriteBgra(Path.Combine(dir, $"gpu-{f:D5}.png"), rgba, w, h);
            PngWriter.WriteBgra(Path.Combine(dir, $"cpu-{f:D5}.png"), cpuBgra, 640, 480);
        }

        renderer.Dispose();
        if (times.Count > 10)
        {
            times.Sort();
            Console.WriteLine($"  GPU step incl. read-back: median {times[times.Count / 2]:0.00} ms, p95 {times[(int)(times.Count * 0.95)]:0.00} ms");
        }
        return 0;
    }

    /// <summary>
    /// Distribution check: over many seeds, the per-frame ink coverage and brightness of the CPU field and
    /// of the GPU path at each size. Feedback is chaotic, so single runs part ways after a few hundred
    /// frames whatever the renderer; the distributions are what must agree.
    ///
    /// Usage: sweep-gpu [--sizes 640x480,1920x1440,1920x1080] [--seeds N] [--frames N] [--every N]
    /// </summary>
    public static int Sweep(string[] args)
    {
        if (!TryLoadAudio("sweep-gpu", out var audio)) return 2;
        var seeds = ArgInt(args, "--seeds", 8);
        var frames = ArgInt(args, "--frames", 1500);
        var every = ArgInt(args, "--every", 10);
        var sizesArg = "640x480,1920x1440,1920x1080";
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == "--sizes") sizesArg = args[i + 1];
        var sizes = sizesArg.Split(',').Select(p => p.Split('x')).Select(p => (W: int.Parse(p[0]), H: int.Parse(p[1]))).ToList();

        using var gl = new WglContext();
        var renderer = new AlchemyGlRenderer(new GlBindings(gl.GetProcAddress), isGles: false);
        renderer.Init();
        var levels = new TimedLevels();

        Console.WriteLine($"sweep-gpu: {seeds} seeds x {frames} frames, sampled every {every}");
        Console.WriteLine("  source          ink% p25/p50/p75        mean p25/p50/p75");

        void Report(string label, List<double> ink, List<double> mean)
        {
            ink.Sort();
            mean.Sort();
            double Q(List<double> l, double q) => l[Math.Min(l.Count - 1, (int)(q * l.Count))];
            Console.WriteLine($"  {label,-14}  {Q(ink, .25) * 100,5:0.0} {Q(ink, .5) * 100,5:0.0} {Q(ink, .75) * 100,5:0.0}" +
                              $"      {Q(mean, .25),5:0.0} {Q(mean, .5),5:0.0} {Q(mean, .75),5:0.0}");
        }

        {
            var ink = new List<double>();
            var mean = new List<double>();
            var bgra = new byte[640 * 480 * 4];
            for (var seed = 1; seed <= seeds; seed++)
            {
                var core = new AlchemyCore(new Random(seed));
                core.Resize(640, 480);
                for (var f = 1; f <= frames; f++)
                {
                    Load(levels, audio[(f - 1) % audio.Count]);
                    core.Update(levels, Fps);
                    if (f % every != 0 || f < 100) continue;
                    Buffer.BlockCopy(core.Pixels, 0, bgra, 0, bgra.Length);
                    var (i, m) = Coverage(bgra);
                    ink.Add(i);
                    mean.Add(m);
                }
            }
            Report("cpu 640x480", ink, mean);
        }

        foreach (var (w, h) in sizes)
        {
            var scale = AlchemyFieldScale.For(w, h);
            var ink = new List<double>();
            var mean = new List<double>();
            var rgba = new byte[w * h * 4];
            for (var seed = 1; seed <= seeds; seed++)
            {
                var engine = new AlchemyGpuEngine(new Random(seed));
                engine.Resize(scale.FieldWidth, scale.FieldHeight);
                renderer.EnsureSize(scale);
                renderer.Clear();
                var frame = new AlchemyGpuFrame();
                var scaler = new GpuWarpScaler();
                for (var f = 1; f <= frames; f++)
                {
                    Load(levels, audio[(f - 1) % audio.Count]);
                    if (engine.Update(levels, Fps, frame))
                    {
                        scaler.Update(frame, scale.Scale);
                        renderer.Step(frame, scaler);
                    }
                    if (f % every != 0 || f < 100) continue;
                    renderer.ReadFrame(rgba);
                    var (i, m) = Coverage(rgba);
                    ink.Add(i);
                    mean.Add(m);
                }
            }
            Report($"gpu {w}x{h}", ink, mean);
        }

        renderer.Dispose();
        return 0;
    }

    private static bool TryLoadAudio(string verb, out List<AudioFrame> audio)
    {
        audio = [];
        if (!File.Exists(HarnessPaths.SynthFile))
        {
            Console.Error.WriteLine($"{verb}: {HarnessPaths.SynthFile} not found — run 'synth' first.");
            return false;
        }
        audio = AudioFrameSet.Read(HarnessPaths.SynthFile).Where(a => a.State == 2).ToList();
        return true;
    }

    private static void Load(TimedLevels levels, AudioFrame a)
    {
        a.Frequency0.CopyTo(levels.Frequency[0], 0);
        a.Frequency1.CopyTo(levels.Frequency[1], 0);
        a.Waveform0.CopyTo(levels.Waveform[0], 0);
        a.Waveform1.CopyTo(levels.Waveform[1], 0);
        levels.State = a.State;
    }

    private static void ToRgba(int[] argb, byte[] rgba)
    {
        for (var i = 0; i < argb.Length; i++)
        {
            var v = argb[i];
            rgba[i * 4] = (byte)(v >> 16);
            rgba[i * 4 + 1] = (byte)(v >> 8);
            rgba[i * 4 + 2] = (byte)v;
            rgba[i * 4 + 3] = 255;
        }
    }

    private static int Rgb(byte[] rgba, int i) => (rgba[i * 4] << 16) | (rgba[i * 4 + 1] << 8) | rgba[i * 4 + 2];

    private static int CountOff(int[] argb, byte[] rgba, int w, out int first)
    {
        first = -1;
        var off = 0;
        for (var i = 0; i < argb.Length; i++)
        {
            if ((argb[i] & 0xFFFFFF) == Rgb(rgba, i)) continue;
            if (first < 0) first = i;
            off++;
        }
        return off;
    }

    /// <summary>Mean absolute channel difference, and the fraction of pixels off by more than 16 on any channel.</summary>
    private static (double Mean, double Far) Difference(int[] argb, byte[] rgba)
    {
        long sum = 0;
        var far = 0;
        for (var i = 0; i < argb.Length; i++)
        {
            var v = argb[i];
            var dr = Math.Abs(((v >> 16) & 0xFF) - rgba[i * 4]);
            var dg = Math.Abs(((v >> 8) & 0xFF) - rgba[i * 4 + 1]);
            var db = Math.Abs((v & 0xFF) - rgba[i * 4 + 2]);
            sum += dr + dg + db;
            if (dr > 16 || dg > 16 || db > 16) far++;
        }
        return (sum / (3.0 * argb.Length), far / (double)argb.Length);
    }

    /// <summary>Ink = any channel above 24; mean = average of the channel means.</summary>
    private static (double Ink, double Mean) Coverage(byte[] rgba)
    {
        long ink = 0, sum = 0;
        var n = rgba.Length / 4;
        for (var i = 0; i < rgba.Length; i += 4)
        {
            int r = rgba[i], g = rgba[i + 1], b = rgba[i + 2];
            sum += r + g + b;
            if (r > 24 || g > 24 || b > 24) ink++;
        }
        return (ink / (double)n, sum / (3.0 * n));
    }

    private static (double Ink, double Mean) CoverageBgra(byte[] bgra) => Coverage(bgra);

    private static string Describe(GpuWarpSet set)
    {
        var a = set.A.Kind == GpuWarpKind.OScope ? $"OScope[{set.A.Params[0]}/{set.A.ChildKind}]" : set.A.Kind.ToString();
        return set.HasB ? $"{a}+{set.B.Kind}" : a;
    }

    private static int ArgInt(string[] args, string name, int fallback) => ArgOpt(args, name) ?? fallback;

    private static int? ArgOpt(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(args[i + 1], out var v)) return v;
        return null;
    }
}
