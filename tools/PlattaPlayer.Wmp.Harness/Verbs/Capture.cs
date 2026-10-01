using PlattaPlayer.Wmp.Harness.Imaging;
using PlattaPlayer.Wmp.Harness.Interop;
using PlattaPlayer.Wmp.Harness.Synth;

namespace PlattaPlayer.Wmp.Harness.Verbs;

/// <summary>
/// Drives a real WMP visualization over the synthetic sequence and records every frame.
///
/// Usage: capture &lt;alchemy|barswaves|battery&gt; [--preset N] [--size WxH] [--png a,b,c] [--verify]
///
/// <c>--verify</c> runs the whole capture twice and compares content hashes. That is how the
/// reproducibility question gets answered — in particular whether seeding ucrtbase's per-thread RNG
/// tames the +/-10 px jitter wmp.dll's Bars adds to every bar when TimedLevel.state == 2.
/// </summary>
internal static class Capture
{
    /// <summary>
    /// Fixed seed pushed into ucrtbase's per-thread rand() state before each capture run. wmp.dll
    /// reaches rand() through the api-ms-win-crt-* forwarders, which land in the same ucrtbase this
    /// call targets, so if the seed is genuinely shared the jitter sequence becomes reproducible.
    /// </summary>
    private const uint RandSeed = 1;

    /// <summary>Seed pinned into mpvis.DLL for Alchemy captures; -1 leaves the effect unpatched.</summary>
    private static int AlchemySeed = -1;

    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("capture: expected an effect (alchemy | barswaves | battery).");
            return 2;
        }

        var effect = args[0].ToLowerInvariant();
        var preset = ArgInt(args, "--preset", 0);
        var (w, h) = ArgSize(args, "--size", 640, 480);
        var verify = args.Contains("--verify");
        var pngs = ArgIntList(args, "--png");
        var freshState = ArgInt(args, "--fresh-state", 2);
        AlchemySeed = ArgInt(args, "--seed", -1);
        if (effect == "alchemy" && AlchemySeed >= 0)
        {
            Console.WriteLine($"   before: {AlchemySeedPatch.Describe()}");
            AlchemySeedPatch.Apply((uint)AlchemySeed);
            Console.WriteLine($"   after:  {AlchemySeedPatch.Describe()}");
        }

        if (!File.Exists(HarnessPaths.SynthFile))
        {
            Console.Error.WriteLine($"capture: {HarnessPaths.SynthFile} not found — run the 'synth' verb first.");
            return 2;
        }
        var audio = AudioFrameSet.Read(HarnessPaths.SynthFile);

        Console.WriteLine($"capturing {effect} preset {preset} at {w}x{h} over {audio.Count} frames" +
                          (freshState != 2 ? $" (fresh frames sent as state {freshState})" : ""));

        var set = CaptureOnce(effect, preset, w, h, audio, freshState);
        var path = HarnessPaths.FrameSetFile(HarnessPaths.TruthDir, effect, preset, w, h, freshState);
        set.Write(path);

        Console.WriteLine($"  wrote {path}");
        Console.WriteLine($"  hash {set.ContentHash()}   frames that changed: {set.DistinctConsecutiveFrames()}/{set.Frames.Count - 1}");

        if (pngs.Count > 0)
        {
            var dir = Path.Combine(HarnessPaths.TruthDir, $"{effect}-p{preset}-{w}x{h}");
            set.WritePngs(dir, pngs);
            Console.WriteLine($"  wrote {pngs.Count} png(s) to {dir}");
        }

        if (verify)
        {
            // Re-run in a CHILD PROCESS rather than re-instantiating in this one. mpvis.DLL carries
            // module-level state across instantiations, so a second object in the same process starts
            // from a different point even with the seed pinned — which looks exactly like
            // non-determinism but is not. A fresh process is the honest comparison, and it is also how
            // the captures are actually produced and consumed.
            var same = RepeatInChildProcess(path, set.ContentHash(), out var detail);
            Console.WriteLine();
            Console.WriteLine(same
                ? "  [ok] capture is reproducible across processes — a bit-exact comparison is possible."
                : $"  [!!] capture is NOT reproducible: {detail}\n" +
                  "       Fall back to the structural gate for this effect.");
            if (!same) return 1;
        }

        return 0;
    }

    /// <summary>
    /// Re-runs this capture in a fresh process and compares the written frame set against
    /// <paramref name="expectedHash"/>.
    /// </summary>
    private static bool RepeatInChildProcess(string path, string expectedHash, out string detail)
    {
        var exe = Environment.ProcessPath;
        if (exe is null) { detail = "could not determine this process's executable path"; return false; }

        var original = File.ReadAllBytes(path);
        var args = Environment.GetCommandLineArgs().Skip(1)
            .Where(a => !string.Equals(a, "--verify", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var psi = new System.Diagnostics.ProcessStartInfo(exe) { RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var child = System.Diagnostics.Process.Start(psi);
        if (child is null) { detail = "could not start the child process"; return false; }
        child.StandardOutput.ReadToEnd();
        child.WaitForExit();
        if (child.ExitCode != 0) { detail = $"the child capture exited with {child.ExitCode}"; return false; }

        var repeated = File.ReadAllBytes(path);
        if (repeated.AsSpan().SequenceEqual(original)) { detail = ""; return true; }

        // Restore the first run's bytes so a failed verify does not leave a half-trusted artefact.
        File.WriteAllBytes(path, original);
        detail = $"a fresh process produced different frames (first run hashed {expectedHash})";
        return false;
    }

    private static ImageFrameSet CaptureOnce(
        string effect, int preset, int w, int h, List<AudioFrame> audio, int freshState)
    {
        using var dib = new DibTarget(w, h);
        if (dib.BitsPerPixel != 32)
            throw new InvalidOperationException($"Surface reports {dib.BitsPerPixel} bpp; the effects need 32.");

        VirtualClock.Reset();
        using var fx = CreateEffect(effect, preset);
        using var levels = new TimedLevelBuffer();

        // Seed AFTER construction: wmp.dll's Battery creator calls srand() itself, which would otherwise
        // clobber whatever we set.
        NativeMethods.UcrtSrand(RandSeed);

        var set = new ImageFrameSet(w, h);
        var rc = new RECT(0, 0, w, h);

        foreach (var frame in audio)
        {
            frame.Frequency0.CopyTo(levels.Frequency0);
            frame.Frequency1.CopyTo(levels.Frequency1);
            frame.Waveform0.CopyTo(levels.Waveform0);
            frame.Waveform1.CopyTo(levels.Waveform1);
            levels.State = frame.State == 2 ? freshState : frame.State;
            levels.TimeStamp = frame.TimeStamp;

            unsafe { fx.Render(levels.Pointer, dib.Hdc, ref rc); }
            VirtualClock.AdvanceFrame();
            set.Add(dib.Snapshot());
        }

        return set;
    }

    private static WmpEffect CreateEffect(string effect, int preset)
    {
        switch (effect)
        {
            case "alchemy":
            {
                // Alchemy seeds itself from the clock, so without pinning the seed no two runs agree and
                // it cannot serve as a frame-exact oracle. -1 means "leave it alone".
                var fx = AlchemySeed < 0
                    ? AlchemyFactory.Create()
                    : AlchemyFactory.CreateSeeded((uint)AlchemySeed);
                // Alchemy has one preset and SetCurrentPreset is a no-op; call it anyway so the capture
                // path is identical for all three effects.
                fx.SetCurrentPreset(preset);
                return fx;
            }
            case "barswaves":
            {
                var fx = WmpInternalFactory.CreateInstance(
                    WmpInternalFactory.Resolve(WmpGuids.ClsidBarsAndWaves));
                var hr = fx.SetCurrentPreset(preset);
                if (hr < 0) { fx.Dispose(); throw new InvalidOperationException($"SetCurrentPreset({preset}) -> 0x{hr:X8}"); }
                return fx;
            }
            case "battery":
            {
                var fx = WmpInternalFactory.CreateInstance(
                    WmpInternalFactory.Resolve(WmpGuids.ClsidBattery));
                var hr = fx.SetCurrentPreset(preset);
                if (hr < 0) { fx.Dispose(); throw new InvalidOperationException($"SetCurrentPreset({preset}) -> 0x{hr:X8}"); }
                return fx;
            }
            default:
                throw new ArgumentException($"Unknown effect '{effect}' (expected alchemy | barswaves | battery).");
        }
    }

    // ---- argument helpers ------------------------------------------------------------------------

    private static string? ArgValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }

    private static int ArgInt(string[] args, string name, int fallback)
        => int.TryParse(ArgValue(args, name), out var v) ? v : fallback;

    private static List<int> ArgIntList(string[] args, string name)
    {
        var raw = ArgValue(args, name);
        if (string.IsNullOrWhiteSpace(raw)) return [];
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                  .Select(int.Parse).ToList();
    }

    public static (int W, int H) ArgSize(string[] args, string name, int dw, int dh)
    {
        var raw = ArgValue(args, name);
        if (string.IsNullOrWhiteSpace(raw)) return (dw, dh);
        var parts = raw.Split('x', 'X');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var w) || !int.TryParse(parts[1], out var h))
            throw new ArgumentException($"{name} expects WxH, got '{raw}'.");
        return (w, h);
    }
}
