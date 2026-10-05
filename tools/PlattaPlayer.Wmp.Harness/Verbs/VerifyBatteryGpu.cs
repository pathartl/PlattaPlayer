using PlattaPlayer.Visualizations.Wmp;
using PlattaPlayer.Visualizations.Wmp.Battery;
using PlattaPlayer.Visualizations.Wmp.Battery.Gpu;
using PlattaPlayer.Visualizations.Wmp.Battery.Plugin;
using PlattaPlayer.Wmp.Harness.Imaging;
using PlattaPlayer.Wmp.Harness.Interop;
using PlattaPlayer.Wmp.Harness.Synth;

namespace PlattaPlayer.Wmp.Harness.Verbs;

/// <summary>
/// Checks the window-resolution GPU Battery against the exact CPU port.
///
/// <c>verify-battery-gpu</c> runs two <see cref="BatteryCore"/>s from one seed on one audio stream: one on
/// the CPU raster, one through <see cref="BatteryGpuEngine"/> (the recorder) and the plugin's
/// <see cref="BatteryGlRenderer"/> in an offscreen WGL context at 384x288, where every GPU pass is meant
/// to be exact. Every frame it compares the rand() draw count, every pixel of the field (the index the
/// GPU keeps in R), and every presented pixel (the GPU's palette pass against the CPU's
/// <see cref="BatteryCore.CopyTo"/>, or the stop fill). A run is play with a pause every 97 frames, then
/// the 300-frame stop fade and the fill after it. <c>--mutations</c> also proves the check can fail: a
/// broken exact blur must be caught, and the scaled blur's loss λ must make no difference at scale 1.
///
/// <c>render-battery-gpu --size WxH</c> runs the GPU path at any size and writes PNGs of it and of the CPU
/// field (stretched as the original stretches it), with field statistics and the GPU step's timing.
///
/// <c>sweep-battery-gpu</c> is the fidelity test away from scale 1, where single runs part ways: over seeds
/// × presets it compares the distributions (p25/p50/p75) of the mean palette index, the share of pixels
/// above index 16, and the mean luminance, at each size against the CPU.
///
/// Usage: verify-battery-gpu [--presets a,b,c] [--frames N] [--seed N] [--mutations]
///        render-battery-gpu [--size WxH] [--preset N] [--frames N] [--seed N] [--every N] [--loss L]
///        sweep-battery-gpu [--sizes WxH,...] [--presets a,b,c] [--seeds N] [--frames N] [--every N] [--loss L]
/// </summary>
internal static class VerifyBatteryGpu
{
    private const int W = BatteryCore.FieldWidth;
    private const int H = BatteryCore.FieldHeight;

    public static int Run(string[] args)
    {
        if (!TryLoadAudio("verify-battery-gpu", out var audio)) return 2;
        var frames = VerifyWarps.ArgInt(args, "--frames", 1000);
        var seed = VerifyWarps.ArgInt(args, "--seed", 1000);
        var presets = ParseList(VerifyWarps.ArgString(args, "--presets"), [0, 2, 5, 9, 13, 17, 21, 25]);

        using var gl = new WglContext();
        var renderer = new BatteryGlRenderer(new GlBindings(gl.GetProcAddress), isGles: false);
        renderer.Init();

        var failures = 0;
        foreach (var p in presets)
        {
            var result = RunPreset(renderer, audio, p, frames, (uint)(seed + 97 * p));
            Console.WriteLine(result);
            if (!result.StartsWith("  ok", StringComparison.Ordinal)) failures++;
        }

        if (args.Contains("--mutations"))
        {
            var mutationFrames = Math.Min(frames, 400);
            renderer.BlurLoss = 0.7f;
            var loss = RunPreset(renderer, audio, 0, mutationFrames, (uint)seed);
            renderer.BlurLoss = 0.1f;
            var lossOk = loss.StartsWith("  ok", StringComparison.Ordinal);
            Console.WriteLine($"  {(lossOk ? "ok  " : "FAIL")} mutation: blur loss 0.7 at scale 1 {(lossOk ? "changes nothing" : "CHANGED the field: " + loss.Trim())}");

            renderer.MutateExactBlur = true;
            var broken = RunPreset(renderer, audio, 0, mutationFrames, (uint)seed);
            renderer.MutateExactBlur = false;
            var caught = !broken.StartsWith("  ok", StringComparison.Ordinal);
            Console.WriteLine($"  {(caught ? "ok  " : "FAIL")} mutation: broken blur corner {(caught ? "caught:" + broken.Replace("  FAIL", "") : "NOT caught")}");
            if (!lossOk) failures++;
            if (!caught) failures++;
        }

        renderer.Dispose();
        Console.WriteLine(failures == 0 ? "verify-battery-gpu: ALL EXACT" : $"verify-battery-gpu: {failures} failure(s)");
        return failures == 0 ? 0 : 1;
    }

    private static string RunPreset(BatteryGlRenderer renderer, List<AudioFrame> audio, int preset, int frames, uint seed)
    {
        var cpu = new BatteryCore(seed: seed);
        using var gpu = new BatteryGpuEngine(seed: seed);
        cpu.SetCurrentPreset(preset);
        gpu.Core.SetCurrentPreset(preset);
        var scale = BatteryFieldScale.For(W, H);
        if (!scale.IsExact) throw new InvalidOperationException("384x288 must run exact.");
        gpu.Resize(scale);
        renderer.EnsureSize(scale);
        renderer.Clear();

        var levels = new BatteryLevels();
        var field = new byte[W * H * 4];
        var shown = new byte[W * H * 4];
        var cpuPixels = new int[W * H];
        long vertices = 0;
        var gathers = 0;
        var transitions = 0;

        var total = frames + 310;
        for (var f = 0; f < total; f++)
        {
            Load(levels, audio[f % audio.Count]);
            levels.State = f >= frames ? 0 : f % 97 == 96 ? 1 : 2;

            var visible = cpu.Render(levels);
            var frame = gpu.Render(levels);
            renderer.Step(frame, gpu.Tables);
            vertices += frame.VertexCount;
            foreach (var c in frame.Commands)
            {
                if (c.Op != BatteryGpuOp.Gather) continue;
                gathers++;
                if (c.Step >= 0) transitions++;
            }

            var where = $"preset {preset} ({cpu.PresetTitle(preset)}) frame {f} state {levels.State}";
            if (cpu.Rand.Draws != gpu.Core.Rand.Draws)
                return $"  FAIL {where}: rand draws cpu {cpu.Rand.Draws} gpu {gpu.Core.Rand.Draws}";
            if (visible != frame.Visible)
                return $"  FAIL {where}: visible cpu {visible} gpu {frame.Visible}";

            renderer.ReadField(field);
            var bits = cpu.RenderData.Front.Bits;
            for (var i = 0; i < bits.Length; i++)
                if (field[i * 4] != bits[i])
                    return $"  FAIL {where}: field ({i % W},{i / W}) cpu {bits[i]} gpu {field[i * 4]}";

            renderer.ReadPresented(shown);
            if (visible)
            {
                cpu.CopyTo(cpuPixels);
                for (var i = 0; i < cpuPixels.Length; i++)
                {
                    var want = cpuPixels[i] & 0xFFFFFF;
                    var got = shown[i * 4] << 16 | shown[i * 4 + 1] << 8 | shown[i * 4 + 2];
                    if (want != got)
                        return $"  FAIL {where}: presented ({i % W},{i / W}) index {bits[i]} cpu #{want:X6} gpu #{got:X6}";
                }
            }
            else
            {
                var c = cpu.StopFillColor;
                var want = (int)((c & 0xFF) << 16 | (c & 0xFF00) | (c >> 16) & 0xFF);
                var got = shown[0] << 16 | shown[1] << 8 | shown[2];
                if (want != got) return $"  FAIL {where}: stop fill cpu #{want:X6} gpu #{got:X6}";
            }
        }

        return $"  ok   preset {preset,2} {cpu.PresetTitle(preset),-28} {total} frames, {gathers} gathers ({transitions} in transitions), " +
               $"{vertices / total} vertices/frame, {cpu.Rand.Draws} draws";
    }

    public static int Render(string[] args)
    {
        if (!TryLoadAudio("render-battery-gpu", out var audio)) return 2;
        var (w, h) = Capture.ArgSize(args, "--size", 1920, 1080);
        var frames = VerifyWarps.ArgInt(args, "--frames", 600);
        var seed = (uint)VerifyWarps.ArgInt(args, "--seed", 1000);
        var every = VerifyWarps.ArgInt(args, "--every", 100);
        var preset = VerifyWarps.ArgInt(args, "--preset", 0);
        var loss = VerifyWarps.ArgString(args, "--loss");

        var cpu = new BatteryCore(seed: seed);
        using var gpu = new BatteryGpuEngine(seed: seed);
        cpu.SetCurrentPreset(preset);
        gpu.Core.SetCurrentPreset(preset);
        var scale = BatteryFieldScale.For(w, h);
        gpu.Resize(scale);

        using var gl = new WglContext();
        var renderer = new BatteryGlRenderer(new GlBindings(gl.GetProcAddress), isGles: false);
        renderer.Init();
        if (loss is not null) renderer.BlurLoss = float.Parse(loss, System.Globalization.CultureInfo.InvariantCulture);
        renderer.EnsureSize(scale);
        renderer.Clear();

        var dir = Path.Combine(HarnessPaths.Root, "battery-gpu", $"{w}x{h}-p{preset}-s{seed}");
        Directory.CreateDirectory(dir);
        Console.WriteLine($"render-battery-gpu: '{cpu.PresetTitle(preset)}', field {scale.FieldWidth}x{scale.FieldHeight} " +
                          $"at scale {scale.Scale:0.###} -> {w}x{h}{(scale.IsExact ? " (exact)" : "")}; writing to {dir}");
        Console.WriteLine("  frame    gpu idx  cpu idx   gpu >16%  cpu >16%   gpu lum  cpu lum");

        var levels = new BatteryLevels();
        var field = new byte[w * h * 4];
        var shown = new byte[w * h * 4];
        var cpuPixels = new int[W * H];
        var cpuShown = new byte[w * h * 4];
        var times = new List<double>();
        var waits = new List<double>();
        var sw = new System.Diagnostics.Stopwatch();

        for (var f = 1; f <= frames; f++)
        {
            Load(levels, audio[(f - 1) % audio.Count]);
            cpu.Render(levels);
            sw.Restart();
            var frame = gpu.Render(levels);
            var engineMs = sw.Elapsed.TotalMilliseconds;
            renderer.Step(frame, gpu.Tables);
            renderer.ReadField(field); // forces the frame to finish, so the time is real
            sw.Stop();
            times.Add(sw.Elapsed.TotalMilliseconds);
            waits.Add(engineMs);

            if (f % every != 0) continue;
            var g = Stats(field, 4, frame.Palette);
            var c = Stats(cpu.RenderData.Front.Bits, 1, cpu.PresentedPalette);
            Console.WriteLine($"  {f,5}   {g.Index,7:0.0}  {c.Index,7:0.0}   {g.Lit * 100,8:0.0}  {c.Lit * 100,8:0.0}   {g.Luma,7:0.0}  {c.Luma,7:0.0}");

            renderer.ReadPresented(shown);
            ToBgra(shown);
            PngWriter.WriteBgra(Path.Combine(dir, $"gpu-{f:D5}.png"), shown, w, h);
            cpu.CopyTo(cpuPixels);
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var v = cpuPixels[y * H / h * W + x * W / w];
                var o = (y * w + x) * 4;
                cpuShown[o] = (byte)v;
                cpuShown[o + 1] = (byte)(v >> 8);
                cpuShown[o + 2] = (byte)(v >> 16);
                cpuShown[o + 3] = 255;
            }
            PngWriter.WriteBgra(Path.Combine(dir, $"cpu-{f:D5}.png"), cpuShown, w, h);
        }

        renderer.Dispose();
        times.Sort();
        waits.Sort();
        Console.WriteLine($"  step (engine + GPU + read-back): median {times[times.Count / 2]:0.00} ms, p95 {times[(int)(times.Count * 0.95)]:0.00} ms, " +
                          $"max {times[^1]:0.00} ms; engine alone median {waits[waits.Count / 2]:0.00} ms, max {waits[^1]:0.00} ms");
        return 0;
    }

    public static int Sweep(string[] args)
    {
        if (!TryLoadAudio("sweep-battery-gpu", out var audio)) return 2;
        var seeds = VerifyWarps.ArgInt(args, "--seeds", 3);
        var frames = VerifyWarps.ArgInt(args, "--frames", 1000);
        var every = VerifyWarps.ArgInt(args, "--every", 10);
        var presets = ParseList(VerifyWarps.ArgString(args, "--presets"), [0, 3, 7, 11, 15, 19, 23]);
        var sizes = (VerifyWarps.ArgString(args, "--sizes") ?? "1280x720,1920x1080,2560x1440")
            .Split(',').Select(p => p.Split('x')).Select(p => (W: int.Parse(p[0]), H: int.Parse(p[1]))).ToList();
        var loss = VerifyWarps.ArgString(args, "--loss");

        using var gl = new WglContext();
        var renderer = new BatteryGlRenderer(new GlBindings(gl.GetProcAddress), isGles: false);
        renderer.Init();
        if (loss is not null) renderer.BlurLoss = float.Parse(loss, System.Globalization.CultureInfo.InvariantCulture);
        var levels = new BatteryLevels();

        Console.WriteLine($"sweep-battery-gpu: {seeds} seeds x presets {string.Join(",", presets)} x {frames} frames, " +
                          $"sampled every {every} from frame 100, blur loss {renderer.BlurLoss}");
        Console.WriteLine("  source             mean index p25/p50/p75      >16% p25/p50/p75      luminance p25/p50/p75");

        void Report(string label, Samples s)
        {
            Console.WriteLine($"  {label,-17}  {Q(s.Index, .25),6:0.0} {Q(s.Index, .5),6:0.0} {Q(s.Index, .75),6:0.0}" +
                              $"     {Q(s.Lit, .25) * 100,5:0.0} {Q(s.Lit, .5) * 100,5:0.0} {Q(s.Lit, .75) * 100,5:0.0}" +
                              $"     {Q(s.Luma, .25),6:0.0} {Q(s.Luma, .5),6:0.0} {Q(s.Luma, .75),6:0.0}");
        }

        // The CPU port at a field size: 384x288 is the original; a widened field is the reference for the
        // GPU at a non-4:3 size, since a wider field alone changes the statistics.
        Samples Cpu(int fw, int fh)
        {
            var samples = new Samples();
            foreach (var p in presets)
            for (var k = 0; k < seeds; k++)
            {
                var cpu = new BatteryCore(seed: (uint)(1000 + 7919 * k + 97 * p));
                cpu.SetCurrentPreset(p);
                cpu.Resize(fw, fh);
                for (var f = 1; f <= frames; f++)
                {
                    Load(levels, audio[(f - 1) % audio.Count]);
                    cpu.Render(levels);
                    if (f % every == 0 && f >= 100) samples.Add(Stats(cpu.RenderData.Front.Bits, 1, cpu.PresentedPalette));
                }
            }
            return samples;
        }

        Report($"cpu {W}x{H}", Cpu(W, H));
        var fields = new HashSet<(int, int)> { (W, H) };

        foreach (var (w, h) in sizes)
        {
            var scale = BatteryFieldScale.For(w, h);
            if (fields.Add((scale.FieldWidth, scale.FieldHeight)))
                Report($"cpu {scale.FieldWidth}x{scale.FieldHeight}", Cpu(scale.FieldWidth, scale.FieldHeight));
            renderer.EnsureSize(scale);
            var field = new byte[w * h * 4];
            var samples = new Samples();
            foreach (var p in presets)
            for (var k = 0; k < seeds; k++)
            {
                using var gpu = new BatteryGpuEngine(seed: (uint)(1000 + 7919 * k + 97 * p));
                gpu.Core.SetCurrentPreset(p);
                gpu.Resize(scale);
                renderer.Clear();
                for (var f = 1; f <= frames; f++)
                {
                    Load(levels, audio[(f - 1) % audio.Count]);
                    var frame = gpu.Render(levels);
                    renderer.Step(frame, gpu.Tables);
                    if (f % every != 0 || f < 100) continue;
                    renderer.ReadField(field);
                    samples.Add(Stats(field, 4, frame.Palette));
                }
            }
            Report($"gpu {w}x{h}", samples);
        }

        renderer.Dispose();
        return 0;
    }

    private sealed class Samples
    {
        public List<double> Index { get; } = [];
        public List<double> Lit { get; } = [];
        public List<double> Luma { get; } = [];

        public void Add((double Index, double Lit, double Luma) s)
        {
            Index.Add(s.Index);
            Lit.Add(s.Lit);
            Luma.Add(s.Luma);
        }
    }

    private static double Q(List<double> l, double q)
    {
        l.Sort();
        return l.Count == 0 ? double.NaN : l[Math.Min(l.Count - 1, (int)(q * l.Count))];
    }

    /// <summary>Mean palette index, the share of pixels above 16, and the mean luminance through the
    /// palette. <paramref name="stride"/> is 1 for a CPU field and 4 for a GPU read-back.</summary>
    private static (double Index, double Lit, double Luma) Stats(byte[] indices, int stride, uint[] palette)
    {
        Span<double> luma = stackalloc double[256];
        for (var i = 0; i < 256; i++)
        {
            var e = palette[i];
            luma[i] = 0.299 * (e & 0xFF) + 0.587 * ((e >> 8) & 0xFF) + 0.114 * ((e >> 16) & 0xFF);
        }
        long sum = 0, lit = 0;
        double l = 0;
        var n = indices.Length / stride;
        for (var i = 0; i < indices.Length; i += stride)
        {
            var v = indices[i];
            sum += v;
            if (v > 16) lit++;
            l += luma[v];
        }
        return (sum / (double)n, lit / (double)n, l / n);
    }

    private static void ToBgra(byte[] rgba)
    {
        for (var i = 0; i < rgba.Length; i += 4) (rgba[i], rgba[i + 2]) = (rgba[i + 2], rgba[i]);
    }

    private static int[] ParseList(string? list, int[] fallback) =>
        list is null ? fallback : list.Split(',').Select(int.Parse).ToArray();

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

    private static void Load(BatteryLevels levels, AudioFrame a)
    {
        a.Frequency0.CopyTo(levels.Bytes, BatteryLevels.Freq0);
        a.Frequency1.CopyTo(levels.Bytes, BatteryLevels.Freq1);
        a.Waveform0.CopyTo(levels.Bytes, BatteryLevels.Wave0);
        a.Waveform1.CopyTo(levels.Bytes, BatteryLevels.Wave1);
        levels.State = a.State;
    }
}
