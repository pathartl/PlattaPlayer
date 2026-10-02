using ManagedBass;
using PlattaPlayer.Codecs.Abstractions;

namespace PlattaPlayer.Playback;

/// <summary>
/// Plays an <see cref="ICodecDecoder"/> as a BASS user stream: BASS asks for audio and the decoder renders it
/// on demand, so codec-decoded formats go through the same output path (and the audio tap that feeds the
/// visualizations) as everything else.
/// <para>
/// Seeking is deferred to the stream's next render, which runs on BASS's thread: a decoder may need to
/// emulate its way to the target, and that shouldn't block the caller.
/// </para>
/// </summary>
internal sealed class CodecStream : IDisposable
{
    private readonly ICodecDecoder _decoder;
    private readonly StreamProcedure _proc;
    private readonly int _bytesPerFrame;
    private readonly Lock _gate = new();
    private float[] _scratch = new float[4096];

    // A seek waiting for the next render, in ticks (-1 = none).
    private long _pendingSeek = -1;

    // Frames handed to BASS so far (song time of the render cursor).
    private long _renderedFrames;
    private bool _ended;
    private bool _disposed;

    /// <param name="decodeOnly">A decoding channel (for the seek-bar waveform) rather than a playing one.</param>
    public CodecStream(ICodecDecoder decoder, bool decodeOnly = false)
    {
        _decoder = decoder;
        _bytesPerFrame = decoder.Channels * sizeof(float);
        _proc = StreamProc;
        var flags = BassFlags.Float | (decodeOnly ? BassFlags.Decode : BassFlags.Default);
        Handle = Bass.CreateStream(decoder.SampleRate, decoder.Channels, flags, _proc, IntPtr.Zero);
    }

    /// <summary>The BASS channel (0 when it could not be created).</summary>
    public int Handle { get; }

    public TimeSpan Duration => _decoder.Duration;

    /// <summary>What produces the audio, for display.</summary>
    public string Description => _decoder.Description;

    /// <summary>What is being heard: the render cursor less what BASS still has buffered.</summary>
    public TimeSpan Position
    {
        get
        {
            var pending = Interlocked.Read(ref _pendingSeek);
            if (pending >= 0) return TimeSpan.FromTicks(pending);

            var buffered = Handle == 0 ? 0 : Math.Max(0, Bass.ChannelGetData(Handle, IntPtr.Zero, (int)DataFlags.Available));
            var frames = Math.Max(0, Interlocked.Read(ref _renderedFrames) - buffered / _bytesPerFrame);
            return TimeSpan.FromSeconds(Math.Min(frames / (double)_decoder.SampleRate, Duration.TotalSeconds));
        }
    }

    /// <summary>
    /// Moves to <paramref name="position"/>. The caller stops the channel first (stopping a user stream
    /// discards its buffer) and plays it again afterwards; the decoder seeks on the next render.
    /// </summary>
    public void Seek(TimeSpan position)
    {
        var ticks = Math.Clamp(position.Ticks, 0, Duration.Ticks);
        lock (_gate)
        {
            Interlocked.Exchange(ref _pendingSeek, ticks);
            Interlocked.Exchange(ref _renderedFrames, (long)(TimeSpan.FromTicks(ticks).TotalSeconds * _decoder.SampleRate));
            _ended = false;
        }
    }

    private int StreamProc(int handle, IntPtr buffer, int length, IntPtr user)
    {
        lock (_gate)
        {
            if (_disposed || _ended) return (int)StreamProcedureType.End;

            var pending = Interlocked.Exchange(ref _pendingSeek, -1);
            if (pending >= 0) _decoder.Seek(TimeSpan.FromTicks(pending));

            var samples = length / sizeof(float);
            if (_scratch.Length < samples) _scratch = new float[samples];
            var frames = _decoder.Read(_scratch.AsSpan(0, samples - samples % _decoder.Channels));
            var bytes = frames * _bytesPerFrame;
            System.Runtime.InteropServices.Marshal.Copy(_scratch, 0, buffer, frames * _decoder.Channels);
            Interlocked.Add(ref _renderedFrames, frames);

            // A short read is the end: flag it so BASS raises its end sync once this is played out.
            if (bytes < length)
            {
                _ended = true;
                return bytes | (int)StreamProcedureType.End;
            }
            return bytes;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        if (Handle != 0) Bass.StreamFree(Handle);
        _decoder.Dispose();
    }
}
