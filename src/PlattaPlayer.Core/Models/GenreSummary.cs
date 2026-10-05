namespace PlattaPlayer.Core.Models;

/// <summary>
/// One genre in the library: a single <see cref="TagValues"/> value of a track's genre (falling back to its
/// album's), so a track tagged "Ambient; Drone" counts under both.
/// </summary>
/// <param name="CoverArtKeys">Cover keys of the genre's albums, those with the most tracks in it first.</param>
public sealed record GenreSummary(string Name, int AlbumCount, int TrackCount, IReadOnlyList<string> CoverArtKeys);
