namespace PlattaPlayer.Core.Models;

/// <summary>
/// One slot in the play queue. Entries compare by reference, so the same track queued twice is two distinct
/// entries that can be moved or removed independently.
/// </summary>
public sealed class QueueEntry
{
    public QueueEntry(Track track) => Track = track;

    public Track Track { get; }
}
