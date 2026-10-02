using System.Collections.Generic;
using System.Linq;
using PlattaPlayer.App.Formatting;
using PlattaPlayer.Core.Abstractions;
using PlattaPlayer.Core.Models;

namespace PlattaPlayer.App.ViewModels.Items;

/// <summary>Display-ready wrapper around an <see cref="Artist"/> for the Artists grid and list.</summary>
public sealed class ArtistItemViewModel
{
    public ArtistItemViewModel(Artist artist, ICoverArtCache covers, int trackCount)
    {
        Artist = artist;
        TrackCount = trackCount;

        // Libraries rarely have artist photos, so the card shows a row of up to four of the artist's
        // covers instead (newest first), each whole.
        Covers = artist.Albums
            .OrderByDescending(a => a.Year ?? 0).ThenBy(a => a.SortTitle)
            .Select(a => covers.GetPath(a.CoverArtKey))
            .Where(p => p is not null)
            .Distinct()
            .Take(4)
            .Cast<string>()
            .ToList();

        var albums = artist.Albums.Count;
        Meta = $"{AudioFormatText.Count(albums, "album", "albums")} · {AudioFormatText.Count(trackCount, "track", "tracks")}";

        var first = SortKey.Length > 0 ? char.ToUpperInvariant(SortKey[0]) : '#';
        Letter = first is >= 'A' and <= 'Z' ? first : '#';
    }

    public Artist Artist { get; }

    public int Id => Artist.Id;
    public string Name => Artist.Name;
    public int AlbumCount => Artist.Albums.Count;
    public int TrackCount { get; }

    /// <summary>Name used for ordering and the A–Z jump bar (the sort tag when present).</summary>
    public string SortKey => string.IsNullOrWhiteSpace(Artist.SortName) ? Artist.Name : Artist.SortName;

    /// <summary>Up to four cover paths for the card's art row.</summary>
    public IReadOnlyList<string> Covers { get; }

    /// <summary>First cover, for the list view.</summary>
    public string? CoverPath => Covers.Count > 0 ? Covers[0] : null;

    /// <summary>"3 albums · 34 tracks".</summary>
    public string Meta { get; }

    /// <summary>A–Z jump bar bucket; '#' for digits and symbols.</summary>
    public char Letter { get; }
}
