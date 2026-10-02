using System.Diagnostics;
using System.Runtime.InteropServices;
using PlattaPlayer.Codecs.Abstractions;
using PlattaPlayer.Codecs.Usf.Emulation;
using PlattaPlayer.Codecs.Usf.Psf;

namespace PlattaPlayer.Codecs.Usf;

/// <summary>
/// Plays a USF on lazyusf2: boots the emulated N64 from the rip's save state, runs it for the tagged play
/// time, then fades out linearly over the tagged fade length and ends. The output is the audio interface's
/// own stream at the rate the game programmed when the song started; the host resamples it to the output
/// device. Should the game reprogram the rate mid-song, the rest is resampled to the first rate, since a
/// decoder's rate is fixed.
/// <para>
/// lazyusf2 can't snapshot its state (it is full of pointers into itself and into blocks the interpreter
/// compiles), so a seek forwards emulates up to the target with the audio discarded, and a seek backwards
/// restarts the song first. The emulator runs far faster than real time, so this takes a moment rather than
/// the target's length.
/// </para>
/// </summary>
internal sealed unsafe class UsfDecoder : ICodecDecoder
{
    private const int ChunkFrames = 4096;

    private readonly long _playFrames;
    private readonly long _fadeFrames;
    private readonly float _volume;
    private readonly bool _hle;
    private readonly short[] _pcm = new short[ChunkFrames * 2];

    private IntPtr _state;
    private long _frame;
    // Set once the game changed the audio rate: from then on lazyusf2 resamples to SampleRate.
    private bool _resampling;
    private bool _failed;

    public UsfDecoder(UsfSet set, bool hle, double playSeconds, double fadeSeconds, double volume)
    {
        _hle = hle;
        _volume = (float)volume;
        _state = (IntPtr)NativeMemory.Alloc(LazyUsf2Native.GetStateSize());
        try
        {
            LazyUsf2Native.Clear(_state);
            foreach (var section in set.Sections)
                fixed (byte* data = section)
                    if (LazyUsf2Native.UploadSection(_state, data, (nuint)section.Length) < 0)
                        throw new InvalidDataException("The USF data is invalid.");

            LazyUsf2Native.SetCompare(_state, set.EnableCompare ? 1 : 0);
            LazyUsf2Native.SetFifoFull(_state, set.EnableFifoFull ? 1 : 0);
            LazyUsf2Native.SetHleAudio(_state, hle ? 1 : 0);

            // Boots the machine and runs it until the first audio block, which tells the rate. The block stays
            // buffered in the emulator for the first Read.
            int rate;
            var error = LazyUsf2Native.Render(_state, null, 0, &rate);
            if (error != IntPtr.Zero) throw new InvalidDataException(Marshal.PtrToStringUTF8(error)?.Trim() ?? "The USF failed to start.");
            if (rate <= 0) throw new InvalidDataException("The USF produced no audio.");
            SampleRate = rate;
        }
        catch
        {
            Free();
            throw;
        }

        _playFrames = (long)Math.Round(playSeconds * SampleRate);
        _fadeFrames = (long)Math.Round(fadeSeconds * SampleRate);
    }

    public int SampleRate { get; }
    public int Channels => 2;
    public TimeSpan Duration => FramesToTime(TotalFrames);
    public TimeSpan Position => FramesToTime(_frame);
    public string Description => _hle ? "N64 R4300 + RSP, HLE audio (lazyusf2)" : "N64 R4300 + RSP (lazyusf2, emulated)";
    public string? Variant => _hle ? "hle" : "lle";

    private long TotalFrames => _playFrames + _fadeFrames;

    public int Read(Span<float> buffer)
    {
        ObjectDisposedException.ThrowIf(_state == IntPtr.Zero, this);
        if (_failed) return 0;

        var wanted = (int)Math.Min(buffer.Length / 2, Math.Max(0, TotalFrames - _frame));
        var done = 0;
        while (done < wanted)
        {
            var frames = Math.Min(wanted - done, ChunkFrames);
            fixed (short* pcm = _pcm)
                if (!Render(pcm, frames)) break;

            var output = buffer.Slice(done * 2, frames * 2);
            for (var i = 0; i < frames; i++)
            {
                var frame = _frame + i;
                var gain = frame < _playFrames ? _volume : _volume * (1f - (float)(frame - _playFrames) / _fadeFrames);
                output[i * 2] = _pcm[i * 2] / 32768f * gain;
                output[i * 2 + 1] = _pcm[i * 2 + 1] / 32768f * gain;
            }

            _frame += frames;
            done += frames;
        }
        return done;
    }

    public void Seek(TimeSpan position)
    {
        ObjectDisposedException.ThrowIf(_state == IntPtr.Zero, this);
        var target = Math.Clamp((long)Math.Round(position.TotalSeconds * SampleRate), 0, TotalFrames);

        if (target < _frame || _failed)
        {
            LazyUsf2Native.Restart(_state);
            _frame = 0;
            _resampling = false;
            _failed = false;
        }

        // In steps of about a second, so a rate change part-way is noticed where it happens.
        while (_frame < target && !_failed)
        {
            var frames = (int)Math.Min(target - _frame, SampleRate);
            if (!Render(null, frames)) break;
            _frame += frames;
        }
    }

    public void Dispose() => Free();

    // Renders exactly `frames` frames (discarded when `pcm` is null). False when the emulator failed, which
    // ends the song.
    private bool Render(short* pcm, int frames)
    {
        IntPtr error;
        if (_resampling)
        {
            error = LazyUsf2Native.RenderResampled(_state, pcm, (nuint)frames, SampleRate);
        }
        else
        {
            int rate;
            error = LazyUsf2Native.Render(_state, pcm, (nuint)frames, &rate);
            // The rate reported is the one in effect after this block; from the next block on, resample.
            if (error == IntPtr.Zero && rate != SampleRate && rate > 0) _resampling = true;
        }

        if (error == IntPtr.Zero) return true;
        Debug.WriteLine($"USF: emulation stopped at {Position}: {Marshal.PtrToStringUTF8(error)?.Trim()}");
        _failed = true;
        return false;
    }

    private void Free()
    {
        if (_state == IntPtr.Zero) return;
        LazyUsf2Native.Shutdown(_state);
        NativeMemory.Free((void*)_state);
        _state = IntPtr.Zero;
    }

    private TimeSpan FramesToTime(long frames) => TimeSpan.FromSeconds(frames / (double)SampleRate);
}
