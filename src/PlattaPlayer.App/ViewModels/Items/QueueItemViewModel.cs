using CommunityToolkit.Mvvm.ComponentModel;
using PlattaPlayer.Core.Abstractions;
using PlattaPlayer.Core.Models;

namespace PlattaPlayer.App.ViewModels.Items;

/// <summary>One row of the queue panel: a <see cref="QueueEntry"/> plus its position relative to the playing entry.</summary>
public sealed partial class QueueItemViewModel : ObservableObject
{
    public QueueItemViewModel(QueueEntry entry, ICoverArtCache covers)
    {
        Entry = entry;
        Track = new TrackItemViewModel(entry.Track, covers);
    }

    public QueueEntry Entry { get; }
    public TrackItemViewModel Track { get; }

    /// <summary>The entry the transport is on.</summary>
    [ObservableProperty] private bool _isCurrent;

    /// <summary>Before the current entry (already played, or skipped over).</summary>
    [ObservableProperty] private bool _isPlayed;

    /// <summary>Being dragged to a new position; the row dims while the drop marker shows where it will land.</summary>
    [ObservableProperty] private bool _isDragging;
}
