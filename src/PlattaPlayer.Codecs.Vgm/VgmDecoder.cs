using PlattaPlayer.Codecs.Abstractions;
using PlattaPlayer.Codecs.Vgm.Emulation;

namespace PlattaPlayer.Codecs.Vgm;

/// <summary>
/// Plays a VGM on libvgm: the log drives the emulated sound chips at VGM's own 44100 Hz. A song without a
/// loop plays once and ends where its log does. A looping song plays through, repeats its looped section
/// <c>loops</c> times in all, and then fades out over the next pass, the way VGMPlay and libvgm's own player
/// do (the same squared fade curve, too); the host resamples to the output device.
/// <para>
/// Seeking uses libvgm's own: from freshly created chips it replays the song's commands up to the target
/// without rendering, so it is near-instant at any distance, and every chip register is as it would be. What
/// it skips is time inside the chips: an envelope or a sample that started before the target begins afresh at
/// it. That is how every VGM player seeks. A seek lands on the same samples however it was reached, and a seek
/// to the start on exactly those of playing from the start.
/// </para>
/// </summary>
internal sealed unsafe class VgmDecoder : ICodecDecoder
{
    private const int ChunkFrames = 4096;

    private readonly long _playFrames;
    private readonly long _fadeFrames;
    private readonly float _gain;
    private readonly int[] _pcm = new int[ChunkFrames * 2];

    private IntPtr _vgm;
    private long _frame;
    private bool _ended;

    public VgmDecoder(byte[] data, VgmFile file, int loops, double fadeSeconds, bool nukedFm, byte[]? rom, string chips)
    {
        fixed (byte* bytes = data)
        fixed (byte* romBytes = rom)
            _vgm = LibVgmNative.Create(bytes, (nuint)data.Length, VgmFile.SampleRate, nukedFm ? LibVgmNative.NukedFm : 0,
                romBytes, (nuint)(rom?.Length ?? 0));
        if (_vgm == IntPtr.Zero) throw new InvalidDataException("libvgm could not load the VGM data.");

        var (play, fade) = Length(file, loops, fadeSeconds);
        _playFrames = play;
        _fadeFrames = fade;
        _gain = LibVgmNative.VolumeGain(_vgm) / 65536f / LibVgmNative.FullScale;
        Description = $"{chips} (libvgm{(nukedFm ? ", Nuked FM" : "")}, emulated)";
        Variant = nukedFm ? "nuked-fm" : null;
    }

    public int SampleRate => VgmFile.SampleRate;
    public int Channels => 2;
    public TimeSpan Duration => FramesToTime(TotalFrames);
    public TimeSpan Position => FramesToTime(_frame);
    public string Description { get; }
    public string? Variant { get; }

    private long TotalFrames => _playFrames + _fadeFrames;

    /// <summary>The play length before the fade, and the fade, in frames (see the class summary).</summary>
    public static (long Play, long Fade) Length(VgmFile file, int loops, double fadeSeconds) =>
        file.Loops
            ? (file.TotalSamples + file.LoopSamples * (file.LoopCount(loops) - 1), (long)Math.Round(fadeSeconds * VgmFile.SampleRate))
            : (file.TotalSamples, 0);

    public int Read(Span<float> buffer)
    {
        ObjectDisposedException.ThrowIf(_vgm == IntPtr.Zero, this);

        var wanted = (int)Math.Min(buffer.Length / 2, Math.Max(0, TotalFrames - _frame));
        var done = 0;
        while (done < wanted && !_ended)
        {
            var frames = Math.Min(wanted - done, ChunkFrames);
            int rendered;
            fixed (int* pcm = _pcm)
                rendered = (int)LibVgmNative.Render(_vgm, pcm, (uint)frames);

            var output = buffer.Slice(done * 2, rendered * 2);
            for (var i = 0; i < rendered; i++)
            {
                var gain = _gain * FadeAt(_frame + i);
                output[i * 2] = _pcm[i * 2] * gain;
                output[i * 2 + 1] = _pcm[i * 2 + 1] * gain;
            }

            _frame += rendered;
            done += rendered;
            if (rendered < frames) _ended = true;
        }
        return done;
    }

    public void Seek(TimeSpan position)
    {
        ObjectDisposedException.ThrowIf(_vgm == IntPtr.Zero, this);
        var target = Math.Clamp((long)Math.Round(position.TotalSeconds * SampleRate), 0, TotalFrames);
        LibVgmNative.Seek(_vgm, (uint)Math.Min(target, uint.MaxValue));
        _frame = target;
        _ended = false;
    }

    public void Dispose()
    {
        if (_vgm == IntPtr.Zero) return;
        LibVgmNative.Destroy(_vgm);
        _vgm = IntPtr.Zero;
    }

    // Full volume, then (1 - t)² over the fade, as libvgm's PlayerA.
    private float FadeAt(long frame)
    {
        if (frame < _playFrames) return 1f;
        var remaining = 1f - (float)(frame - _playFrames) / _fadeFrames;
        return remaining > 0 ? remaining * remaining : 0f;
    }

    private static TimeSpan FramesToTime(long frames) => TimeSpan.FromSeconds(frames / (double)VgmFile.SampleRate);
}
