namespace PlattaPlayer.Core.Models;

/// <summary>
/// A resolved, ready-to-play handle produced by <see cref="Abstractions.IMediaSource.ResolvePlayableAsync"/>.
/// </summary>
/// <param name="Kind">Whether <paramref name="Location"/> is a local file path or a remote URL.</param>
/// <param name="Location">Absolute file path or stream URL.</param>
public sealed record PlayableMedia(PlayableKind Kind, string Location);
