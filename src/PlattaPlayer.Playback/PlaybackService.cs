using System.ComponentModel;
using System.Runtime.CompilerServices;
using PlattaPlayer.Core.Abstractions;
using PlattaPlayer.Core.Models;

namespace PlattaPlayer.Playback;

/// <summary>
/// Owns the play queue, shuffle/repeat and current-track state on top of an <see cref="IPlaybackEngine"/>.
/// Resolves playable media from the relevant source and records play statistics.
/// </summary>
public sealed class PlaybackService : IPlaybackService, IDisposable
{
    private readonly IPlaybackEngine _engine;
    private readonly IMediaSourceManager _sources;
    private readonly ILibraryRepository _repository;

    private readonly List<Track> _queue = new();
    private readonly List<int> _order = new();   // indices into _queue, possibly shuffled
    private int _orderPos = -1;
    private readonly Random _rng = new();

    public PlaybackService(IPlaybackEngine engine, IMediaSourceManager sources, ILibraryRepository repository)
    {
        _engine = engine;
        _sources = sources;
        _repository = repository;

        _engine.StateChanged += OnEngineStateChanged;
        _engine.PositionChanged += OnEnginePositionChanged;
        _engine.PlaybackEnded += OnEnginePlaybackEnded;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Track? CurrentTrack { get; private set; }
    public PlaybackState State { get; private set; } = PlaybackState.Stopped;
    public TimeSpan Position { get; private set; }
    public TimeSpan Duration { get; private set; }
    public string Renderer { get; private set; } = string.Empty;

    public int Volume
    {
        get => _engine.Volume;
        set { _engine.Volume = Math.Clamp(value, 0, 100); OnPropertyChanged(); }
    }

    private bool _shuffle;
    public bool ShuffleEnabled
    {
        get => _shuffle;
        set { if (_shuffle == value) return; _shuffle = value; RebuildOrder(preserveCurrent: true); OnPropertyChanged(); }
    }

    private RepeatMode _repeat = RepeatMode.Off;
    public RepeatMode RepeatMode
    {
        get => _repeat;
        set { _repeat = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasNext)); }
    }

    public bool HasNext => _repeat != RepeatMode.Off || _orderPos < _order.Count - 1;
    public bool HasPrevious => _orderPos > 0;

    public async Task PlayQueueAsync(IReadOnlyList<Track> tracks, int startIndex = 0, CancellationToken ct = default)
    {
        _queue.Clear();
        _queue.AddRange(tracks);
        RebuildOrder(preserveCurrent: false);

        // Position the order cursor so the requested track plays first.
        _orderPos = _order.IndexOf(Math.Clamp(startIndex, 0, Math.Max(0, _queue.Count - 1)));
        if (_orderPos < 0) _orderPos = 0;

        await PlayCurrentAsync(ct);
    }

    public Task PlayTrackAsync(Track track, CancellationToken ct = default)
        => PlayQueueAsync(new[] { track }, 0, ct);

    public async Task TogglePlayPauseAsync(CancellationToken ct = default)
    {
        if (CurrentTrack is null)
        {
            if (_queue.Count > 0) await PlayCurrentAsync(ct);
            return;
        }

        if (State == PlaybackState.Playing) _engine.Pause();
        else _engine.Play();
    }

    public async Task NextAsync(CancellationToken ct = default)
    {
        if (_order.Count == 0) return;

        if (_orderPos < _order.Count - 1) _orderPos++;
        else if (_repeat == RepeatMode.All) _orderPos = 0;
        else { _engine.Stop(); return; }

        await PlayCurrentAsync(ct);
    }

    public async Task PreviousAsync(CancellationToken ct = default)
    {
        if (_order.Count == 0) return;

        // Restart current track if we're more than a few seconds in.
        if (Position > TimeSpan.FromSeconds(3))
        {
            _engine.Seek(TimeSpan.Zero);
            return;
        }

        if (_orderPos > 0) _orderPos--;
        await PlayCurrentAsync(ct);
    }

    public void Seek(TimeSpan position) => _engine.Seek(position);

    private async Task PlayCurrentAsync(CancellationToken ct)
    {
        if (_orderPos < 0 || _orderPos >= _order.Count) return;

        var track = _queue[_order[_orderPos]];
        var source = await _sources.GetAsync(track.SourceId, ct);
        var media = await source.ResolvePlayableAsync(track, ct);

        await _engine.LoadAsync(media, ct);
        _engine.Play();

        CurrentTrack = track;
        Duration = _engine.Duration;
        Renderer = _engine.Renderer;
        OnPropertyChanged(nameof(Renderer));
        OnPropertyChanged(nameof(CurrentTrack));
        OnPropertyChanged(nameof(Duration));
        OnPropertyChanged(nameof(HasNext));
        OnPropertyChanged(nameof(HasPrevious));

        await _repository.RecordPlayAsync(track.Id, ct);
    }

    private void RebuildOrder(bool preserveCurrent)
    {
        var current = (_orderPos >= 0 && _orderPos < _order.Count) ? _order[_orderPos] : -1;

        _order.Clear();
        for (var i = 0; i < _queue.Count; i++) _order.Add(i);

        if (_shuffle)
        {
            // Fisher–Yates.
            for (var i = _order.Count - 1; i > 0; i--)
            {
                var j = _rng.Next(i + 1);
                (_order[i], _order[j]) = (_order[j], _order[i]);
            }
        }

        if (preserveCurrent && current >= 0)
        {
            var idx = _order.IndexOf(current);
            if (idx > 0) { (_order[0], _order[idx]) = (_order[idx], _order[0]); }
            _orderPos = 0;
        }
    }

    private void OnEngineStateChanged(object? sender, PlaybackState state)
    {
        State = state;
        OnPropertyChanged(nameof(State));
    }

    private void OnEnginePositionChanged(object? sender, TimeSpan position)
    {
        Position = position;
        if (_engine.Duration != Duration) { Duration = _engine.Duration; OnPropertyChanged(nameof(Duration)); }
        OnPropertyChanged(nameof(Position));
    }

    private async void OnEnginePlaybackEnded(object? sender, EventArgs e)
    {
        if (_repeat == RepeatMode.One) { _engine.Seek(TimeSpan.Zero); _engine.Play(); return; }
        await NextAsync();
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        _engine.StateChanged -= OnEngineStateChanged;
        _engine.PositionChanged -= OnEnginePositionChanged;
        _engine.PlaybackEnded -= OnEnginePlaybackEnded;
    }
}
