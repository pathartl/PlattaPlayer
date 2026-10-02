namespace PlattaPlayer.Core.Models;

/// <summary>
/// A configured media source. <see cref="SettingsJson"/> holds source-specific configuration
/// (local folder list, or Jellyfin server URL + access token) serialized as JSON.
/// </summary>
public class SourceConfig
{
    /// <summary>Stable string id (GUID) referenced by <see cref="Track.SourceId"/>.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public SourceType Type { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public string SettingsJson { get; set; } = "{}";

    public DateTimeOffset? LastSyncedAt { get; set; }
}
