using PlattaPlayer.Codecs.Abstractions;
using PlattaPlayer.Codecs.Spc.Emulation;

namespace PlattaPlayer.Codecs.Spc;

/// <summary>
/// Plays an SPC file: emulates the audio unit from the snapshot for the tagged play time, then fades out
/// linearly over the tagged fade length and ends. The output is the DSP's own 32 kHz samples, unfiltered;
/// the host resamples them to the output device.
/// <para>
/// The emulation only runs forwards, so seeking means emulating up to the target. To keep that short, a copy
/// of the emulator state is kept every <see cref="CheckpointSeconds"/> of song time as it is reached; a seek
/// resumes from the nearest one at or before the target (or the current state, if that is nearer).
/// </para>
/// </summary>
internal sealed class SpcDecoder : ICodecDecoder
{
    private const int CheckpointSeconds = 5;
    private const long CheckpointFrames = CheckpointSeconds * Apu.SampleRate;

    private readonly long _playFrames;
    private readonly long _fadeFrames;

    // Emulator state at frame k * CheckpointFrames (index k); index 0 is the snapshot as loaded.
    private readonly List<Apu> _checkpoints = new();

    private Apu _apu;
    private long _frame;
    private short[] _pcm = new short[4096];

    public SpcDecoder(SpcFile file, double playSeconds, double fadeSeconds)
    {
        _apu = new Apu(file);
        _checkpoints.Add(_apu.Clone());
        _playFrames = (long)Math.Round(playSeconds * Apu.SampleRate);
        _fadeFrames = (long)Math.Round(fadeSeconds * Apu.SampleRate);
    }

    public int SampleRate => Apu.SampleRate;
    public int Channels => 2;
    public TimeSpan Duration => FramesToTime(TotalFrames);
    public TimeSpan Position => FramesToTime(_frame);
    public string Description => "SPC700 + S-DSP (emulated)";

    private long TotalFrames => _playFrames + _fadeFrames;

    public int Read(Span<float> buffer)
    {
        var wanted = (int)Math.Min(buffer.Length / 2, Math.Max(0, TotalFrames - _frame));
        var done = 0;
        while (done < wanted)
        {
            // Render up to the next checkpoint at most, so it can be taken on the exact frame.
            var frames = (int)Math.Min(wanted - done, NextCheckpoint() - _frame);
            if (_pcm.Length < frames * 2) _pcm = new short[frames * 2];
            var pcm = _pcm.AsSpan(0, frames * 2);
            _apu.Render(pcm);

            var output = buffer.Slice(done * 2, frames * 2);
            for (var i = 0; i < frames; i++)
            {
                var frame = _frame + i;
                var gain = frame < _playFrames ? 1f : 1f - (float)(frame - _playFrames) / _fadeFrames;
                output[i * 2] = pcm[i * 2] / 32768f * gain;
                output[i * 2 + 1] = pcm[i * 2 + 1] / 32768f * gain;
            }

            Advance(frames);
            done += frames;
        }
        return done;
    }

    public void Seek(TimeSpan position)
    {
        var target = Math.Clamp((long)Math.Round(position.TotalSeconds * Apu.SampleRate), 0, TotalFrames);

        // Resume from the latest checkpoint at or before the target, unless the current state is nearer.
        var index = (int)Math.Min(target / CheckpointFrames, _checkpoints.Count - 1);
        var checkpointFrame = index * CheckpointFrames;
        if (target < _frame || checkpointFrame > _frame)
        {
            _apu = _checkpoints[index].Clone();
            _frame = checkpointFrame;
        }

        while (_frame < target)
        {
            var frames = Math.Min(target - _frame, NextCheckpoint() - _frame);
            _apu.Skip(frames);
            Advance(frames);
        }
    }

    public void Dispose() => _checkpoints.Clear();

    private long NextCheckpoint() => (_frame / CheckpointFrames + 1) * CheckpointFrames;

    // Moves the song position on, taking a checkpoint when it lands on a new one.
    private void Advance(long frames)
    {
        _frame += frames;
        if (_frame % CheckpointFrames == 0 && _frame / CheckpointFrames == _checkpoints.Count)
            _checkpoints.Add(_apu.Clone());
    }

    private static TimeSpan FramesToTime(long frames) => TimeSpan.FromSeconds(frames / (double)Apu.SampleRate);
}
