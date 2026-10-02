using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlattaPlayer.Core.Abstractions;
using PlattaPlayer.Core.Models;

namespace PlattaPlayer.App.ViewModels;

/// <summary>Where a lyric line sits relative to the playhead; drives its size and opacity.</summary>
public enum LyricLineState { Past, Current, Next, Later, Unsynced }

public sealed partial class LyricLineViewModel : ObservableObject
{
    public LyricLineViewModel(int index, LyricLine line)
    {
        Index = index;
        Time = line.Time;
        Text = string.IsNullOrWhiteSpace(line.Text) ? "♪" : line.Text;
    }

    public int Index { get; }
    public TimeSpan? Time { get; }
    public string Text { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPast), nameof(IsCurrent), nameof(IsNext), nameof(IsLater))]
    private LyricLineState _state = LyricLineState.Later;

    public bool IsPast => State == LyricLineState.Past;
    public bool IsCurrent => State == LyricLineState.Current;
    public bool IsNext => State == LyricLineState.Next;
    public bool IsLater => State == LyricLineState.Later;
}

/// <summary>
/// Lyrics for the playing track: loads them through <see cref="ILyricsProvider"/> on track change and keeps the
/// current line in step with the playhead. Clicking a synced line seeks to it.
/// </summary>
public sealed partial class LyricsViewModel : ObservableObject
{
    private readonly ILyricsProvider _provider;
    private readonly NowPlayingViewModel _player;
    private CancellationTokenSource? _loadCts;
    private bool _statesApplied;

    public LyricsViewModel(ILyricsProvider provider, NowPlayingViewModel player)
    {
        _provider = provider;
        _player = player;
        _player.PropertyChanged += OnPlayerChanged;
    }

    public ObservableCollection<LyricLineViewModel> Lines { get; } = new();

    [ObservableProperty] private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoLyrics), nameof(IsUnsynced))]
    private bool _hasLyrics;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUnsynced))]
    private bool _isSynced;

    /// <summary>Badge text: "SYNCED · LRC", "SYNCED · SYLT" or "UNSYNCED".</summary>
    [ObservableProperty] private string _badge = string.Empty;

    /// <summary>Index of the line under the playhead, or -1 before the first line / when unsynced.</summary>
    [ObservableProperty] private int _currentIndex = -1;

    public bool HasNoLyrics => !HasLyrics && !IsLoading;
    public bool IsUnsynced => HasLyrics && !IsSynced;

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(HasNoLyrics));

    [RelayCommand]
    private void SeekToLine(LyricLineViewModel line)
    {
        if (IsSynced && line.Time is { } time)
            _player.SeekTo(time);
    }

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NowPlayingViewModel.Track))
            _ = LoadAsync(_player.Track);
        else if (e.PropertyName == nameof(NowPlayingViewModel.PositionSeconds))
            UpdateCurrent(TimeSpan.FromSeconds(_player.PositionSeconds));
    }

    private async Task LoadAsync(Track? track)
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();

        Lines.Clear();
        CurrentIndex = -1;
        _statesApplied = false;
        HasLyrics = false;
        if (track is null) { IsLoading = false; return; }

        IsLoading = true;
        Lyrics? lyrics = null;
        try { lyrics = await _provider.GetLyricsAsync(track, cts.Token); }
        catch (OperationCanceledException) { return; }
        catch { lyrics = null; }
        if (cts.IsCancellationRequested) return;

        IsLoading = false;
        if (lyrics is null || lyrics.Lines.Count == 0) return;

        IsSynced = lyrics.IsSynced;
        Badge = lyrics.IsSynced ? $"SYNCED · {lyrics.SourceLabel}" : "UNSYNCED";
        for (var i = 0; i < lyrics.Lines.Count; i++)
            Lines.Add(new LyricLineViewModel(i, lyrics.Lines[i])
            {
                State = lyrics.IsSynced ? LyricLineState.Later : LyricLineState.Unsynced
            });
        HasLyrics = true;
        UpdateCurrent(TimeSpan.FromSeconds(_player.PositionSeconds));
    }

    private void UpdateCurrent(TimeSpan position)
    {
        if (!IsSynced || Lines.Count == 0) return;

        // Lines are time-ordered; find the last one at or before the playhead.
        var index = -1;
        for (var i = 0; i < Lines.Count; i++)
        {
            if (Lines[i].Time is { } t && t <= position) index = i;
            else if (Lines[i].Time > position) break;
        }

        if (index == CurrentIndex && _statesApplied)
            return;

        _statesApplied = true;
        for (var i = 0; i < Lines.Count; i++)
            Lines[i].State = i < index ? LyricLineState.Past
                : i == index ? LyricLineState.Current
                : i == index + 1 ? LyricLineState.Next
                : LyricLineState.Later;
        CurrentIndex = index;
    }
}
