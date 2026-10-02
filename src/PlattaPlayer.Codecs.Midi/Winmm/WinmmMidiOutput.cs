using System.Diagnostics;
using System.Runtime.Versioning;
using PlattaPlayer.Codecs.Abstractions;

namespace PlattaPlayer.Codecs.Midi.Winmm;

/// <summary>
/// Streams a Standard MIDI File to an OS <c>midiOut</c> port in real time: a background thread dispatches the
/// file's channel messages via <c>midiOutShortMsg</c> at their scheduled wall-clock times. SysEx and meta
/// events other than tempo are not forwarded. Produces no PCM, so the host can't visualize it.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WinmmMidiOutput : ICodecDirectOutput
{
    private readonly Lock _gate = new();
    private readonly IntPtr _handle;
    private readonly bool _open;

    private readonly ScheduledMessage[] _events;
    private readonly double _durationSeconds;

    // Transport. _baseSeconds is the position captured when the clock last started; while playing the
    // live position is _baseSeconds + _clock.Elapsed.
    private readonly Stopwatch _clock = new();
    private double _baseSeconds;
    private int _index;
    private volatile bool _playing;
    private double _volume = 1;

    private Thread? _worker;
    private volatile bool _disposed;

    /// <summary>Opens the port. Throws when it can't be opened (e.g. another program holds it).</summary>
    public WinmmMidiOutput(MidiDevice device, ScheduledMessage[] events, double durationSeconds)
    {
        // Only channel messages are sent through midiOutShortMsg; SysEx is not forwarded.
        _events = Array.FindAll(events, static e => !e.IsSysEx);
        _durationSeconds = durationSeconds;
        Description = $"MIDI out · {device.Name}";

        var deviceId = device.WinmmDeviceId < 0 ? WinmmMidiInterop.MidiMapper : (uint)device.WinmmDeviceId;
        var result = WinmmMidiInterop.midiOutOpen(out _handle, deviceId, IntPtr.Zero, IntPtr.Zero, 0);
        if (result != 0)
            throw new IOException($"{device.Name} could not be opened (midiOutOpen error {result}).");
        _open = true;
        ApplyVolume();
    }

    public event EventHandler? Ended;

    public int SampleRate => 0;
    public int Channels => 0;
    public string Description { get; }
    public bool AllowsBackgroundDecode => false;

    public TimeSpan Duration => TimeSpan.FromSeconds(_durationSeconds);

    public TimeSpan Position
    {
        get { lock (_gate) return TimeSpan.FromSeconds(Math.Min(CurrentSeconds(), _durationSeconds)); }
    }

    public double Volume
    {
        set
        {
            _volume = Math.Clamp(value, 0, 1);
            ApplyVolume();
        }
    }

    /// <summary>Not used: the port plays the file itself.</summary>
    public int Read(Span<float> buffer) => 0;

    public void Play()
    {
        if (!_open) return;
        lock (_gate)
        {
            if (_playing) return;
            // Realign the dispatch cursor to the current position before the clock starts.
            _index = FirstIndexAtOrAfter(_baseSeconds);
            _clock.Restart();
            _playing = true;
        }
        EnsureWorker();
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (!_playing) return;
            _baseSeconds = CurrentSeconds();
            StopClockLocked();
            AllNotesOffLocked();
        }
    }

    public void Seek(TimeSpan position)
    {
        var target = Math.Clamp(position.TotalSeconds, 0, _durationSeconds);
        lock (_gate)
        {
            var wasPlaying = _playing;
            StopClockLocked();
            AllNotesOffLocked();

            // Restore controller/program/pitch state up to the target so instruments sound correct
            // after a jump (note-on/off messages are skipped — we don't want stuck or retriggered notes).
            RestoreStateLocked(target);

            _baseSeconds = target;
            _index = FirstIndexAtOrAfter(target);

            if (wasPlaying)
            {
                _clock.Restart();
                _playing = true;
            }
        }
    }

    // --- worker --------------------------------------------------------------------------------

    private void EnsureWorker()
    {
        if (_worker is not null) return;
        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "WinmmMidiOutput",
            Priority = ThreadPriority.AboveNormal
        };
        _worker.Start();
    }

    private void WorkerLoop()
    {
        while (!_disposed)
        {
            var ended = false;

            lock (_gate)
            {
                if (_playing)
                {
                    var pos = CurrentSeconds();
                    while (_index < _events.Length && _events[_index].Seconds <= pos)
                    {
                        WinmmMidiInterop.midiOutShortMsg(_handle, _events[_index].Msg);
                        _index++;
                    }

                    if (_index >= _events.Length && pos >= _durationSeconds)
                    {
                        _playing = false;
                        StopClockLocked();
                        ended = true;
                    }
                }
            }

            if (ended)
                Ended?.Invoke(this, EventArgs.Empty);

            Thread.Sleep(_playing ? 1 : 10);
        }
    }

    // --- helpers (call under _gate unless noted) -----------------------------------------------

    private double CurrentSeconds()
        => _clock.IsRunning ? _baseSeconds + _clock.Elapsed.TotalSeconds : _baseSeconds;

    private void StopClockLocked()
    {
        _clock.Reset();
        _playing = false;
    }

    private int FirstIndexAtOrAfter(double seconds)
    {
        // _events are sorted ascending by Seconds.
        var lo = 0;
        var hi = _events.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (_events[mid].Seconds < seconds) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private void RestoreStateLocked(double target)
    {
        if (!_open) return;
        for (var i = 0; i < _events.Length && _events[i].Seconds < target; i++)
        {
            var status = _events[i].Msg & 0xF0;
            // Control change (0xB0), program change (0xC0), channel pressure (0xD0), pitch bend (0xE0).
            if (status is 0xB0 or 0xC0 or 0xD0 or 0xE0)
                WinmmMidiInterop.midiOutShortMsg(_handle, _events[i].Msg);
        }
    }

    private void AllNotesOffLocked()
    {
        if (!_open) return;
        for (uint ch = 0; ch < 16; ch++)
        {
            // CC120 All Sound Off, CC123 All Notes Off, CC121 Reset All Controllers.
            WinmmMidiInterop.midiOutShortMsg(_handle, 0xB0u | ch | (120u << 8));
            WinmmMidiInterop.midiOutShortMsg(_handle, 0xB0u | ch | (123u << 8));
        }
    }

    private void ApplyVolume()
    {
        if (!_open) return;
        var scaled = (uint)Math.Clamp(_volume * 0xFFFF, 0, 0xFFFF);
        var stereo = scaled | (scaled << 16); // low word = left, high word = right
        WinmmMidiInterop.midiOutSetVolume(_handle, stereo);
    }

    public void Dispose()
    {
        _disposed = true;
        _worker?.Join(500);

        lock (_gate)
        {
            if (_open)
            {
                WinmmMidiInterop.midiOutReset(_handle);
                WinmmMidiInterop.midiOutClose(_handle);
            }
        }
    }
}
