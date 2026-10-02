namespace PlattaPlayer.Core.Models;

/// <summary>One lyric line. <see cref="Time"/> is null for unsynced lyrics.</summary>
public sealed record LyricLine(TimeSpan? Time, string Text);

/// <summary>Lyrics for a track, as found by an <see cref="Abstractions.ILyricsProvider"/>.</summary>
public sealed class Lyrics
{
    public required IReadOnlyList<LyricLine> Lines { get; init; }

    /// <summary>True when every line carries a timestamp (LRC / SYLT) and can be highlighted and seeked.</summary>
    public required bool IsSynced { get; init; }

    /// <summary>Where the lyrics came from, shown in the lyrics badge: "LRC", "SYLT" or "TAG".</summary>
    public required string SourceLabel { get; init; }
}
