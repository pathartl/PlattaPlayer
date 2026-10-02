using PlattaPlayer.Codecs.Abstractions;

namespace PlattaPlayer.Codecs.Midi.Emulation;

/// <summary>
/// Plays a MIDI file through an <see cref="Emu88Synth"/>. The emulation is too expensive to pre-render a whole
/// song the way the SoundFont path does, so the decoder renders on demand: the host asks for audio, the
/// sequencer delivers the song's events that fall due and renders the device up to the next one.
/// <para>
/// Seeking re-establishes the device state instead of rendering the skipped part: the device is reset,
/// then the song's SysEx and controller/program state up to the target is replayed (with the audio
/// discarded), so instruments, effects and parts sound as they would have.
/// </para>
/// </summary>
internal sealed class EmulatedMidiDecoder : ICodecDecoder
{
    // Lets reverb and releases ring out past the last event before the song counts as ended.
    private const double TailSeconds = 2.0;

    // How long a reset takes to settle before the song's setup is sent: 88emu's own player waits 200 ms
    // (400 ms for GM2); the MT-32 family takes a moment longer, so allow for the slower of them.
    private const double ResetSettleSeconds = 0.4;

    // Events closer together than this are delivered with the same render call (88emu applies the MIDI
    // played since the previous call at the start of the next one, so this is the timing resolution).
    private const int MinRenderFrames = 32;

    private readonly Emu88Synth _synth;
    private readonly ScheduledMessage[] _events;
    private readonly long[] _eventFrames;
    private readonly long _endFrame;
    private float[] _scratch = new float[4096];

    // Song-time render cursor (frames) and the next event to deliver; guarded by the synth's gate.
    private long _songFrame;
    private int _index;
    private bool _needsChase = true;
    private bool _disposed;

    /// <summary>
    /// Takes the synth over and brings it to the song's start: silence, reset, settle. Blocks for the
    /// emulation of that.
    /// </summary>
    public EmulatedMidiDecoder(Emu88Synth synth, string modelName, ScheduledMessage[] events, double durationSeconds)
    {
        _synth = synth;
        _events = events;
        Duration = TimeSpan.FromSeconds(durationSeconds);
        Description = $"88emu · {modelName} (emulated)";

        var rate = synth.SampleRate;
        _eventFrames = new long[events.Length];
        for (var i = 0; i < events.Length; i++)
            _eventFrames[i] = (long)Math.Round(events[i].Seconds * rate);
        _endFrame = (long)Math.Ceiling((durationSeconds + TailSeconds) * rate);

        Seek(TimeSpan.Zero, chaseNow: true);
    }

    /// <summary>The device's own DAC rate.</summary>
    public int SampleRate => _synth.SampleRate;

    public int Channels => 2;

    /// <summary>The length of the song (without the release tail).</summary>
    public TimeSpan Duration { get; }

    public string Description { get; }

    /// <summary>A second decoder would need a second booted device.</summary>
    public bool AllowsBackgroundDecode => false;

    public TimeSpan Position
    {
        get
        {
            var seconds = (double)Interlocked.Read(ref _songFrame) / _synth.SampleRate;
            return TimeSpan.FromSeconds(Math.Min(seconds, Duration.TotalSeconds));
        }
    }

    /// <summary>Moves to <paramref name="position"/>; the device state is rebuilt on the next render.</summary>
    public void Seek(TimeSpan position) => Seek(position, chaseNow: false);

    private void Seek(TimeSpan position, bool chaseNow)
    {
        var target = (long)Math.Round(Math.Clamp(position.TotalSeconds, 0, Duration.TotalSeconds) * _synth.SampleRate);
        lock (_synth.Gate)
        {
            if (_disposed) return;
            _synth.Owner = this;
            Interlocked.Exchange(ref _songFrame, target);
            _index = FirstEventAtOrAfter(target);
            _needsChase = true;
            if (chaseNow) ChaseLocked();
        }
    }

    public int Read(Span<float> buffer)
    {
        var frames = buffer.Length / 2;
        lock (_synth.Gate)
        {
            if (_disposed) return 0;

            // The next track is taking the device over while this one still plays out: silence, but not the
            // end (that would skip ahead in the queue while the next track is loading).
            if (!ReferenceEquals(_synth.Owner, this) || !_synth.IsOpen)
            {
                buffer[..(frames * 2)].Clear();
                return frames;
            }

            frames = (int)Math.Min(frames, Math.Max(0, _endFrame - _songFrame));
            if (frames == 0) return 0;

            if (_needsChase) ChaseLocked();
            RenderLocked(buffer[..(frames * 2)], frames);
        }
        return frames;
    }

    /// <summary>Renders <paramref name="frames"/> of song time, delivering each event at its frame.</summary>
    private void RenderLocked(Span<float> output, int frames)
    {
        var produced = 0;
        while (produced < frames)
        {
            while (_index < _events.Length && _eventFrames[_index] <= _songFrame)
                Deliver(_events[_index++]);

            var next = _index < _events.Length ? Math.Min(_eventFrames[_index], _endFrame) : _endFrame;
            var chunk = (int)Math.Min(Math.Max(next - _songFrame, MinRenderFrames), frames - produced);
            _synth.Render(output.Slice(produced * 2, chunk * 2));

            produced += chunk;
            Interlocked.Add(ref _songFrame, chunk);
        }
    }

    private void Deliver(in ScheduledMessage message)
    {
        if (message.SysEx is { } sysex) _synth.PlaySysEx(sysex);
        else _synth.PlayMessage(message.Msg);
    }

    /// <summary>
    /// Rebuilds the device state at the render cursor: silence, reset and settle, then the song's SysEx
    /// before the cursor in order (each given time to be digested), then the controller/program/pitch
    /// state each channel ended up with. Notes are skipped. The audio of all of this is discarded.
    /// </summary>
    private void ChaseLocked()
    {
        _needsChase = false;
        _synth.PlaySilence();
        _synth.PlayDeviceReset();
        Discard(ResetSettleSeconds);

        var state = new ChannelState();
        for (var i = 0; i < _index; i++)
        {
            var message = _events[i];
            if (message.SysEx is { } sysex)
            {
                if (IsSystemReset(sysex)) state.Clear();
                _synth.PlaySysEx(sysex);
                // MIDI's wire time for the message (320 µs a byte) plus time for the firmware to apply it;
                // a reset needs as long as the one at the start.
                Discard(IsSystemReset(sysex) ? ResetSettleSeconds : 0.02 + sysex.Length * 0.00032);
            }
            else
            {
                state.Apply(message.Msg);
            }
        }

        // Send the channel state in small batches so the device's MIDI input keeps up.
        var sent = 0;
        foreach (var msg in state.Messages())
        {
            _synth.PlayMessage(msg);
            if (++sent % 32 == 0) Discard(0.01);
        }
        if (sent > 0) Discard(0.02);
    }

    /// <summary>Runs the device for <paramref name="seconds"/> without advancing the song, dropping the audio.</summary>
    private void Discard(double seconds)
    {
        var frames = (int)Math.Ceiling(seconds * _synth.SampleRate);
        while (frames > 0)
        {
            var chunk = Math.Min(frames, _scratch.Length / 2);
            _synth.Render(_scratch.AsSpan(0, chunk * 2));
            frames -= chunk;
        }
    }

    /// <summary>GM System On, GM2 System On, GS Reset, or the MT-32's all-parameters reset.</summary>
    private static bool IsSystemReset(byte[] s)
        => (s.Length >= 6 && s[0] == 0xF0 && s[1] == 0x7E && s[3] == 0x09 && s[4] is 0x01 or 0x03)
           || (s.Length >= 11 && s[0] == 0xF0 && s[1] == 0x41 && s[3] == 0x42 && s[4] == 0x12
               && s[5] == 0x40 && s[6] == 0x00 && s[7] == 0x7F)
           || (s.Length >= 9 && s[0] == 0xF0 && s[1] == 0x41 && s[3] == 0x16 && s[4] == 0x12
               && s[5] == 0x7F && s[6] == 0x00 && s[7] == 0x00);

    private int FirstEventAtOrAfter(long frame)
    {
        var lo = 0;
        var hi = _eventFrames.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (_eventFrames[mid] < frame) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    public void Dispose()
    {
        lock (_synth.Gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (ReferenceEquals(_synth.Owner, this))
            {
                _synth.PlaySilence();
                _synth.Owner = null;
            }
        }
    }

    /// <summary>
    /// The non-note state each channel accumulates. Plain controllers keep their last value; parameter
    /// numbers and data entry (RPN/NRPN, e.g. pitch-bend range) depend on their order and are kept as a
    /// sequence. Bank select is replayed ahead of the program change it qualifies.
    /// </summary>
    private sealed class ChannelState
    {
        private readonly int[,] _controllers = new int[16, 128];
        private readonly int[] _program = new int[16];
        private readonly int[] _pitchBend = new int[16];
        private readonly int[] _pressure = new int[16];
        private readonly List<uint> _ordered = new();

        public ChannelState() => Clear();

        public void Clear()
        {
            for (var ch = 0; ch < 16; ch++)
            {
                for (var cc = 0; cc < 128; cc++) _controllers[ch, cc] = -1;
                _program[ch] = _pitchBend[ch] = _pressure[ch] = -1;
            }
            _ordered.Clear();
        }

        public void Apply(uint msg)
        {
            var ch = (int)(msg & 0x0F);
            var d1 = (int)((msg >> 8) & 0x7F);
            var d2 = (int)((msg >> 16) & 0x7F);
            switch (msg & 0xF0)
            {
                case 0xB0 when d1 is 6 or 38 or 96 or 97 or >= 98 and <= 101:
                    _ordered.Add(msg);
                    break;
                case 0xB0 when d1 >= 120:
                    break; // channel mode messages: nothing to restore
                case 0xB0:
                    _controllers[ch, d1] = d2;
                    break;
                case 0xC0:
                    _program[ch] = d1;
                    break;
                case 0xD0:
                    _pressure[ch] = d1;
                    break;
                case 0xE0:
                    _pitchBend[ch] = d1 | (d2 << 7);
                    break;
            }
        }

        public IEnumerable<uint> Messages()
        {
            for (var ch = 0; ch < 16; ch++)
            {
                var c = (uint)ch;
                foreach (var cc in new[] { 0, 32 })
                    if (_controllers[ch, cc] >= 0) yield return 0xB0u | c | ((uint)cc << 8) | ((uint)_controllers[ch, cc] << 16);
                if (_program[ch] >= 0) yield return 0xC0u | c | ((uint)_program[ch] << 8);
                for (var cc = 1; cc < 128; cc++)
                    if (cc != 32 && _controllers[ch, cc] >= 0)
                        yield return 0xB0u | c | ((uint)cc << 8) | ((uint)_controllers[ch, cc] << 16);
                if (_pitchBend[ch] >= 0)
                    yield return 0xE0u | c | ((uint)(_pitchBend[ch] & 0x7F) << 8) | ((uint)(_pitchBend[ch] >> 7) << 16);
                if (_pressure[ch] >= 0) yield return 0xD0u | c | ((uint)_pressure[ch] << 8);
            }

            foreach (var msg in _ordered) yield return msg;
        }
    }
}
