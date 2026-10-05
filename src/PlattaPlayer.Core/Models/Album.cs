namespace PlattaPlayer.Core.Models;

/// <summary>An album, grouped under its album artist credit — one artist, or several ("A; B").</summary>
public class Album
{
    public int Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public string SortTitle { get; set; } = string.Empty;

    /// <summary>The first-credited album artist: where the album's artist link leads.</summary>
    public int AlbumArtistId { get; set; }
    public Artist? AlbumArtist { get; set; }

    /// <summary>The full album artist credit in canonical <see cref="TagValues"/> form ("A; B").</summary>
    public string ArtistCredit { get; set; } = string.Empty;

    /// <summary>Every credited album artist (the album shows under each of them).</summary>
    public List<Artist> Artists { get; } = new();

    public int? Year { get; set; }

    public string? Genre { get; set; }

    /// <summary>Hash key into the cover-art cache.</summary>
    public string? CoverArtKey { get; set; }

    public DateTimeOffset DateAdded { get; set; }

    public List<Track> Tracks { get; } = new();
}
