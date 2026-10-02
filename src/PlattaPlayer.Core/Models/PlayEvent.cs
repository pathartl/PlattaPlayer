namespace PlattaPlayer.Core.Models;

/// <summary>
/// One recorded playback of a track. The source of truth for the Home view's
/// "recently played" / "most played" sections; <see cref="Track.PlayCount"/> and
/// <see cref="Track.LastPlayedAt"/> are denormalised rollups of these rows.
/// </summary>
public class PlayEvent
{
    public long Id { get; set; }

    public int TrackId { get; set; }
    public Track? Track { get; set; }

    public DateTimeOffset PlayedAt { get; set; }
}
