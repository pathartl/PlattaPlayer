using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PlattaPlayer.App.Formatting;
using PlattaPlayer.Core.Abstractions;
using PlattaPlayer.Core.Models;

namespace PlattaPlayer.App.ViewModels.Items;

/// <summary>Display-ready wrapper around an <see cref="Album"/> for grid tiles.</summary>
public sealed partial class AlbumItemViewModel
{
    public AlbumItemViewModel(Album album, ICoverArtCache covers, IReadOnlyList<Track>? tracks = null)
    {
        Album = album;
        CoverPath = covers.GetPath(album.CoverArtKey);
        Tracks = tracks ?? Array.Empty<Track>();

        ReleaseType = InferReleaseType(album.Title, Tracks);

        // The most common format on the album, e.g. "FLAC 24/96" (mixed-format albums show the majority).
        FormatLabel = Tracks
            .GroupBy(AudioFormatText.WithCodec)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault() ?? string.Empty;
    }

    public Album Album { get; }
    public IReadOnlyList<Track> Tracks { get; }

    public int Id => Album.Id;
    public int ArtistId => Album.AlbumArtistId;
    public string Title => Album.Title;
    public string Artist => TagValues.Display(Album.ArtistCredit) is { Length: > 0 } artist ? artist : "Unknown Artist";
    public string? Year => Album.Year?.ToString();
    public string? CoverPath { get; }

    /// <summary>"Album", "EP", "Single" or "Live".</summary>
    public string ReleaseType { get; }

    /// <summary>"2021 · Album".</summary>
    public string YearAndType => Year is null ? ReleaseType : $"{Year} · {ReleaseType}";

    /// <summary>"FLAC 24/192", "MP3 320"; empty when no track data was loaded.</summary>
    public string FormatLabel { get; }

    [GeneratedRegex(@"\blive\b", RegexOptions.IgnoreCase)]
    private static partial Regex LiveWord();

    [GeneratedRegex(@"(\bEP\b|\(EP\)|\[EP\])$", RegexOptions.IgnoreCase)]
    private static partial Regex EpSuffix();

    /// <summary>
    /// The library has no release-type tag yet, so this is inferred: "live" in the title → Live; an "EP"
    /// suffix, or 4–6 tracks under 30 minutes → EP; 1–3 tracks → Single; otherwise Album.
    /// </summary>
    private static string InferReleaseType(string title, IReadOnlyList<Track> tracks)
    {
        if (LiveWord().IsMatch(title)) return "Live";
        if (EpSuffix().IsMatch(title.Trim())) return "EP";
        if (tracks.Count == 0) return "Album";
        if (tracks.Count <= 3) return "Single";
        var minutes = tracks.Sum(t => t.Duration.TotalMinutes);
        if (tracks.Count <= 6 && minutes < 30) return "EP";
        return "Album";
    }
}
