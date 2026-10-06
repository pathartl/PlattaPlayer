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

/// <summary>Strongly-typed payload serialized into <see cref="SourceConfig.SettingsJson"/> for Plex sources.</summary>
public sealed class PlexSourceSettings
{
    /// <summary>The server connection chosen at sign-in (often a plex.direct HTTPS address).</summary>
    public string ServerUrl { get; set; } = string.Empty;

    /// <summary>plex.tv account UUID; empty when a browser sign-in couldn't look it up.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>The server's own access token (differs from the account token for servers shared with the user).</summary>
    public string AccessToken { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    /// <summary>Stable per-source X-Plex-Client-Identifier; generated once at sign-in.</summary>
    public string DeviceId { get; set; } = string.Empty;
}

/// <summary>Strongly-typed payload serialized into <see cref="SourceConfig.SettingsJson"/> for Emby sources.</summary>
public sealed class EmbySourceSettings
{
    /// <summary>Server address without the <c>/emby</c> path prefix (requests add it).</summary>
    public string ServerUrl { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string AccessToken { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;

    /// <summary>Stable per-source device id reported to the server; generated once at login.</summary>
    public string DeviceId { get; set; } = string.Empty;
}

/// <summary>Strongly-typed payload serialized into <see cref="SourceConfig.SettingsJson"/> for Navidrome sources.</summary>
public sealed class NavidromeSourceSettings
{
    /// <summary>Server address without the <c>/rest</c> path (requests add it).</summary>
    public string ServerUrl { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;

    /// <summary>Salt and md5(password + salt) for token auth, fixed at login so the password itself isn't kept.</summary>
    public string Salt { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;

    /// <summary>
    /// Hex-encoded password (<c>enc:...</c>), set only for servers that can't do token auth (e.g. LDAP-backed);
    /// empty otherwise.
    /// </summary>
    public string EncodedPassword { get; set; } = string.Empty;
}