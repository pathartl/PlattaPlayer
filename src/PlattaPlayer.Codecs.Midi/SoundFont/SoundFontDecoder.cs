using MeltySynth;
using PlattaPlayer.Codecs.Abstractions;

namespace PlattaPlayer.Codecs.Midi.SoundFont;

/// <summary>
/// Plays a MIDI file rendered by MeltySynth with a SoundFont. The whole song is rendered when the decoder is
/// opened (MeltySynth runs far faster than real time), which makes seeking instant. The song ends where the
/// other devices end it (at its last event, see <see cref="SmfParser"/>), not at a trailing end-of-track
/// marker, which in some files lies minutes past the music.
/// <para>
/// The last rendering is remembered (weakly) so the host's second decoder for the seek-bar waveform shares it
/// instead of rendering the song again.
/// </para>
/// </summary>
internal sealed class SoundFontDecoder : ICodecDecoder
{
    private const int Rate = 44100;

    // A small tail lets reverb/release ring out instead of being clipped at the final note.
    private const double TailSeconds = 1.0;

    private static readonly Lock CacheGate = new();
    private static (string Key, WeakReference<short[]> Pcm)? _cache;

    private readonly short[] _pcm; // interleaved stereo
    private long _frame;

    /// <param name="songSeconds">The song's length by <see cref="SmfParser"/>, or null to use MeltySynth's.</param>
    public SoundFontDecoder(string midiPath, byte[] midiData, string soundFontPath, double? songSeconds)
    {
        Description = $"MeltySynth · {Path.GetFileNameWithoutExtension(soundFontPath)}";
        Variant = "sf:" + FileIdentity(soundFontPath);

        var smf = MidiMetadataReader.LocateSmf(midiData);
        if (smf.Offset < 0) throw new InvalidDataException($"{Path.GetFileName(midiPath)} is not a MIDI file.");
        var midiFile = new MidiFile(new MemoryStream(midiData, smf.Offset, smf.Length, writable: false));
        Duration = songSeconds is { } seconds && seconds < midiFile.Length.TotalSeconds
            ? TimeSpan.FromSeconds(seconds)
            : midiFile.Length;

        var key = FileIdentity(midiPath) + "|" + Variant;
        _pcm = Cached(key) ?? Render(midiFile, Duration, soundFontPath, key);
    }

    public int SampleRate => Rate;
    public int Channels => 2;
    public TimeSpan Duration { get; }
    public string Description { get; }
    public string? Variant { get; }

    public TimeSpan Position => TimeSpan.FromSeconds(Math.Min((double)_frame / Rate, Duration.TotalSeconds));

    public int Read(Span<float> buffer)
    {
        var total = _pcm.Length / 2;
        var frames = (int)Math.Min(buffer.Length / 2, Math.Max(0, total - _frame));
        var start = (int)(_frame * 2);
        for (var i = 0; i < frames * 2; i++)
            buffer[i] = _pcm[start + i] / 32768f;
        _frame += frames;
        return frames;
    }

    public void Seek(TimeSpan position) =>
        _frame = (long)(Math.Clamp(position.TotalSeconds, 0, Duration.TotalSeconds) * Rate);

    public void Dispose() { }

    private static short[] Render(MidiFile midiFile, TimeSpan length, string soundFontPath, string key)
    {
        var synthesizer = new Synthesizer(soundFontPath, Rate);
        var sequencer = new MidiFileSequencer(synthesizer);
        sequencer.Play(midiFile, loop: false);

        var frames = (int)(Rate * (length.TotalSeconds + TailSeconds));
        var left = new float[frames];
        var right = new float[frames];
        sequencer.Render(left, right);

        // Kept as 16-bit, half the memory of float, as the WAV the player used to build was.
        var pcm = new short[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            pcm[2 * i] = (short)(Math.Clamp(left[i], -1f, 1f) * short.MaxValue);
            pcm[2 * i + 1] = (short)(Math.Clamp(right[i], -1f, 1f) * short.MaxValue);
        }

        lock (CacheGate) _cache = (key, new WeakReference<short[]>(pcm));
        return pcm;
    }

    private static short[]? Cached(string key)
    {
        lock (CacheGate)
            return _cache is { } c && c.Key == key && c.Pcm.TryGetTarget(out var pcm) ? pcm : null;
    }

    /// <summary>Path, size and write time: changes whenever the file does.</summary>
    private static string FileIdentity(string path)
    {
        var info = new FileInfo(path);
        return $"{Path.GetFullPath(path)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
    }
}
