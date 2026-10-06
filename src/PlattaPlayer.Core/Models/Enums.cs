namespace PlattaPlayer.Core.Models;

/// <summary>Identifies the kind of media source a configuration/track belongs to.</summary>
public enum SourceType
{
    Local,
    Jellyfin,
    Plex,
    Emby,
    Navidrome
}

/// <summary>High-level playback engine state.</summary>
public enum PlaybackState
{
    Stopped,
    Playing,
    Paused,
    Buffering
}

/// <summary>Queue repeat behaviour.</summary>
public enum RepeatMode
{
    Off,
    All,
    One
}

/// <summary>How a resolved <see cref="PlayableMedia"/> should be opened by the engine.</summary>
public enum PlayableKind
{
    LocalFile,
    RemoteUrl
}
