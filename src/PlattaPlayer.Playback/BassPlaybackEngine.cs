using System.Diagnostics;
using ManagedBass;
using PlattaPlayer.Codecs.Abstractions;
using PlattaPlayer.Core.Abstractions;
using PlattaPlayer.Core.Models;

// Both namespaces define a PlaybackState; the engine's public contract uses ours.
using PlaybackState = PlattaPlayer.Core.Models.PlaybackState;

namespace PlattaPlayer.Playback;

/// <summary>
/// <see cref="IPlaybackEngine"/> backed by BASS (via ManagedBass). Standard audio is opened straight
/// from the file path or remote URL. Formats BASS can't decode (MIDI, SNES SPC, …) are played by a codec
/// plugin (<see cref="ICodecPlugin"/>): its PCM goes through a BASS user stream (<see cref="CodecStream"/>),
/// so there is a single output path, unless the codec plays on an output of its own
/// (<see cref="ICodecDirectOutput"/>, e.g. a hardware MIDI port), which has no audio to tap. BASS exposes the
/// decoded signal through <c>ChannelGetData</c> (incl. FFT), which keeps visualisations cheap.
/// </summary>
public sealed class BassPlaybackEngine : IPlaybackEngine, IAudioTap, IWaveformSource
{
    private static readonly object InitLock = new();
    private static bool _initialized;

    // BASS DataFlags.FFT2048 performs a 2048-point FFT and returns 1024 magnitude bins.
    private const int FftBins = 1024;

    private readonly IAppSettings _settings;
    private readonly CodecRegistry _codecs;
    private readonly Lock _gate = new();

    // BASS sync/position notifications arrive on BASS threads; keep the delegate alive for the
    // channel's lifetime so it is not collected while BASS still holds the pointer.
    private readonly SyncProcedure _endSync;
    private readonly Timer _positionTimer;

    private int _stream;          // current BASS channel handle (0 = none)
    private int _volume = 100;
    private TimeSpan _duration;

    // Set when a codec plugin decodes the track; its channel is _stream.
    private CodecStream? _codec;

    // Set when a codec plays the track on an output of its own. While active the BASS stream is 0, so the
    // audio tap is automatically inactive (no visualizations).
    private ICodecDirectOutput? _direct;

    // Bumped by every LoadAsync. A load that finishes after a newer one started is stale and is dropped.
    private int _loadGeneration;

    // Cancels the background seek-bar waveform pass for the previous track.
    private CancellationTokenSource? _waveformCts;

    // Null when no cache directory was supplied: every track is then decoded for its waveform each time.
    private readonly WaveformCache? _waveformCache;

    public BassPlaybackEngine(
        IAppSettings settings,
        string? waveformCacheDirectory = null,
        CodecRegistry? codecs = null)
    {
        _settings = settings;
        _codecs = codecs ?? CodecRegistry.Empty;
        if (waveformCacheDirectory is not null) _waveformCache = new WaveformCache(waveformCacheDirectory);
        _endSync = OnChannelEnded;

        EnsureCoreInitialized();

        // Polls position while playing; BASS has no built-in periodic position notification.
        _positionTimer = new Timer(_ => ReportPosition(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public PlaybackState State { get; private set; } = PlaybackState.Stopped;

    public TimeSpan Position
    {
        get
        {
            if (_direct is { } direct) return direct.Position;
            if (_codec is { } codec) return codec.Position;
            var handle = _stream;
            if (handle == 0) return TimeSpan.Zero;
            var bytes = Bass.ChannelGetPosition(handle);
            if (bytes < 0) return TimeSpan.Zero;
            return TimeSpan.FromSeconds(Bass.ChannelBytes2Seconds(handle, bytes));
        }
    }

    public TimeSpan Duration => _duration;

    public string Renderer { get; private set; } = string.Empty;

    public int Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 100);
            ApplyVolume();
        }
    }

    public event EventHandler<PlaybackState>? StateChanged;
    public event EventHandler? PlaybackEnded;
    public event EventHandler<TimeSpan>? PositionChanged;

    public async Task LoadAsync(PlayableMedia media, CancellationToken ct = default)
    {
        var generation = Interlocked.Increment(ref _loadGeneration);

        // Opening a codec can take emulated seconds (booting a sound module, rendering a song), so it runs off
        // the caller's thread, and a load overtaken by a newer one in the meantime is dropped.
        var plugin = media.Kind == PlayableKind.LocalFile ? _codecs.ForPath(media.Location) : null;
        ICodecDecoder? decoder = null;
        if (plugin is not null)
        {
            decoder = await Task.Run(() => TryOpen(plugin, media), CancellationToken.None);
            if (ct.IsCancellationRequested || generation != Volatile.Read(ref _loadGeneration))
            {
                decoder?.Dispose();
                ct.ThrowIfCancellationRequested();
                return;
            }
        }

        lock (_gate)
        {
            FreeStream();
            _duration = TimeSpan.Zero;
            Renderer = string.Empty;
            CancelWaveform();

            if (plugin is not null)
            {
                if (decoder is null || !StartCodec(plugin, decoder, media))
                {
                    // Unplayable file: treat it as finished so the queue advances instead of stalling.
                    OnState(PlaybackState.Stopped);
                    ThreadPool.QueueUserWorkItem(_ => PlaybackEnded?.Invoke(this, EventArgs.Empty));
                }
                return;
            }

            _stream = CreateStandardStream(media);
            if (_stream == 0)
            {
                // Could not open the media (e.g. a decode error). Treat as a finished track so the
                // queue advances instead of stalling.
                OnState(PlaybackState.Stopped);
                ThreadPool.QueueUserWorkItem(_ => PlaybackEnded?.Invoke(this, EventArgs.Empty));
                return;
            }
            Renderer = "BASS";

            var lengthBytes = Bass.ChannelGetLength(_stream);
            if (lengthBytes > 0)
                _duration = TimeSpan.FromSeconds(Bass.ChannelBytes2Seconds(_stream, lengthBytes));

            Bass.ChannelSetSync(_stream, SyncFlags.End, 0, _endSync);
            ApplyVolume();

            // The seek-bar waveform decodes the whole track a second time; for remote streams that means
            // downloading it twice, so those honour the user's opt-out. A cached envelope costs nothing,
            // so it is shown either way.
            var cacheKey = _waveformCache is null ? null : WaveformCache.KeyFor(media, null, WaveformVersion);
            if (media.Kind != PlayableKind.RemoteUrl)
                StartWaveform(cacheKey, () => new DecodeChannel(Bass.CreateStream(media.Location, 0, 0, BassFlags.Decode | BassFlags.Float)));
            else
                StartWaveform(cacheKey, _settings.ShowRemoteWaveforms
                    ? () => new DecodeChannel(Bass.CreateStream(media.Location, 0, BassFlags.Decode | BassFlags.Float, null))
                    : null);
        }
    }

    private static ICodecDecoder? TryOpen(ICodecPlugin plugin, PlayableMedia media)
    {
        try
        {
            return Open(plugin, media);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"{plugin.DisplayName} could not open {media.Location}: {ex}");
            return null;
        }
    }

    // A song of a multi-song file opens through the plugin's subsong support; anything else as a whole file.
    private static ICodecDecoder Open(ICodecPlugin plugin, PlayableMedia media) =>
        media.Subsong is { } subsong && plugin is ICodecSubsongs subsongs
            ? subsongs.Open(media.Location, subsong)
            : plugin.Open(media.Location);

    /// <summary>
    /// Plays an opened codec decoder: its PCM through a BASS user stream (which ends by itself, raising the end
    /// sync, and gets a seek-bar waveform decoded on a second decoder in the background when the codec allows
    /// one), or on the codec's own output. Takes ownership of <paramref name="decoder"/>. Call under <see cref="_gate"/>.
    /// </summary>
    private bool StartCodec(ICodecPlugin plugin, ICodecDecoder decoder, PlayableMedia media)
    {
        if (decoder is ICodecDirectOutput direct)
        {
            direct.Ended += OnDirectEnded;
            _direct = direct;
            _duration = direct.Duration;
            Renderer = direct.Description;
            ApplyVolume();
            return true;
        }

        var stream = new CodecStream(decoder);
        if (stream.Handle == 0)
        {
            Debug.WriteLine($"{plugin.DisplayName}: BASS could not create the stream ({Bass.LastError})");
            stream.Dispose();
            return false;
        }

        _codec = stream;
        _stream = stream.Handle;
        _duration = stream.Duration;
        Renderer = $"{stream.Description} → PCM";
        // Resample the codec's native rate to the device with BASS's best filter (64-point sinc).
        Bass.ChannelSetAttribute(_stream, ChannelAttribute.SampleRateConversion, 4);
        Bass.ChannelSetSync(_stream, SyncFlags.End, 0, _endSync);
        ApplyVolume();

        var cacheKey = _waveformCache is null ? null : WaveformCache.KeyFor(media, decoder.Variant, WaveformVersion);
        StartWaveform(cacheKey, decoder.AllowsBackgroundDecode
            ? () =>
            {
                var waveformDecoder = new CodecStream(Open(plugin, media), decodeOnly: true);
                return new DecodeChannel(waveformDecoder.Handle, waveformDecoder);
            }
            : null);
        return true;
    }

    private int CreateStandardStream(PlayableMedia media)
    {
        return media.Kind == PlayableKind.RemoteUrl
            ? Bass.CreateStream(media.Location, 0, BassFlags.Default, null)
            : Bass.CreateStream(media.Location, 0, 0, BassFlags.Default);
    }

    private void OnDirectEnded(object? sender, EventArgs e)
        => ThreadPool.QueueUserWorkItem(_ =>
        {
            _positionTimer.Change(Timeout.Infinite, Timeout.Infinite);
            PlaybackEnded?.Invoke(this, EventArgs.Empty);
        });

    public void Play()
    {
        lock (_gate)
        {
            if (_direct is { } direct) direct.Play();
            else if (_stream != 0) Bass.ChannelPlay(_stream);
            else return;
        }

        OnState(PlaybackState.Playing);
        _positionTimer.Change(0, 250);
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_direct is { } direct) direct.Pause();
            else if (_stream != 0) Bass.ChannelPause(_stream);
            else return;
        }

        OnState(PlaybackState.Paused);
        _positionTimer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_direct is { } direct)
            {
                direct.Pause();
                direct.Seek(TimeSpan.Zero);
            }
            else if (_codec is { } codec)
            {
                Bass.ChannelStop(_stream);
                codec.Seek(TimeSpan.Zero);
            }
            else if (_stream != 0)
            {
                Bass.ChannelStop(_stream);
                Bass.ChannelSetPosition(_stream, 0);
            }
        }

        _positionTimer.Change(Timeout.Infinite, Timeout.Infinite);
        OnState(PlaybackState.Stopped);
    }

    public void Seek(TimeSpan position)
    {
        lock (_gate)
        {
            if (_direct is { } direct) direct.Seek(position);
            else if (_codec is { } codec)
            {
                // Stopping a user stream discards what it has buffered; the decoder seeks on the next render.
                Bass.ChannelStop(_stream);
                codec.Seek(position);
                if (State == PlaybackState.Playing) Bass.ChannelPlay(_stream);
            }
            else if (_stream != 0)
            {
                var bytes = Bass.ChannelSeconds2Bytes(_stream, position.TotalSeconds);
                Bass.ChannelSetPosition(_stream, bytes);
            }
        }
    }

    private void OnChannelEnded(int handle, int channel, int data, IntPtr user)
        // Raised on a BASS thread; hop to the pool before re-entering the engine to avoid
        // reentrancy on the callback thread.
        => ThreadPool.QueueUserWorkItem(_ =>
        {
            _positionTimer.Change(Timeout.Infinite, Timeout.Infinite);
            PlaybackEnded?.Invoke(this, EventArgs.Empty);
        });

    private void ReportPosition()
    {
        if (_direct is not null || _codec is not null)
        {
            PositionChanged?.Invoke(this, Position);
            return;
        }

        var handle = _stream;
        if (handle == 0) return;

        // Some network streams only learn their length once buffered — fill it in late.
        if (_duration == TimeSpan.Zero)
        {
            var lengthBytes = Bass.ChannelGetLength(handle);
            if (lengthBytes > 0)
                _duration = TimeSpan.FromSeconds(Bass.ChannelBytes2Seconds(handle, lengthBytes));
        }

        PositionChanged?.Invoke(this, Position);
    }

    // --- IAudioTap: passive FFT tap for visualizations -----------------------------------------

    public bool IsActive => State == PlaybackState.Playing && _stream != 0;

    public int SampleRate
    {
        get
        {
            var handle = _stream;
            return handle == 0 ? 0 : Bass.ChannelGetInfo(handle).Frequency;
        }
    }

    public int ReadSpectrum(float[] destination)
    {
        var handle = _stream;
        if (handle == 0 || destination.Length == 0) return 0;

        // FFT on a playback channel reads what is currently being heard; non-blocking and passive.
        var read = Bass.ChannelGetData(handle, destination, (int)DataFlags.FFT2048);
        return read < 0 ? 0 : Math.Min(destination.Length, FftBins);
    }

    // Scratch buffer for the interleaved float samples BASS hands back before we downmix to mono.
    private float[] _pcmScratch = new float[4096];

    public int ReadWaveform(float[] destination)
    {
        var handle = _stream;
        if (handle == 0 || destination.Length == 0) return 0;

        var channels = Math.Max(1, Bass.ChannelGetInfo(handle).Channels);
        var wantSamples = destination.Length * channels;
        if (_pcmScratch.Length < wantSamples) _pcmScratch = new float[wantSamples];

        // DataFlags.Float forces 32-bit float output regardless of the channel's native format;
        // the low bits of the length are the byte count to read. Passive, non-blocking.
        var byteCount = wantSamples * sizeof(float);
        var read = Bass.ChannelGetData(handle, _pcmScratch, byteCount | (int)DataFlags.Float);
        if (read <= 0) return 0;

        var framesRead = read / sizeof(float) / channels;
        var frames = Math.Min(framesRead, destination.Length);
        for (var i = 0; i < frames; i++)
        {
            var sum = 0f;
            for (var c = 0; c < channels; c++) sum += _pcmScratch[i * channels + c];
            destination[i] = sum / channels;
        }

        return frames;
    }

    public int ReadStereoWaveform(float[] left, float[] right)
    {
        var handle = _stream;
        var length = Math.Min(left.Length, right.Length);
        if (handle == 0 || length == 0) return 0;

        var channels = Math.Max(1, Bass.ChannelGetInfo(handle).Channels);
        var wantSamples = length * channels;
        if (_pcmScratch.Length < wantSamples) _pcmScratch = new float[wantSamples];

        var read = Bass.ChannelGetData(handle, _pcmScratch, wantSamples * sizeof(float) | (int)DataFlags.Float);
        if (read <= 0) return 0;

        // Channel 0 is left and channel 1 right; anything beyond is ignored, and mono feeds both sides.
        var frames = Math.Min(read / sizeof(float) / channels, length);
        var second = channels > 1 ? 1 : 0;
        for (var i = 0; i < frames; i++)
        {
            left[i] = _pcmScratch[i * channels];
            right[i] = _pcmScratch[i * channels + second];
        }

        return frames;
    }

    // --- IWaveformSource: whole-track envelope for the seek bar -----------------------------------

    // Envelope resolution; the seek bar resamples it to its width, so this only needs to exceed it loosely.
    private const int WaveformBins = 600;

    // Half-width of the triangular smoothing kernel, in bins.
    private const int WaveformSmoothing = 4;

    // Part of every cache key: bump it whenever the envelope computation changes, so stale entries are ignored.
    private const string WaveformVersion = "env1:bins600:smooth4";

    public float[]? Waveform { get; private set; }

    public event EventHandler? WaveformChanged;

    private void CancelWaveform()
    {
        _waveformCts?.Cancel();
        _waveformCts = null;
        if (Waveform is null) return;
        Waveform = null;
        ThreadPool.QueueUserWorkItem(_ => WaveformChanged?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>
    /// Loads the track's envelope from the cache, or decodes it on a separate decode-only channel and caches
    /// it, in the background. A null <paramref name="openDecoder"/> means cache only. Call under <see cref="_gate"/>.
    /// </summary>
    private void StartWaveform(string? cacheKey, Func<DecodeChannel>? openDecoder)
    {
        if (cacheKey is null && openDecoder is null) return;

        var cts = new CancellationTokenSource();
        _waveformCts = cts;
        Task.Run(() =>
        {
            float[]? envelope = null;
            try
            {
                if (cacheKey is not null) envelope = _waveformCache!.TryLoad(cacheKey, WaveformBins);
                if (envelope is null && openDecoder is not null)
                {
                    envelope = ComputeWaveform(openDecoder, cts.Token);
                    if (envelope is not null && cacheKey is not null) _waveformCache!.Save(cacheKey, envelope);
                }
            }
            catch { envelope = null; }

            lock (_gate)
            {
                // A newer track has loaded since; its own pass owns the waveform now.
                if (cts.IsCancellationRequested || envelope is null) return;
                Waveform = envelope;
            }

            WaveformChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    /// <summary>A decode-only BASS channel for the waveform pass, and whatever must be disposed with it.</summary>
    private readonly record struct DecodeChannel(int Handle, IDisposable? Owner = null);

    private static float[]? ComputeWaveform(Func<DecodeChannel> openDecoder, CancellationToken ct)
    {
        var channel = openDecoder();
        var handle = channel.Handle;
        if (handle == 0)
        {
            channel.Owner?.Dispose();
            return null;
        }
        try
        {
            var channels = Math.Max(1, Bass.ChannelGetInfo(handle).Channels);

            // Sum of squares of the mono downmix per fixed-size block. Blocks rather than final bins because
            // a remote stream may not know its length up front; they are regrouped once decoding ends.
            const int blockFrames = 1024;
            var blocks = new List<double>();
            var buffer = new float[16384 * channels];
            double blockSum = 0;
            var blockFill = 0;
            while (!ct.IsCancellationRequested)
            {
                var read = Bass.ChannelGetData(handle, buffer, buffer.Length * sizeof(float));
                // Anything but a clean end (e.g. a dropped download) leaves the envelope partial: don't show
                // or cache it.
                if (read < 0)
                {
                    if (Bass.LastError != Errors.Ended) return null;
                    break;
                }
                if (read == 0)
                {
                    // A remote stream returns 0 while the download catches up; only -1 (or a stopped
                    // channel) is the end. Breaking here would stretch a fraction of a second over the bar.
                    if (Bass.ChannelIsActive(handle) == ManagedBass.PlaybackState.Stopped) break;
                    Thread.Sleep(20);
                    continue;
                }

                var frames = read / sizeof(float) / channels;
                for (var i = 0; i < frames; i++)
                {
                    var mono = 0f;
                    for (var c = 0; c < channels; c++) mono += buffer[i * channels + c];
                    mono /= channels;

                    blockSum += mono * mono;
                    if (++blockFill == blockFrames)
                    {
                        blocks.Add(blockSum);
                        blockSum = 0;
                        blockFill = 0;
                    }
                }
            }
            if (ct.IsCancellationRequested) return null;

            var totalFrames = (long)blocks.Count * blockFrames + blockFill;
            if (blockFill > 0) blocks.Add(blockSum);
            if (totalFrames == 0) return null;

            // Regroup blocks into evenly spaced bins → RMS loudness.
            var rms = new double[WaveformBins];
            for (var b = 0; b < WaveformBins; b++)
            {
                var first = (int)((long)b * blocks.Count / WaveformBins);
                var last = Math.Max(first + 1, (int)((long)(b + 1) * blocks.Count / WaveformBins));
                double sum = 0;
                long count = 0;
                for (var k = first; k < last && k < blocks.Count; k++)
                {
                    sum += blocks[k];
                    count += k == blocks.Count - 1 && blockFill > 0 ? blockFill : blockFrames;
                }
                rms[b] = count == 0 ? 0 : Math.Sqrt(sum / count);
            }

            // Triangular moving average keeps the outline soft without flattening the song's shape.
            var envelope = new float[WaveformBins];
            var peak = 0d;
            for (var i = 0; i < WaveformBins; i++)
            {
                double sum = 0, weightSum = 0;
                for (var k = -WaveformSmoothing; k <= WaveformSmoothing; k++)
                {
                    var j = i + k;
                    if (j < 0 || j >= WaveformBins) continue;
                    var weight = WaveformSmoothing + 1 - Math.Abs(k);
                    sum += rms[j] * weight;
                    weightSum += weight;
                }
                var value = sum / weightSum;
                envelope[i] = (float)value;
                peak = Math.Max(peak, value);
            }
            if (peak <= 0) return null;

            for (var i = 0; i < WaveformBins; i++) envelope[i] = (float)(envelope[i] / peak);
            return envelope;
        }
        finally
        {
            Bass.StreamFree(handle);
            channel.Owner?.Dispose();
        }
    }

    private void ApplyVolume()
    {
        if (_direct is { } direct) direct.Volume = _volume / 100.0;
        var handle = _stream;
        if (handle != 0)
            Bass.ChannelSetAttribute(handle, ChannelAttribute.Volume, _volume / 100.0);
    }

    private void OnState(PlaybackState state)
    {
        State = state;
        StateChanged?.Invoke(this, state);
    }

    private void FreeStream()
    {
        if (_direct is { } direct)
        {
            direct.Ended -= OnDirectEnded;
            try { direct.Dispose(); } catch { /* ignore device close errors */ }
            _direct = null;
        }

        if (_codec is { } codec)
        {
            codec.Dispose();
            _codec = null;
            _stream = 0;
        }

        if (_stream == 0) return;
        Bass.StreamFree(_stream);
        _stream = 0;
    }

    private static void EnsureCoreInitialized()
    {
        if (_initialized) return;
        lock (InitLock)
        {
            if (_initialized) return;
            // Default output device, 44.1 kHz. Init returns false if already initialised for the
            // device, which is harmless here.
            Bass.Init();
            _initialized = true;
        }
    }

    public void Dispose()
    {
        _positionTimer.Dispose();
        lock (_gate)
        {
            _waveformCts?.Cancel();
            FreeStream();
        }
    }
}
