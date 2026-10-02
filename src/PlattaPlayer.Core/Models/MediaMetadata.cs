namespace PlattaPlayer.Core.Models;

/// <summary>Now-playing metadata pushed to OS media controls (e.g. Windows SMTC).</summary>
/// <param name="Title">Track title.</param>
/// <param name="Artist">Display artist.</param>
/// <param name="Album">Album title.</param>
/// <param name="CoverArtPath">Absolute path to the cached cover image, if any.</param>
public sealed record MediaMetadata(string Title, string Artist, string Album, string? CoverArtPath);
