using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlattaPlayer.App.Formatting;
using PlattaPlayer.App.ViewModels.Items;
using PlattaPlayer.Core.Abstractions;
using PlattaPlayer.Core.Models;

namespace PlattaPlayer.App.ViewModels;

/// <summary>
/// The queue panel: mirrors <see cref="IPlaybackService.Queue"/> onto the UI thread as rows, and forwards
/// edits (play next, add, reorder, remove, clear) back to the service.
/// </summary>
public sealed partial class QueueViewModel : ObservableObject
{
    private readonly IPlaybackService _playback;
    private readonly ICoverArtCache _covers;

    public QueueViewModel(IPlaybackService playback, ICoverArtCache covers)
    {
        _playback = playback;
        _covers = covers;
        _playback.QueueChanged += (_, _) => Dispatcher.UIThread.Post(Sync);
        Sync();
    }

    /// <summary>Rows in play order. Replaced wholesale when the queue changes beyond a single insert/remove.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private ObservableCollection<QueueItemViewModel> _items = new();

    /// <summary>Index of the playing row, or -1.</summary>
    [ObservableProperty] private int _currentIndex = -1;

    /// <summary>"12 songs · 48 min" for what's still to come.</summary>
    [ObservableProperty] private string _upNextSummary = string.Empty;

    [ObservableProperty] private bool _hasUpcoming;

    public bool IsEmpty => Items.Count == 0;

    public void PlayNext(IReadOnlyList<Track> tracks) => _playback.PlayNext(tracks);

    public void AddToQueue(IReadOnlyList<Track> tracks) => _playback.AddToQueue(tracks);

    /// <summary>
    /// Moves a row (drag-and-drop, Alt+↑/↓). The row moves here first so the list doesn't jump while the
    /// service catches up; the resulting <see cref="Sync"/> then finds the order already matching.
    /// </summary>
    public void Move(int from, int to)
    {
        if (from < 0 || from >= Items.Count || to < 0 || to >= Items.Count || from == to) return;
        Items.Move(from, to);
        _playback.MoveQueueEntry(from, to);
    }

    [RelayCommand]
    private Task Play(QueueItemViewModel item) => _playback.PlayQueueEntryAsync(Items.IndexOf(item));

    [RelayCommand]
    private Task Remove(QueueItemViewModel item) => _playback.RemoveQueueEntryAsync(Items.IndexOf(item));

    /// <summary>Moves a row to just after the playing one.</summary>
    [RelayCommand]
    private void MoveToNext(QueueItemViewModel item)
    {
        var from = Items.IndexOf(item);
        Move(from, from < CurrentIndex ? CurrentIndex : CurrentIndex + 1);
    }

    [RelayCommand]
    private void ClearUpcoming() => _playback.ClearUpcoming();

    private void Sync()
    {
        var entries = _playback.Queue;
        var current = Math.Min(_playback.CurrentIndex, entries.Count - 1);

        if (!TryApplyIncrementally(entries))
        {
            var existing = Items.ToDictionary(i => i.Entry);
            Items = new ObservableCollection<QueueItemViewModel>(
                entries.Select(e => existing.TryGetValue(e, out var vm) ? vm : new QueueItemViewModel(e, _covers)));
        }
        OnPropertyChanged(nameof(IsEmpty));

        var remaining = TimeSpan.Zero;
        for (var i = 0; i < Items.Count; i++)
        {
            var item = Items[i];
            item.IsCurrent = i == current;
            item.IsPlayed = i < current;
            if (i > current) remaining += item.Entry.Track.Duration;
        }

        CurrentIndex = current;
        var upcoming = Items.Count - current - 1;
        HasUpcoming = upcoming > 0;
        UpNextSummary = upcoming > 0
            ? $"{AudioFormatText.Count(upcoming, "song", "songs")} · {AudioFormatText.Runtime(remaining)}"
            : string.Empty;
    }

    /// <summary>
    /// Applies the common edits in place (no change, one contiguous insert, one contiguous removal) so the
    /// list keeps its scroll position; anything else (new queue, shuffle) is a rebuild.
    /// </summary>
    private bool TryApplyIncrementally(IReadOnlyList<QueueEntry> entries)
    {
        var old = Items;
        var prefix = 0;
        while (prefix < old.Count && prefix < entries.Count && old[prefix].Entry == entries[prefix]) prefix++;
        if (prefix == old.Count && prefix == entries.Count) return true;

        var suffix = 0;
        while (suffix < old.Count - prefix && suffix < entries.Count - prefix
               && old[old.Count - 1 - suffix].Entry == entries[entries.Count - 1 - suffix]) suffix++;

        if (prefix + suffix == old.Count)
        {
            // A block was inserted at prefix. Keep big inserts (a whole library) to one rebuild.
            var added = entries.Count - old.Count;
            if (added > 200) return false;
            for (var i = 0; i < added; i++)
                old.Insert(prefix + i, new QueueItemViewModel(entries[prefix + i], _covers));
            return true;
        }

        if (prefix + suffix == entries.Count)
        {
            // A block was removed at prefix.
            if (old.Count - entries.Count > 200) return false;
            for (var i = old.Count - entries.Count; i > 0; i--)
                old.RemoveAt(prefix);
            return true;
        }

        return false;
    }
}
