using PlattaPlayer.Visualizations.Wmp.Audio;
using PlattaPlayer.Wmp.Harness.Interop;
using PlattaPlayer.Wmp.Harness.Synth;

namespace PlattaPlayer.Wmp.Harness.Verbs;

/// <summary>
/// Diffs our <see cref="AudioSnapshot"/> against the real analysis (<c>FUN_18000ac48</c>) over the whole
/// synthetic sequence, frame by frame.
///
/// The audio front end is the one layer where an error is invisible AND total: its bass level scales
/// every radius the renderers draw, and its beat flag decides when every effect re-randomizes. A beat
/// that fires one frame early makes the visualization subtly out of time with the music forever, and no
/// amount of staring at frames will show you why.
///
/// Both sides see identical bytes — our engines take raw TimedLevel blocks rather than floats precisely
/// so this is possible — so any difference is ours. Only fresh frames (state 2) are compared, because
/// that is the only state the real host advances the analysis on.
///
/// Usage: verify-audio
/// </summary>
internal static unsafe class VerifyAudio
{
    public static int Run(string[] args)
    {
        if (!File.Exists(HarnessPaths.SynthFile))
        {
            Console.Error.WriteLine($"verify-audio: {HarnessPaths.SynthFile} not found — run 'synth' first.");
            return 2;
        }
        var audio = AudioFrameSet.Read(HarnessPaths.SynthFile);

        Console.WriteLine(MpvisModule.Describe());
        Console.WriteLine($"comparing the audio analysis over {audio.Count} frames");
        Console.WriteLine();

        using var oracle = new AudioOracle();
        using var buffer = new TimedLevelBuffer();
        var ours = new AudioSnapshot();
        var levels = new TimedLevels();
        var random = new Random(1);

        int compared = 0, bassBad = 0, deltaBad = 0, beatBad = 0, hitBad = 0;
        double worstBass = 0, worstDelta = 0;
        var firstBeatMismatch = -1;
        var firstBassMismatch = -1;

        for (var i = 0; i < audio.Count; i++)
        {
            var a = audio[i];
            // The real host gates the whole analysis on state == 2 (FUN_180008170), so anything else is
            // simply not fed to it; feed neither side and skip.
            if (a.State != 2) continue;

            a.Frequency0.CopyTo(buffer.Frequency0);
            a.Frequency1.CopyTo(buffer.Frequency1);
            a.Waveform0.CopyTo(buffer.Waveform0);
            a.Waveform1.CopyTo(buffer.Waveform1);
            buffer.State = a.State;
            buffer.TimeStamp = a.TimeStamp;
            oracle.Analyze(buffer.Pointer);

            a.Frequency0.CopyTo(levels.Frequency[0], 0);
            a.Frequency1.CopyTo(levels.Frequency[1], 0);
            a.Waveform0.CopyTo(levels.Waveform[0], 0);
            a.Waveform1.CopyTo(levels.Waveform[1], 0);
            levels.State = a.State;
            ours.Update(levels, random);

            compared++;

            var dBass = Math.Abs(oracle.Bass - ours.BassNow);
            var dDelta = Math.Abs(oracle.BassDelta - ours.BassDelta);
            if (dBass > worstBass) worstBass = dBass;
            if (dDelta > worstDelta) worstDelta = dDelta;
            if (dBass != 0) { bassBad++; if (firstBassMismatch < 0) firstBassMismatch = i; }
            if (dDelta != 0) deltaBad++;
            if (oracle.Beat != ours.Beat) { beatBad++; if (firstBeatMismatch < 0) firstBeatMismatch = i; }
            if (oracle.BassHit != ours.BassHit) hitBad++;
        }

        Console.WriteLine($"  frames compared (state 2 only): {compared}");
        Console.WriteLine($"  bass      exact on {compared - bassBad}/{compared}   worst |delta| {worstBass:E3}");
        Console.WriteLine($"  bassDelta exact on {compared - deltaBad}/{compared}   worst |delta| {worstDelta:E3}");
        Console.WriteLine($"  beat      agrees on {compared - beatBad}/{compared}");
        Console.WriteLine($"  bassHit   agrees on {compared - hitBad}/{compared}");
        if (firstBassMismatch >= 0) Console.WriteLine($"  first bass mismatch at frame {firstBassMismatch}");
        if (firstBeatMismatch >= 0) Console.WriteLine($"  first beat mismatch at frame {firstBeatMismatch}");

        var ok = bassBad == 0 && deltaBad == 0 && beatBad == 0 && hitBad == 0;
        Console.WriteLine();
        Console.WriteLine(ok
            ? "verify-audio: our analysis is identical to the real one."
            : "verify-audio: DIVERGES from the real analysis.");
        return ok ? 0 : 1;
    }
}
