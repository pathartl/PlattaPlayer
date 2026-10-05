using PlattaPlayer.Core.Abstractions;
using PlattaPlayer.Core.Models;

namespace PlattaPlayer.App.ViewModels.Items;

/// <summary>Lightweight, display-ready wrapper around a <see cref="Track"/>.</summary>
public sealed class TrackItemViewModel
{
    public TrackItemViewModel(Track track, ICoverArtCache covers)
    {
        Track = track;
        CoverPath = covers.GetPath(track.CoverArtKey ?? track.Album?.CoverArtKey);
    }

    public Track Track { get; }

    public int Id => Track.Id;
    public string Title => Track.Title;
    public string Artist => TagValues.Display(string.IsNullOrEmpty(Track.TrackArtist) ? Track.Album?.ArtistCredit : Track.TrackArtist)
        is { Length: > 0 } artist ? artist : "Unknown Artist";
    public string AlbumTitle => Track.Album?.Title ?? string.Empty;

    /// <summary>Album-artist id used to navigate from the track's artist name (0 when unknown).</summary>
    public int ArtistId => Track.Album?.AlbumArtistId ?? 0;
    public int AlbumId => Track.AlbumId;
    public string? CoverPath { get; }
    public string Duration => Track.Duration.ToString(Track.Duration.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss");
    public int? TrackNo => Track.TrackNo;

    /// <summary>Track number for the "#" column (blank when untagged).</summary>
    public string Number => Track.TrackNo?.ToString() ?? string.Empty;

    /// <summary>Compact quality for the format column, e.g. "24/192" or "320".</summary>
    public string Quality => Formatting.AudioFormatText.Short(Track);
}
