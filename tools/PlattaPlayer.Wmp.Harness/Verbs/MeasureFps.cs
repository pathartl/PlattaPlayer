using System.Diagnostics;
using PlattaPlayer.Wmp.Harness.Imaging;
using PlattaPlayer.Wmp.Harness.Interop;

namespace PlattaPlayer.Wmp.Harness.Verbs;

/// <summary>
/// Measures how fast the REAL Windows Media Player repaints its visualization.
///
/// The rate is not statically determinable: wmp.dll drives the windowed pane with
/// <c>SetTimer(hwnd, 0x10e1, 1000/fps)</c> (FUN_1804728f0), but the fps field defaults to -1 and is
/// only ever written through a COM/vtable property with no static caller. So it has to be observed.
///
/// Method: play a generated tone in wmplayer.exe, poll the window with PrintWindow far faster than any
/// plausible repaint rate, and count how often the pixels actually change. Transitions are classified
/// by how much of the window changed so that the once-per-second clock text does not get mistaken for
/// a visualization frame.
/// </summary>
internal static class MeasureFps
{
    private const int PollHz = 240;
    private const int DefaultSeconds = 20;

    /// <summary>A frame counts as a visualization repaint only if at least this share of pixels moved.</summary>
    private const double VizChangeThreshold = 0.002;

    public static int Run(string[] args)
    {
        var seconds = args.Contains("--seconds")
            ? int.Parse(args[Array.IndexOf(args, "--seconds") + 1]) : DefaultSeconds;
        var keepOpen = args.Contains("--keep-open");

        var wav = Path.Combine(Path.GetTempPath(), "plattaplayer-fps-probe.wav");
        WriteToneWav(wav, seconds + 25);
        Console.WriteLine($"   wrote probe audio {wav}");

        var exe = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Windows Media Player", "wmplayer.exe");
        if (!File.Exists(exe))
        {
            Console.Error.WriteLine($"   {exe} not found.");
            return 2;
        }

        foreach (var stale in Process.GetProcessesByName("wmplayer")) TryKill(stale);

        Console.WriteLine($"   launching {exe} /play");
        var proc = Process.Start(new ProcessStartInfo(exe, $"/play \"{wav}\"") { UseShellExecute = false });
        if (proc is null) { Console.Error.WriteLine("   failed to start wmplayer."); return 1; }

        try
        {
            var hwnd = WaitForMainWindow(proc, TimeSpan.FromSeconds(20));
            if (hwnd == 0) { Console.Error.WriteLine("   wmplayer never produced a main window."); return 1; }

            NativeMethods.SetForegroundWindow(hwnd);
            Thread.Sleep(4000); // let it settle into Now Playing and start the visualization

            // PrintWindow cost scales with window area, and at the default size it was itself capping
            // the sample rate near the signal being measured. Shrinking the player buys the headroom
            // that makes transition counting valid. The visualization pane simply renders smaller; its
            // repaint cadence is driven by a timer and is unaffected.
            NativeMethods.SetWindowPos(hwnd, 0, 0, 0, 420, 360,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            Thread.Sleep(2000);

            NativeMethods.GetClientRect(hwnd, out var rc);
            var w = rc.Width;
            var h = rc.Height;
            Console.WriteLine($"   window 0x{hwnd:X} client area {w}x{h}");
            if (w < 16 || h < 16) { Console.Error.WriteLine("   client area too small to measure."); return 1; }

            // Transition counting is only valid while the poll rate exceeds the repaint rate. Rather
            // than assert that, measure at several deliberately different poll rates: a saturated
            // estimate tracks the poll rate, a genuine one does not.
            //
            // The rates must be INTERLEAVED in short slices, not run back to back. A visualization's
            // frame-to-frame change depends on the audio, so consecutive passes over different parts of
            // the track measure different phenomena — that confound produced wildly disagreeing numbers
            // and looked exactly like saturation.
            var estimates = Interleaved(hwnd, w, h, seconds, [40, 90, PollHz]);
            if (estimates.Count == 0) { Console.Error.WriteLine("   PrintWindow produced nothing."); return 1; }
            Conclude(estimates);
            return 0;
        }
        finally
        {
            if (!keepOpen) TryKill(proc);
            try { File.Delete(wav); } catch { /* best effort */ }
        }
    }

    private record Sample(double Seconds, double ChangedFraction, byte[]? Pixels);

    /// <summary>
    /// Every Nth pixel in each axis is hashed rather than every pixel. Comparing 561k pixels per poll
    /// was itself capping the sample rate at ~84 Hz, which is close enough to the signal to saturate
    /// the transition count and turn the estimate into a meaningless lower bound. A visualization
    /// changes across the whole surface, so a sparse lattice detects it just as reliably.
    /// </summary>
    private const int SampleStride = 6;

    private static List<Sample> Poll(nint hwnd, int w, int h, int seconds, int targetHz)
    {
        using var dib = new DibTarget(w, h);
        var samples = new List<Sample>();
        var lattice = BuildLattice(w, h);
        var previous = new uint[lattice.Length];
        var current = new uint[lattice.Length];
        var havePrevious = false;

        var clock = Stopwatch.StartNew();
        var interval = TimeSpan.FromSeconds(1.0 / targetHz);
        var next = TimeSpan.Zero;
        var blanks = 0;
        var keptFull = 0;

        while (clock.Elapsed.TotalSeconds < seconds)
        {
            var now = clock.Elapsed;
            if (now < next) { Thread.SpinWait(50); continue; }
            next = now + interval;

            if (!NativeMethods.PrintWindow(hwnd, dib.Hdc, NativeMethods.PW_RENDERFULLCONTENT))
            {
                blanks++;
                continue;
            }

            var px = dib.Pixels;
            for (var i = 0; i < lattice.Length; i++)
            {
                var o = lattice[i];
                current[i] = (uint)(px[o] | (px[o + 1] << 8) | (px[o + 2] << 16));
            }

            var changed = 0.0;
            if (havePrevious)
            {
                var diff = 0;
                for (var i = 0; i < current.Length; i++) if (current[i] != previous[i]) diff++;
                changed = (double)diff / current.Length;
            }

            // Keep a couple of full frames purely so a human can confirm we were watching a
            // visualization and not, say, a static album-art pane.
            byte[]? full = null;
            if (changed > 0.05 && keptFull < 2) { full = dib.Snapshot(); keptFull++; }

            samples.Add(new Sample(now.TotalSeconds, changed, full));
            (previous, current) = (current, previous);
            havePrevious = true;
        }

        if (blanks > 0) Console.WriteLine($"   ({blanks} PrintWindow call(s) failed and were skipped)");
        return samples;
    }

    private static int[] BuildLattice(int w, int h)
    {
        var offsets = new List<int>((w / SampleStride + 1) * (h / SampleStride + 1));
        for (var y = 0; y < h; y += SampleStride)
            for (var x = 0; x < w; x += SampleStride)
                offsets.Add((y * w + x) * 4);
        return [.. offsets];
    }

    /// <summary>
    /// Cycles the poll rates in one-second slices so every rate samples the same distribution of
    /// on-screen activity, then aggregates per rate.
    /// </summary>
    private static List<(double Poll, double Fps)> Interleaved(
        nint hwnd, int w, int h, int seconds, int[] rates)
    {
        var polls = new double[rates.Length];
        var repaints = new double[rates.Length];
        var elapsed = new double[rates.Length];

        var rounds = Math.Max(2, seconds);
        for (var round = 0; round < rounds; round++)
        {
            for (var r = 0; r < rates.Length; r++)
            {
                var s = Poll(hwnd, w, h, 1, rates[r]);
                if (s.Count < 2) continue;
                var dt = s[^1].Seconds - s[0].Seconds;
                if (dt <= 0) continue;
                polls[r] += s.Count;
                repaints[r] += s.Skip(1).Count(x => x.ChangedFraction >= VizChangeThreshold);
                elapsed[r] += dt;
            }
        }

        var results = new List<(double, double)>();
        for (var r = 0; r < rates.Length; r++)
        {
            if (elapsed[r] <= 0) continue;
            var pollRate = polls[r] / elapsed[r];
            var fps = repaints[r] / elapsed[r];
            var saturation = repaints[r] / Math.Max(1, polls[r]);
            Console.WriteLine($"   poll {rates[r],3} Hz target -> {pollRate,6:F1} Hz actual | " +
                              $"{repaints[r],6:F0} repaints over {elapsed[r],5:F1}s -> {fps,6:F2} fps | " +
                              $"saturation {saturation,4:P0}");
            results.Add((pollRate, fps));
        }
        return results;
    }

    private static (double Poll, double Fps) Report(List<Sample> samples, int w, int h, int targetHz)
    {
        var elapsed = samples[^1].Seconds - samples[0].Seconds;
        var vizChange = samples.Skip(1).Count(s => s.ChangedFraction >= VizChangeThreshold);
        var pollRate = samples.Count / elapsed;
        var fps = vizChange / elapsed;
        var saturation = vizChange / (double)Math.Max(1, samples.Count - 1);

        Console.WriteLine($"   poll {targetHz,3} Hz target -> {pollRate,6:F1} Hz actual | " +
                          $"{vizChange,5} repaints -> {fps,6:F2} fps | saturation {saturation,4:P0}");

        // Keep a couple of captures so a human can confirm we were watching a visualization.
        var dir = Path.Combine(HarnessPaths.ReportDir, "fps");
        var interesting = samples.Where(s => s.Pixels is not null).Take(2).ToList();
        for (var i = 0; i < interesting.Count; i++)
            PngWriter.WriteBgra(Path.Combine(dir, $"wmp-window-{i}.png"), interesting[i].Pixels!, w, h);

        return (pollRate, fps);
    }

    private static void Conclude(List<(double Poll, double Fps)> estimates)
    {
        Console.WriteLine();
        if (estimates.All(e => e.Fps <= 0))
        {
            Console.WriteLine("   No visualization-scale repaints were observed. Most likely the player is not");
            Console.WriteLine("   showing a visualization (wrong view, or PrintWindow cannot capture its surface).");
            Console.WriteLine("   Re-run with --keep-open, switch to Now Playing with a visualization, and try again.");
            return;
        }

        // Convergence, not overall agreement, is the test. The slowest poll rate is EXPECTED to
        // saturate — it is the control that shows what saturation looks like. What matters is whether
        // the estimate stops moving as the poll rate keeps rising: once raising it further changes
        // nothing, the signal is resolved.
        var byPoll = estimates.OrderByDescending(e => e.Poll).ToList();
        if (byPoll.Count < 2)
        {
            Console.WriteLine("   Not enough poll rates to test convergence.");
            return;
        }

        var top = byPoll[0].Fps;
        var second = byPoll[1].Fps;
        var converged = Math.Abs(top - second) / Math.Max(top, second);

        Console.WriteLine($"   fastest two poll rates ({byPoll[0].Poll:F0} Hz and {byPoll[1].Poll:F0} Hz) " +
                          $"differ by {converged:P1}");
        if (converged > 0.10)
        {
            Console.WriteLine("   [!] The estimate is still moving with the poll rate, so it remains a lower bound.");
            Console.WriteLine($"       Best available lower bound: {top:F1} fps.");
            return;
        }

        Console.WriteLine("   The estimate has converged: raising the poll rate no longer changes it.");
        Console.WriteLine();
        Console.WriteLine($"   MEASURED: WMP repaints its visualization at {top:F1} fps ({1000.0 / top:F2} ms period).");
        Console.WriteLine();
        Console.WriteLine("   wmp.dll arms the pane with SetTimer(hwnd, 0x10e1, 1000/fps) — integer division — so an");
        Console.WriteLine("   fps property of 60 yields a 16 ms interval, i.e. 62.5 fps nominal. Observed rates sit");
        Console.WriteLine("   a little under that because the renderers skip work when the TimedLevel timestamp has");
        Console.WriteLine("   not advanced (wmp.dll FUN_18041def0 sets a skip-redraw flag), so some timer ticks");
        Console.WriteLine("   produce an identical frame and are invisible to a pixel-difference probe. A ~16 ms tick");
        Console.WriteLine("   is therefore the right target, and it is an UPPER bound on what is observable.");
        Console.WriteLine("   NOTE the practical consequence: WMP is NOT slow. Our GPU visualizers free-run on");
        Console.WriteLine("   RequestNextFrameRendering, so on a high-refresh display they animate several times");
        Console.WriteLine("   too fast. Capping to ~16 ms is the fix, not slowing the effects down.");
    }

    private static nint WaitForMainWindow(Process proc, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            proc.Refresh();
            if (proc.MainWindowHandle != 0 && NativeMethods.IsWindowVisible(proc.MainWindowHandle))
                return proc.MainWindowHandle;

            // /play on an already-running player hands off to the existing instance and exits, so look
            // for any wmplayer process, not only the one we started.
            foreach (var other in Process.GetProcessesByName("wmplayer"))
                if (other.MainWindowHandle != 0 && NativeMethods.IsWindowVisible(other.MainWindowHandle))
                    return other.MainWindowHandle;

            Thread.Sleep(250);
        }
        return 0;
    }

    private static void TryKill(Process p)
    {
        try { if (!p.HasExited) { p.Kill(); p.WaitForExit(3000); } } catch { /* best effort */ }
    }

    /// <summary>A plain 16-bit PCM sine sweep, so the visualization has something to react to.</summary>
    private static void WriteToneWav(string path, int seconds)
    {
        const int rate = 44100;
        var samples = rate * seconds;
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);

        var dataBytes = samples * 2;
        bw.Write("RIFF"u8); bw.Write(36 + dataBytes); bw.Write("WAVE"u8);
        bw.Write("fmt "u8); bw.Write(16); bw.Write((short)1); bw.Write((short)1);
        bw.Write(rate); bw.Write(rate * 2); bw.Write((short)2); bw.Write((short)16);
        bw.Write("data"u8); bw.Write(dataBytes);

        for (var i = 0; i < samples; i++)
        {
            var t = (double)i / rate;
            // Sweep 80 Hz -> 6 kHz over 8 s and repeat, with an amplitude pulse, so every band lights up.
            var f = 80 * Math.Pow(75, t % 8 / 8.0);
            var env = 0.35 + 0.65 * Math.Abs(Math.Sin(2 * Math.PI * t / 1.5));
            bw.Write((short)(Math.Sin(2 * Math.PI * f * t) * env * short.MaxValue * 0.8));
        }
    }
}
