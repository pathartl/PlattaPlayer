using PlattaPlayer.Wmp.Harness.Interop;
using PlattaPlayer.Wmp.Harness.Verbs;

namespace PlattaPlayer.Wmp.Harness;

internal static class Program
{
    /// <summary>
    /// mpvis.DLL registers with ThreadingModel = Apartment and both target effects use GDI/GDI+, so the
    /// whole harness runs single-threaded-apartment on one thread. That also matters for the ucrt
    /// rand() seed, which is per-thread.
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        var hr = NativeMethods.CoInitializeEx(0, NativeMethods.COINIT_APARTMENTTHREADED);
        if (hr < 0)
        {
            Console.Error.WriteLine($"CoInitializeEx failed: 0x{hr:X8}");
            return 2;
        }

        try
        {
            var verb = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
            var rest = args.Skip(1).ToArray();

            return verb switch
            {
                "selftest" => SelfTest.Run(rest),
                "probe" => Probe.Run(rest),
                "synth" => SynthVerb.Run(rest),
                "capture" => Capture.Run(rest),
                "randprobe" => RandProbe.Run(rest),
                "measure" => Measure.Run(rest),
                "measure-fps" => MeasureFps.Run(rest),
                "dump-lut" => DumpLut.Run(rest),
                "render-ours" => RenderOurs.Run(rest),
                "diff" => Diff.Run(rest),
                "dump-bars-vtable" => DumpBarsVtable.Run(rest),
                "sweep-alchemy" => SweepAlchemy.Run(rest),
                "sweep-ours" => SweepOurs.Run(rest),
                "verify-warps" => VerifyWarps.Run(rest),
                "verify-battery-shifts" => VerifyBatteryShifts.Run(rest),
                "verify-battery-surface" => VerifyBatterySurface.Run(rest),
                "verify-battery-renders" => VerifyBatteryRenders.Run(rest),
                "verify-battery" => VerifyBattery.Run(rest),
                "verify-battery-gpu" => VerifyBatteryGpu.Run(rest),
                "render-battery-gpu" => VerifyBatteryGpu.Render(rest),
                "sweep-battery-gpu" => VerifyBatteryGpu.Sweep(rest),
                "verify-audio" => VerifyAudio.Run(rest),
                "verify-color" => VerifyColor.Run(rest),
                "verify-renders" => VerifyRenders.Run(rest),
                "verify-gpu" => VerifyGpu.Run(rest),
                "render-gpu" => VerifyGpu.Render(rest),
                "sweep-gpu" => VerifyGpu.Sweep(rest),
                "verify-offsets" => VerifyOffsets.Run(rest),
                "verify-spline" => VerifySpline.Run(rest),
                "verify-line" => VerifyLine.Run(rest),
                "verify-plot" => VerifyPlot.Run(rest),
                "verify-disc" => VerifyDisc.Run(rest),
                "verify-feedback" => VerifyFeedback.Run(rest),
                "verify-map" => VerifyMap.Run(rest),
                "verify-chord" => VerifyChord.Run(rest),
                "verify-scheduler" => VerifyScheduler.Run(rest),
                "verify-normalrender" => VerifyNormalRender.Run(rest),
                "probe-ctors" => ProbeCtors.Run(rest),
                "verify-alchemy" => VerifyAlchemy.Run(rest),
                "time-alchemy" => TimeAlchemy.Run(rest),
                "capture-timedlevel" => CaptureTimedLevel.Run(rest),
                "analyze-timedlevel" => AnalyzeTimedLevel.Run(rest),
                "calibrate-timedlevel" => CalibrateTimedLevel.Run(rest),
                "oracle-timedlevel" => OracleTimedLevel.Run(rest),
                "verify-analyzer" => VerifyAnalyzer.Run(rest),
                "dump-schedule" => DumpSchedule.Run(rest),
                "isolate-renderers" => IsolateRenderers.Run(rest),
                "warp-profile" => WarpProfile.Run(rest),
                "help" or "--help" or "-h" => Help(),
                _ => Unknown(verb),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"FAILED: {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
        finally
        {
            NativeMethods.CoUninitialize();
        }
    }

    private static int Help()
    {
        Console.WriteLine("""
            PlattaPlayer WMP visualization ground-truth harness (dev-only, x64).

            Verbs:
              selftest    Check the GDI surface behaves like WMP's (32 bpp DIB, readback round-trip).
              probe       Identify the real effects: Alchemy via COM, Bars/Battery via wmp.dll's
                          private creator table. Runs the preset self-check gate.
              synth       Generate the deterministic audio sequence both sides render.
              capture <alchemy|barswaves|battery> [--preset N] [--size WxH] [--png 0,1,2] [--verify]
                          Render the sequence through the REAL effect and record every frame.
            """);
        return 0;
    }

    private static int Unknown(string verb)
    {
        Console.Error.WriteLine($"Unknown verb '{verb}'. Try 'help'.");
        return 2;
    }
}
