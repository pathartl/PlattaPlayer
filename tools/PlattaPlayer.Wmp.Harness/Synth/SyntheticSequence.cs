namespace PlattaPlayer.Wmp.Harness.Synth;

/// <summary>
/// Generates the deterministic audio sequence both sides of the comparison run on.
///
/// Everything is a pure function of the frame index; the only "randomness" is a fixed 64-bit LCG with a
/// constant seed, so regenerating the file always produces identical bytes. The segments are chosen to
/// exercise the specific behaviours the decompile says exist — the silence path, the log band edges at
/// both extremes, bar falloff, peak-hold expiry, the trail ring, the paused/reuse state, and both of
/// Scope's index paths — rather than to sound like music.
/// </summary>
internal static class SyntheticSequence
{
    public const int FrameCount = 600;

    /// <summary>Nominal 30 fps in 100 ns units, matching the TimedLevel timestamp domain.</summary>
    private const long TicksPerFrame = 10_000_000L / 30;

    public static List<AudioFrame> Generate()
    {
        var frames = new List<AudioFrame>(FrameCount);
        var lcg = new Lcg(0x9E3779B97F4A7C15UL);

        for (var n = 0; n < FrameCount; n++)
        {
            var f = new AudioFrame { TimeStamp = n * TicksPerFrame };

            if (n < 30)
            {
                // Stopped. The host zeroes both blocks itself, so the renderer's silence path is what
                // is under test (Bars fills the background and returns; Alchemy must not advance).
                f.State = 0;
            }
            else if (n < 120)
            {
                // Low band only: a slow full-depth pulse in the first few bins.
                f.State = 2;
                var level = (byte)Math.Clamp(128 + 127 * Math.Sin(2 * Math.PI * (n - 30) / 60.0), 0, 255);
                for (var b = 0; b <= 8; b++) { f.Frequency0[b] = level; f.Frequency1[b] = level; }
            }
            else if (n < 210)
            {
                // Top band only: verifies the 22050 Hz edge and that high bins are not silently dropped.
                f.State = 2;
                var level = (byte)Math.Clamp(128 + 127 * Math.Sin(2 * Math.PI * (n - 120) / 45.0), 0, 255);
                for (var b = 900; b < AudioFrame.Bins; b++) { f.Frequency0[b] = level; f.Frequency1[b] = level; }
            }
            else if (n < 300)
            {
                // Flat across every bin, ramping 0 -> 255. Every band gets energy, so every bar column
                // and every nBars edge case is touched.
                f.State = 2;
                var level = (byte)Math.Clamp((n - 210) * 255 / 89, 0, 255);
                f.Frequency0.AsSpan().Fill(level);
                f.Frequency1.AsSpan().Fill(level);
            }
            else if (n < 390)
            {
                // Impulses every 15 frames with a fast decay: drives peak-hold past its hold count and
                // makes the trail ring's per-frame shift visible.
                f.State = 2;
                var since = (n - 300) % 15;
                var amp = 255.0 * Math.Pow(0.78, since);
                var level = (byte)Math.Clamp(amp, 0, 255);
                for (var b = 0; b <= 40; b++) { f.Frequency0[b] = level; f.Frequency1[b] = level; }
            }
            else if (n < 420)
            {
                // Paused: state 1 means "reuse the previous data", so the buffers repeat frame 389.
                f.CopyFrom(frames[389]);
                f.State = 1;
                f.TimeStamp = n * TicksPerFrame;
            }
            else if (n < 450)
            {
                // Back to silence, exercising the 2 -> 1 -> 0 transition.
                f.State = 0;
            }
            else
            {
                // Waveform segment for Scope. Frequency stays low so the bar renderers are quiet and any
                // difference is attributable to the waveform path.
                f.State = 2;
                for (var b = 0; b <= 4; b++) { f.Frequency0[b] = 40; f.Frequency1[b] = 40; }
                FillWaveform(f, n - 450, ref lcg);
            }

            frames.Add(f);
        }

        return frames;
    }

    private static void FillWaveform(AudioFrame f, int k, ref Lcg lcg)
    {
        if (k < 60)
        {
            // Sine sweeping 1 -> 8 cycles across the 1024 samples.
            var cycles = 1.0 + 7.0 * k / 59.0;
            for (var i = 0; i < AudioFrame.Bins; i++)
            {
                var v = Math.Sin(2 * Math.PI * cycles * i / AudioFrame.Bins);
                var b = (byte)Math.Clamp(Math.Round(128 + v * 127), 0, 255);
                f.Waveform0[i] = b;
                f.Waveform1[i] = b;
            }
        }
        else if (k < 105)
        {
            // Square wave: hard vertical edges, which is where a polyline rasteriser differs from a
            // per-sample plot.
            var period = AudioFrame.Bins / 4;
            for (var i = 0; i < AudioFrame.Bins; i++)
            {
                var b = (byte)(i % period < period / 2 ? 250 : 5);
                f.Waveform0[i] = b;
                f.Waveform1[i] = b;
            }
        }
        else
        {
            // Deterministic noise burst.
            for (var i = 0; i < AudioFrame.Bins; i++)
            {
                var b = (byte)(lcg.Next() >> 24);
                f.Waveform0[i] = b;
                f.Waveform1[i] = (byte)(255 - b);
            }
        }
    }

    /// <summary>Fixed 64-bit LCG. Not for quality — for repeatability.</summary>
    private struct Lcg(ulong seed)
    {
        private ulong _s = seed;

        public uint Next()
        {
            _s = _s * 6364136223846793005UL + 1442695040888963407UL;
            return (uint)(_s >> 32);
        }
    }

    /// <summary>Human-readable segment boundaries, printed by the synth verb and used to group diffs.</summary>
    public static readonly (int Start, int End, string Name)[] Segments =
    [
        (0, 29, "silence (state 0)"),
        (30, 119, "low band pulse"),
        (120, 209, "top band pulse"),
        (210, 299, "flat ramp 0->255"),
        (300, 389, "impulses (peak hold / trails)"),
        (390, 419, "paused (state 1, reuse)"),
        (420, 449, "silence again (2->1->0)"),
        (450, 599, "waveform: sweep / square / noise"),
    ];
}
