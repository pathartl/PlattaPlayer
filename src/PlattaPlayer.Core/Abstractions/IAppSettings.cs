namespace PlattaPlayer.Core.Abstractions;

/// <summary>
/// Persisted, per-user application preferences (a small JSON file in the app data folder).
/// Distinct from <see cref="ILibraryRepository"/> which owns the music library database.
/// </summary>
public interface IAppSettings
{
    /// <summary>The value picked in Settings for a codec plugin's setting, or null when none was picked (the
    /// plugin then uses its default).</summary>
    string? GetCodecSetting(string codecId, string key);

    /// <summary>Records a codec plugin setting; null removes it. Call <see cref="Save"/> to persist.</summary>
    void SetCodecSetting(string codecId, string key, string? value);

    /// <summary>
    /// Stable identifier of the full-window visualizer plugin shown while audio plays
    /// (see <c>IVisualizerPlugin.Id</c>). Null means "use the default" — the first registered plugin.
    /// </summary>
    string? ActiveVisualizerId { get; set; }

    /// <summary>
    /// Whether remote (e.g. Jellyfin) tracks get a seek-bar waveform. Drawing it means fetching and decoding
    /// the whole track a second time alongside playback, so it can be turned off to save bandwidth. Default on.
    /// </summary>
    bool ShowRemoteWaveforms { get; set; }

    /// <summary>
    /// UI accent color as <c>#RRGGBB</c>, chosen in Settings. Null means "use the default" (the design's mint).
    /// </summary>
    string? AccentColor { get; set; }

    /// <summary>Persists the current values to disk.</summary>
    void Save();
}
