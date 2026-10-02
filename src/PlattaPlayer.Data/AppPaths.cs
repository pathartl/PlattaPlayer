namespace PlattaPlayer.Data;

/// <summary>Resolves the per-user application data locations (database, cover cache, soundfont).</summary>
public static class AppPaths
{
    /// <summary>%LOCALAPPDATA%\PlattaPlayer (created on first access).</summary>
    public static string DataDirectory { get; } = EnsureDir(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PlattaPlayer"));

    public static string DatabasePath => Path.Combine(DataDirectory, "library.db");

    public static string CoversDirectory { get; } = EnsureDir(Path.Combine(DataDirectory, "covers"));

    /// <summary>Cached seek-bar envelopes, one small file per track; safe to delete at any time.</summary>
    public static string WaveformsDirectory { get; } = EnsureDir(Path.Combine(DataDirectory, "waveforms"));

    /// <summary>User-supplied MilkDrop <c>.milk</c> presets for the visualizer (empty until populated).</summary>
    public static string PresetsDirectory { get; } = EnsureDir(Path.Combine(DataDirectory, "presets"));

    /// <summary>
    /// Drop-in folder for firmware dumps (<see cref="UserFolder"/> "Roms"): the emulated sound modules of the
    /// MIDI codec plugin (Sound Canvas, MT-32, …) and the PSP firmware update the PSP visualizers read.
    /// Empty until populated.
    /// </summary>
    public static string RomsDirectory { get; } = UserFolder("Roms");

    /// <summary>
    /// A drop-in folder the user manages, shared by plugins by name (<c>%LOCALAPPDATA%\PlattaPlayer\&lt;name&gt;</c>,
    /// e.g. "Roms", "SoundFonts"), created on demand. <paramref name="name"/> must be a plain folder name.
    /// </summary>
    public static string UserFolder(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException($"Not a plain folder name: {name}", nameof(name));
        return EnsureDir(Path.Combine(DataDirectory, name));
    }

    /// <summary>
    /// Drop-in folder for external visualizer plugins. Each plugin is an assembly (ideally in its own
    /// subfolder with its private dependencies) exposing an <c>IVisualizerPlugin</c>. Empty until populated.
    /// </summary>
    public static string PluginsDirectory { get; } = EnsureDir(Path.Combine(DataDirectory, "plugins"));

    /// <summary>
    /// Per-plugin private data folder (<c>%LOCALAPPDATA%\PlattaPlayer\plugins-data\&lt;id&gt;</c>), created on
    /// demand. Scoped by plugin id so plugins never collide; handed to a plugin via its host context.
    /// </summary>
    public static string PluginDataDirectory(string id)
        => EnsureDir(Path.Combine(DataDirectory, "plugins-data", id));

    /// <summary>Persisted application preferences (visualizer, accent, codec plugin settings, …).</summary>
    public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    public static string SqliteConnectionString => $"Data Source={DatabasePath}";

    private static string EnsureDir(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
