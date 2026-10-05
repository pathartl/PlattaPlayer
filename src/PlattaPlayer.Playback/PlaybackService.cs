using System.ComponentModel;
using System.Runtime.CompilerServices;
using PlattaPlayer.Core.Abstractions;
using PlattaPlayer.Core.Models;

namespace PlattaPlayer.Playback;

/// <summary>
/// Owns the play queue (add, play next, reorder, remove), shuffle/repeat and current-track state on top of an
/// <see cref="IPlaybackEngine"/>. Resolves playable media from the relevant source and records play statistics.
/// </summary>
public sealed class PlaybackService : IPlaybackService, IDisposable
{
    private readonly IPlaybackEngine _engine;
    private readonly IMediaSourceManager _sources;
    private readonly ILibraryRepository _repository;

    // The queue in play order, and the cursor into it. While shuffle is on, _unshuffled holds the original
    // order so turning shuffle off can restore it; adds and removes are mirrored into it, moves are not.
    // Engine callbacks advance the cursor off the UI thread, hence the lock.
    private readonly object _gate = new();
    private readonly List<QueueEntry> _queue = new();
    private List<QueueEntry>? _unshuffled;
    private int _current = -1;
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
    public event EventHandler? QueueChanged;

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
        set
        {
            lock (_gate)
            {
                if (_shuffle == value) return;
                _shuffle = value;
                if (value)
                {
                    _unshuffled = _queue.ToList();
                    ShuffleAroundCurrent();
                }
                else if (_unshuffled is not null)
                {
                    var current = CurrentEntry;
                    _queue.Clear();
                    _queue.AddRange(_unshuffled);
                    _unshuffled = null;
                    _current = current is null ? (_queue.Count > 0 ? 0 : -1) : _queue.IndexOf(current);
                }
            }
            OnPropertyChanged();
            OnQueueChanged();
        }
    }

    private RepeatMode _repeat = RepeatMode.Off;
    public RepeatMode RepeatMode
    {
        get => _repeat;
        set { _repeat = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasNext)); }
    }

    public bool HasNext { get { lock (_gate) return _repeat != RepeatMode.Off || _current < _queue.Count - 1; } }
    public bool HasPrevious { get { lock (_gate) return _current > 0; } }

    public IReadOnlyList<QueueEntry> Queue { get { lock (_gate) return _queue.ToArray(); } }
    public int CurrentIndex { get { lock (_gate) return _current; } }

    private QueueEntry? CurrentEntry => _current >= 0 && _current < _queue.Count ? _queue[_current] : null;

    public async Task PlayQueueAsync(IReadOnlyList<Track> tracks, int startIndex = 0, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _queue.Clear();
            _queue.AddRange(tracks.Select(t => new QueueEntry(t)));
            _current = _queue.Count == 0 ? -1 : Math.Clamp(startIndex, 0, _queue.Count - 1);
            _unshuffled = null;
            if (_shuffle)
            {
                // The requested track plays first; everything else follows in random order.
                _unshuffled = _queue.ToList();
                ShuffleAroundCurrent();
            }
        }
        OnQueueChanged();
        await PlayCurrentAsync(ct);
    }

    public Task PlayTrackAsync(Track track, CancellationToken ct = default)
        => PlayQueueAsync(new[] { track }, 0, ct);

    public async Task PlayQueueEntryAsync(int index, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (index < 0 || index >= _queue.Count) return;
            _current = index;
        }
        OnQueueChanged();
        await PlayCurrentAsync(ct);
    }

    public void PlayNext(IReadOnlyList<Track> tracks)
    {
        if (tracks.Count == 0) return;
        lock (_gate)
        {
            var entries = tracks.Select(t => new QueueEntry(t)).ToList();
            var current = CurrentEntry;
            _queue.InsertRange(_current + 1, entries);
            _unshuffled?.InsertRange(current is null ? 0 : _unshuffled.IndexOf(current) + 1, entries);
            // An empty queue gains a current entry, which Play then starts.
            if (_current < 0) _current = 0;
        }
        OnQueueChanged();
    }

    public void AddToQueue(IReadOnlyList<Track> tracks)
    {
        if (tracks.Count == 0) return;
        lock (_gate)
        {
            var entries = tracks.Select(t => new QueueEntry(t)).ToList();
            _queue.AddRange(entries);
            _unshuffled?.AddRange(entries);
            if (_current < 0) _current = 0;
        }
        OnQueueChanged();
    }

    public void MoveQueueEntry(int from, int to)
    {
        lock (_gate)
        {
            if (from < 0 || from >= _queue.Count || to < 0 || to >= _queue.Count || from == to) return;

            var entry = _queue[from];
            _queue.RemoveAt(from);
            _queue.Insert(to, entry);

            // Keep the cursor on the same entry.
            if (from == _current) _current = to;
            else if (from < _current && to >= _current) _current--;
            else if (from > _current && to <= _current) _current++;
        }
        OnQueueChanged();
    }

    public async Task RemoveQueueEntryAsync(int index, CancellationToken ct = default)
    {
        bool removedCurrent, hasReplacement;
        lock (_gate)
        {
            if (index < 0 || index >= _queue.Count) return;

            _unshuffled?.Remove(_queue[index]);
            _queue.RemoveAt(index);

            removedCurrent = index == _current;
            hasReplacement = removedCurrent && index < _queue.Count;
            if (index < _current) _current--;
            else if (removedCurrent && !hasReplacement) _current = _queue.Count - 1;
        }
        OnQueueChanged();
        if (!removedCurrent) return;

        // The removed entry was playing: carry on with the one that took its place, or stop.
        if (hasReplacement && State == PlaybackState.Playing)
        {
            await PlayCurrentAsync(ct);
            return;
        }

        _engine.Stop();
        CurrentTrack = null;
        OnPropertyChanged(nameof(CurrentTrack));
    }

    public void ClearUpcoming()
    {
        lock (_gate)
        {
            var from = _current + 1;
            if (from >= _queue.Count) return;

            var removed = _queue.GetRange(from, _queue.Count - from);
            _queue.RemoveRange(from, removed.Count);
            if (_unshuffled is not null)
            {
                var gone = removed.ToHashSet();
                _unshuffled.RemoveAll(gone.Contains);
            }
        }
        OnQueueChanged();
    }

    public async Task TogglePlayPauseAsync(CancellationToken ct = default)
    {
        if (CurrentTrack is null)
        {
            if (CurrentIndex >= 0) await PlayCurrentAsync(ct);
            return;
        }

        if (State == PlaybackState.Playing) _engine.Pause();
        else _engine.Play();
    }

    public async Task NextAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_queue.Count == 0) return;

            if (_current < _queue.Count - 1) _current++;
            else if (_repeat == RepeatMode.All) _current = 0;
            else { _engine.Stop(); return; }
        }
        OnQueueChanged();
        await PlayCurrentAsync(ct);
    }

    public async Task PreviousAsync(CancellationToken ct = default)
    {
        if (CurrentIndex < 0) return;

        // Restart current track if we're more than a few seconds in.
        if (Position > TimeSpan.FromSeconds(3))
        {
            _engine.Seek(TimeSpan.Zero);
            return;
        }

        lock (_gate)
        {
            if (_current > 0) _current--;
        }
        OnQueueChanged();
        await PlayCurrentAsync(ct);
    }

    public void Seek(TimeSpan position) => _engine.Seek(position);

    private async Task PlayCurrentAsync(CancellationToken ct)
    {
        Track track;
        lock (_gate)
        {
            if (CurrentEntry is not { } entry) return;
            track = entry.Track;
        }

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

    /// <summary>Moves the current entry to the front and shuffles everything else after it (Fisher–Yates).</summary>
    private void ShuffleAroundCurrent()
    {
        var current = CurrentEntry;
        var rest = _queue.Where(e => e != current).ToList();
        for (var i = rest.Count - 1; i > 0; i--)
        {
            var j = _rng.Next(i + 1);
            (rest[i], rest[j]) = (rest[j], rest[i]);
        }

        _queue.Clear();
        if (current is not null) _queue.Add(current);
        _queue.AddRange(rest);
        _current = _queue.Count == 0 ? -1 : 0;
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

    private void OnQueueChanged()
    {
        QueueChanged?.Invoke(this, EventArgs.Empty);
        OnPropertyChanged(nameof(HasNext));
        OnPropertyChanged(nameof(HasPrevious));
    }

    public void Dispose()
    {
        _engine.StateChanged -= OnEngineStateChanged;
        _engine.PositionChanged -= OnEnginePositionChanged;
        _engine.PlaybackEnded -= OnEnginePlaybackEnded;
    }
}
