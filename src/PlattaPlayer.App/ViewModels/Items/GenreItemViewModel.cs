using System.Collections.Generic;
using System.Linq;
using PlattaPlayer.App.Formatting;
using PlattaPlayer.Core.Abstractions;
using PlattaPlayer.Core.Models;

namespace PlattaPlayer.App.ViewModels.Items;

/// <summary>Display-ready wrapper around a <see cref="GenreSummary"/> for the Genres grid.</summary>
public sealed class GenreItemViewModel
{
    public GenreItemViewModel(GenreSummary genre, ICoverArtCache covers)
    {
        Genre = genre;
        // Like the artist cards: a row of up to four covers, the genre's biggest albums first.
        Covers = genre.CoverArtKeys
            .Select(covers.GetPath)
            .OfType<string>()
            .Distinct()
            .Take(4)
            .ToList();
        Meta = $"{AudioFormatText.Count(genre.AlbumCount, "album", "albums")} · {AudioFormatText.Count(genre.TrackCount, "track", "tracks")}";
    }

    public GenreSummary Genre { get; }

    public string Name => Genre.Name;
    public int TrackCount => Genre.TrackCount;
    public IReadOnlyList<string> Covers { get; }

    /// <summary>"12 albums · 140 tracks".</summary>
    public string Meta { get; }
}
