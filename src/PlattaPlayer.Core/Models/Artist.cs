namespace PlattaPlayer.Core.Models;

/// <summary>An album artist (ID3 album-artist semantics), used as the primary artist grouping.</summary>
public class Artist
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Normalised name used for case-insensitive sorting/lookup.</summary>
    public string SortName { get; set; } = string.Empty;

    public List<Album> Albums { get; } = new();
}
