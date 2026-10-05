using PlattaPlayer.Codecs.Abstractions;
using PlattaPlayer.Codecs.Gsf.Emulation;

namespace PlattaPlayer.Codecs.Gsf;

/// <summary>
/// Plays a GSF on mGBA: boots the emulated GBA from the rip's image, runs it for the tagged play time, then
/// fades out linearly over the tagged fade length and ends. The output is the GBA's own audio stream at its
/// 32768 Hz; the host resamples it to the output device.
/// <para>
/// The emulation only runs forwards, so seeking means emulating up to the target. To keep that short, the
/// machine's state is saved every <see cref="CheckpointSeconds"/> of song time as it is reached; a seek
/// resumes from the nearest one at or before the target (or the current state, if that is nearer). A state
/// is about 400 KiB, so a long song's checkpoints run to a few tens of megabytes at most.
/// </para>
/// </summary>
internal sealed unsafe class GsfDecoder : ICodecDecoder
{
    private const int CheckpointSeconds = 10;
    private const int ChunkFrames = 4096;

    private readonly long _playFrames;
    private readonly long _fadeFrames;
    private readonly float _volume;
    private readonly long _checkpointFrames;
    private readonly int _stateSize;
    private readonly short[] _pcm = new short[ChunkFrames * 2];

    // Machine state at frame k * _checkpointFrames (index k); index 0 is the machine as booted.
    private readonly List<byte[]> _checkpoints = [];

    private IntPtr _gsf;
    private long _frame;

    public GsfDecoder(GsfImage image, double playSeconds, double fadeSeconds, double volume)
    {
        fixed (byte* data = image.Data)
            _gsf = MgbaGsfNative.Create(data, (nuint)image.Data.Length, image.EntryPoint);
        if (_gsf == IntPtr.Zero) throw new InvalidDataException("The GSF data is invalid.");

        _volume = (float)volume;
        SampleRate = MgbaGsfNative.SampleRate();
        _stateSize = (int)MgbaGsfNative.StateSize(_gsf);
        _checkpointFrames = (long)CheckpointSeconds * SampleRate;
        _playFrames = (long)Math.Round(playSeconds * SampleRate);
        _fadeFrames = (long)Math.Round(fadeSeconds * SampleRate);
        try
        {
            _checkpoints.Add(SaveState());
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public int SampleRate { get; }
    public int Channels => 2;
    public TimeSpan Duration => FramesToTime(TotalFrames);
    public TimeSpan Position => FramesToTime(_frame);
    public string Description => "GBA ARM7TDMI + sound (mGBA, emulated)";

    private long TotalFrames => _playFrames + _fadeFrames;

    public int Read(Span<float> buffer)
    {
        ObjectDisposedException.ThrowIf(_gsf == IntPtr.Zero, this);

        var wanted = (int)Math.Min(buffer.Length / 2, Math.Max(0, TotalFrames - _frame));
        var done = 0;
        while (done < wanted)
        {
            // Up to the next checkpoint at most, so it can be taken on the exact frame.
            var frames = (int)Math.Min(Math.Min(wanted - done, ChunkFrames), NextCheckpoint() - _frame);
            fixed (short* pcm = _pcm)
                MgbaGsfNative.Render(_gsf, pcm, (nuint)frames);

            var output = buffer.Slice(done * 2, frames * 2);
            for (var i = 0; i < frames; i++)
            {
                var frame = _frame + i;
                var gain = frame < _playFrames ? _volume : _volume * (1f - (float)(frame - _playFrames) / _fadeFrames);
                output[i * 2] = _pcm[i * 2] / 32768f * gain;
                output[i * 2 + 1] = _pcm[i * 2 + 1] / 32768f * gain;
            }

            Advance(frames);
            done += frames;
        }
        return done;
    }

    public void Seek(TimeSpan position)
    {
        ObjectDisposedException.ThrowIf(_gsf == IntPtr.Zero, this);
        var target = Math.Clamp((long)Math.Round(position.TotalSeconds * SampleRate), 0, TotalFrames);

        // Resume from the latest checkpoint at or before the target, unless the current state is nearer.
        var index = (int)Math.Min(target / _checkpointFrames, _checkpoints.Count - 1);
        var checkpointFrame = index * _checkpointFrames;
        if (target < _frame || checkpointFrame > _frame)
        {
            fixed (byte* state = _checkpoints[index])
                if (MgbaGsfNative.LoadState(_gsf, state) != 0)
                    throw new InvalidOperationException("mGBA refused its own saved state.");
            _frame = checkpointFrame;
        }

        while (_frame < target)
        {
            var frames = Math.Min(target - _frame, NextCheckpoint() - _frame);
            MgbaGsfNative.Render(_gsf, null, (nuint)frames);
            Advance(frames);
        }
    }

    public void Dispose()
    {
        if (_gsf == IntPtr.Zero) return;
        MgbaGsfNative.Destroy(_gsf);
        _gsf = IntPtr.Zero;
        _checkpoints.Clear();
    }

    private long NextCheckpoint() => (_frame / _checkpointFrames + 1) * _checkpointFrames;

    // Moves the song position on, taking a checkpoint when it lands on a new one.
    private void Advance(long frames)
    {
        _frame += frames;
        if (_frame % _checkpointFrames == 0 && _frame / _checkpointFrames == _checkpoints.Count)
            _checkpoints.Add(SaveState());
    }

    private byte[] SaveState()
    {
        var state = GC.AllocateUninitializedArray<byte>(_stateSize);
        fixed (byte* data = state)
            if (MgbaGsfNative.SaveState(_gsf, data) != 0)
                throw new InvalidOperationException("mGBA could not save its state.");
        return state;
    }

    private TimeSpan FramesToTime(long frames) => TimeSpan.FromSeconds(frames / (double)SampleRate);
}
