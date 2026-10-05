using PlattaPlayer.Codecs.Abstractions;
using PlattaPlayer.Codecs.Gbs.Emulation;

namespace PlattaPlayer.Codecs.Gbs;

/// <summary>
/// Plays one song of a GBS: emulates the Game Boy Color from the song's start for the play time, then fades
/// out linearly over the fade length and ends. The output is SameBoy's audio path at <see cref="SampleRate"/>
/// with its default ("accurate") high-pass filter; the host resamples it to the output device.
/// <para>
/// The emulation only runs forwards, so seeking means emulating up to the target. To keep that short, a copy
/// of the machine is kept every <see cref="CheckpointSeconds"/> of song time as it is reached; a seek resumes
/// from the nearest one at or before the target (or the current state, if that is nearer).
/// </para>
/// </summary>
internal sealed class GbsDecoder : ICodecDecoder
{
    /// <summary>The rate the APU output is rendered at (SameBoy's GB_set_sample_rate).</summary>
    public const int SampleRate = 44100;

    private const int CheckpointSeconds = 5;
    private const long CheckpointFrames = CheckpointSeconds * SampleRate;

    private readonly long _playFrames;
    private readonly long _fadeFrames;

    // Machine state at frame k * CheckpointFrames (index k); index 0 is the song just started.
    private readonly List<GameBoy> _checkpoints = new();

    private GameBoy _gb;
    private long _frame;
    private short[] _pcm = new short[4096];

    public GbsDecoder(GbsImage image, int song, double playSeconds, double fadeSeconds)
    {
        _gb = new GameBoy(image, SampleRate);
        _gb.StartSong(song);
        _checkpoints.Add(_gb.Clone());
        _playFrames = (long)Math.Round(playSeconds * SampleRate);
        _fadeFrames = (long)Math.Round(fadeSeconds * SampleRate);
    }

    int ICodecDecoder.SampleRate => SampleRate;
    public int Channels => 2;
    public TimeSpan Duration => FramesToTime(TotalFrames);
    public TimeSpan Position => FramesToTime(_frame);
    public string Description => "SM83 + APU (Game Boy Color, emulated)";

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
            _gb.Render(pcm);

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
        var target = Math.Clamp((long)Math.Round(position.TotalSeconds * SampleRate), 0, TotalFrames);

        // Resume from the latest checkpoint at or before the target, unless the current state is nearer.
        var index = (int)Math.Min(target / CheckpointFrames, _checkpoints.Count - 1);
        var checkpointFrame = index * CheckpointFrames;
        if (target < _frame || checkpointFrame > _frame)
        {
            _gb = _checkpoints[index].Clone();
            _frame = checkpointFrame;
        }

        while (_frame < target)
        {
            var frames = Math.Min(target - _frame, NextCheckpoint() - _frame);
            _gb.Skip(frames);
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
            _checkpoints.Add(_gb.Clone());
    }

    private static TimeSpan FramesToTime(long frames) => TimeSpan.FromSeconds(frames / (double)SampleRate);
}
