namespace PlattaPlayer.Core.Models;

/// <summary>Strongly-typed payload serialized into <see cref="SourceConfig.SettingsJson"/> for local sources.</summary>
public sealed class LocalSourceSettings
{
    public List<string> Folders { get; set; } = new();
}

/// <summary>Strongly-typed payload serialized into <see cref="SourceConfig.SettingsJson"/> for Jellyfin sources.</summary>
public sealed class JellyfinSourceSettings
{
    public string ServerUrl { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string AccessToken { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;

    /// <summary>Stable per-source device id reported to the server; generated once at login.</summary>
    public string DeviceId { get; set; } = string.Empty;
}
