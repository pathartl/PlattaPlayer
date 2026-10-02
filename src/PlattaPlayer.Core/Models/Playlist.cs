namespace PlattaPlayer.Core.Models;

/// <summary>A user-curated, ordered collection of tracks.</summary>
public class Playlist
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public DateTimeOffset DateCreated { get; set; }

    public List<PlaylistTrack> Items { get; } = new();
}

/// <summary>Join entity giving each track an explicit position within a playlist.</summary>
public class PlaylistTrack
{
    public int Id { get; set; }

    public int PlaylistId { get; set; }
    public Playlist? Playlist { get; set; }

    public int TrackId { get; set; }
    public Track? Track { get; set; }

    /// <summary>Zero-based ordering within the playlist.</summary>
    public int Position { get; set; }
}
