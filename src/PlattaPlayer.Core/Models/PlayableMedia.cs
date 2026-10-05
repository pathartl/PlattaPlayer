namespace PlattaPlayer.Core.Models;

/// <summary>
/// A resolved, ready-to-play handle produced by <see cref="Abstractions.IMediaSource.ResolvePlayableAsync"/>.
/// </summary>
/// <param name="Kind">Whether <paramref name="Location"/> is a local file path or a remote URL.</param>
/// <param name="Location">Absolute file path or stream URL.</param>
/// <param name="Subsong">For a song of a multi-song file, which one (see <see cref="Track.Subsong"/>).</param>
public sealed record PlayableMedia(PlayableKind Kind, string Location, int? Subsong = null);
