using PlattaPlayer.Visualizations.Wmp.Alchemy;
using PlattaPlayer.Visualizations.Wmp.Audio;
using PlattaPlayer.Wmp.Harness.Imaging;
using PlattaPlayer.Wmp.Harness.Interop;
using PlattaPlayer.Wmp.Harness.Synth;

namespace PlattaPlayer.Wmp.Harness.Verbs;

/// <summary>
/// END TO END: the real <c>CToleranceVis</c>, created through COM and rendered frame by frame, against
/// <see cref="AlchemyCore"/>. One rand() script drives both from construction onward, and both see the
/// same synthetic audio. After every frame it compares the draw count and every pixel of the FIELD.
///
/// The real field is read straight from its surface (<c>*(iface + 0x8f0)</c>: bits at <c>+0xa8</c>, width
/// <c>+0x30</c>, height <c>+0x34</c>), which is the surface the renderers draw into (<c>renderData+0x48</c>).
/// That keeps the comparison independent of how the host presents it.
///
/// This works only because the rest is already exact. Construction draws are matched (probe-ctors), and
/// the scheduler, the renderers and every pixel primitive have their own verifiers. So the first
/// divergence here points at what is still unverified: the warp stage's kernel lifecycle and its
/// morph, or the order things run in within a frame. At that frame it writes real / ours / diff PNGs
/// to <c>artifacts/report/alchemy-e2e/</c> and prints both schedulers' state.
///
/// Usage: verify-alchemy [--frames N] [--sets N] [--seed BASE]
/// </summary>
internal static unsafe class VerifyAlchemy
{
    private const int InterfaceToSurface = 0x8f0;
    private const int ScriptLength = 1 << 22;

    public static int Run(string[] args)
    {
        if (!File.Exists(HarnessPaths.SynthFile))
        {
            Console.Error.WriteLine($"verify-alchemy: {HarnessPaths.SynthFile} not found — run 'synth' first.");
            return 2;
        }

        var frames = ArgInt(args, "--frames", 600);
        var sets = ArgInt(args, "--sets", 1);
        var seedBase = ArgInt(args, "--seed", 11000);
        var audio = AudioFrameSet.Read(HarnessPaths.SynthFile);

        Console.WriteLine(MpvisModule.Describe());
        Console.WriteLine(RandRedirect.SelfTest());
        // --managed-math points the DLL's sin/cos/atan2/sqrt at .NET's, to CLASSIFY a residual: if it
        // disappears, it was a last-bit libm difference rather than a logic one.
        if (args.Any(a => string.Equals(a, "--managed-math", StringComparison.OrdinalIgnoreCase)))
        {
            MathRedirect.Apply();
            Console.WriteLine("managed math: mpvis sin/cos/atan2/sqrt redirected to .NET");
        }
        Console.WriteLine();

        var dir = Path.Combine(HarnessPaths.ReportDir, "alchemy-e2e");
        Directory.CreateDirectory(dir);

        var failures = 0;
        for (var set = 0; set < sets; set++)
            if (!RunOne(seedBase + set, frames, audio, dir)) failures++;

        MathRedirect.Restore();
        RandRedirect.Restore();
        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "verify-alchemy: the whole effect renders exactly what the real one renders."
            : $"verify-alchemy: {failures} of {sets} script(s) DIVERGE — see above and {dir}.");
        return failures == 0 ? 0 : 1;
    }

    private static bool RunOne(int seed, int frames, List<AudioFrame> audio, string dir)
    {
        var script = RandRedirect.MakeScript(seed, ScriptLength);

        RandRedirect.SetScript(script);
        VirtualClock.Reset();
        using var fx = AlchemyFactory.CreateSeeded(0);
        var constructed = RandRedirect.Position;

        var scripted = new ScriptedRandom(script);
        var core = new AlchemyCore(scripted);
        if (scripted.Draws != constructed)
        {
            Console.WriteLine($"  seed {seed}: construction draws real {constructed} ours {scripted.Draws}");
            return false;
        }

        using var dib = new DibTarget(640, 480);
        using var buffer = new TimedLevelBuffer();
        var levels = new TimedLevels();
        var rc = new RECT(0, 0, 640, 480);
        var introspect = new AlchemyIntrospect(fx.RawPointer);
        var compared = 0;

        // The synthetic sequence is looped so a run can outlast effect lifetimes and the render cycle.
        for (var f = 0; f < frames; f++)
        {
            var a = audio[f % audio.Count];
            a.Frequency0.CopyTo(buffer.Frequency0);
            a.Frequency1.CopyTo(buffer.Frequency1);
            a.Waveform0.CopyTo(buffer.Waveform0);
            a.Waveform1.CopyTo(buffer.Waveform1);
            buffer.State = a.State;
            buffer.TimeStamp = a.TimeStamp;
            fx.Render(buffer.Pointer, dib.Hdc, ref rc);
            VirtualClock.AdvanceFrame();

            var surface = *(byte**)(fx.RawPointer + InterfaceToSurface);
            var w = *(int*)(surface + 0x30);
            var h = *(int*)(surface + 0x34);
            var bits = *(int**)(surface + 0xa8);
            if (core.Width != w || core.Height != h) core.Resize(w, h);

            a.Frequency0.CopyTo(levels.Frequency[0], 0);
            a.Frequency1.CopyTo(levels.Frequency[1], 0);
            a.Waveform0.CopyTo(levels.Waveform[0], 0);
            a.Waveform1.CopyTo(levels.Waveform[1], 0);
            levels.State = a.State;
            core.Update(levels, 62.5);

            if (a.State != 2) continue;
            compared++;

            var ours = core.Pixels;
            var diff = 0;
            var first = -1;
            for (var i = 0; i < w * h; i++)
            {
                if (((bits[i] ^ ours[i]) & 0xFFFFFF) == 0) continue;
                diff++;
                if (first < 0) first = i;
            }

            var drawsOk = RandRedirect.Position == scripted.Draws;
            if (diff == 0 && drawsOk) continue;

            Console.WriteLine($"  seed {seed}: DIVERGES at frame {f} ({compared} compared): {diff} pixel(s) differ, " +
                              $"rand() draws real {RandRedirect.Position} ours {scripted.Draws} " +
                              $"(construction {constructed})");
            var listed = 0;
            for (var i = 0; i < w * h && listed < 12; i++)
            {
                if (((bits[i] ^ ours[i]) & 0xFFFFFF) == 0) continue;
                listed++;
                Console.WriteLine($"    pixel ({i % w},{i / w}): real #{bits[i] & 0xFFFFFF:X6} ours #{ours[i] & 0xFFFFFF:X6}");
            }
            DescribeSchedulers(introspect, core.Scheduler);

            var real = new int[w * h];
            for (var i = 0; i < real.Length; i++) real[i] = bits[i];
            Dump(dir, $"seed{seed}-f{f}", real, ours, w, h);
            return false;
        }

        Console.WriteLine($"  seed {seed}: {compared} frames exact, {RandRedirect.Position} draws " +
                          $"(construction {constructed}); now showing '{core.CurrentName}'");
        return true;
    }

    private static void DescribeSchedulers(AlchemyIntrospect real, EffectScheduler ours)
    {
        var slots = real.ReadSlots();
        for (var i = 0; i < slots.Length && i < ours.Steps.Count; i++)
        {
            var step = ours.Steps[i];
            var ourActive = step.Active.Select(e => ours.Pool.ToList().IndexOf(e));
            Console.WriteLine($"    step {i}: countdown real {slots[i].Countdown,4} ours {step.Countdown,4}   " +
                              $"holds real [{string.Join(",", real.SlotActive(i))}] ours [{string.Join(",", ourActive)}]");
        }
        Console.WriteLine($"    ours: warp A {ours.WarpA?.Name ?? "-"}, warp B {ours.WarpB?.Name ?? "-"}, " +
                          $"morphing {ours.Stage?.Transitioning}");
    }

    private static void Dump(string dir, string stem, int[] real, int[] ours, int w, int h)
    {
        var bgraReal = new byte[w * h * 4];
        var bgraOurs = new byte[w * h * 4];
        var bgraDiff = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            Put(bgraReal, i, real[i]);
            Put(bgraOurs, i, ours[i]);
            var r = real[i] & 0xFFFFFF;
            var o = ours[i] & 0xFFFFFF;
            Put(bgraDiff, i, r == o ? 0 : r != 0 && o == 0 ? 0xFF0000 : r == 0 ? 0x00FF00 : 0xFFFF00);
        }
        PngWriter.WriteBgra(Path.Combine(dir, stem + "-real.png"), bgraReal, w, h);
        PngWriter.WriteBgra(Path.Combine(dir, stem + "-ours.png"), bgraOurs, w, h);
        PngWriter.WriteBgra(Path.Combine(dir, stem + "-diff.png"), bgraDiff, w, h);
    }

    private static void Put(byte[] bgra, int i, int c)
    {
        bgra[i * 4 + 0] = (byte)c;
        bgra[i * 4 + 1] = (byte)(c >> 8);
        bgra[i * 4 + 2] = (byte)(c >> 16);
        bgra[i * 4 + 3] = 0xFF;
    }

    private static int ArgInt(string[] args, string name, int fallback)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(args[i + 1], out var v)) return v;
        return fallback;
    }
}
