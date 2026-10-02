namespace PlattaPlayer.Core.Models;

/// <summary>An album, grouped under a single album artist.</summary>
public class Album
{
    public int Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public string SortTitle { get; set; } = string.Empty;

    public int AlbumArtistId { get; set; }
    public Artist? AlbumArtist { get; set; }

    public int? Year { get; set; }

    public string? Genre { get; set; }

    /// <summary>Hash key into the cover-art cache.</summary>
    public string? CoverArtKey { get; set; }

    public DateTimeOffset DateAdded { get; set; }

    public List<Track> Tracks { get; } = new();
}
