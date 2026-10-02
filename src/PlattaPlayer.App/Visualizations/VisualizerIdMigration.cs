namespace PlattaPlayer.App.Visualizations;

/// <summary>
/// Maps persisted visualizer ids that no longer exist onto their replacements, so a user's selection
/// survives a plugin being retired instead of silently reverting to whichever plugin happens to be
/// registered first.
/// </summary>
internal static class VisualizerIdMigration
{
    private static readonly Dictionary<string, string> Renames = new(StringComparer.Ordinal)
    {
        // "WMP Bars" and "WMP Waves" were built from mpvis.dll's now-playing-strip renderers, having
        // been mistaken for the "Bars and Waves" visualization. The real effect lives in wmp.dll and is
        // now one plugin whose four presets include Bars and Scope, so both ids land there.
        ["wmp-bars"] = "wmp-bars-and-waves",
        ["wmp-waves"] = "wmp-bars-and-waves",
        // The approximate GPU Battery, replaced by the GPU port that runs the verified CPU port's logic.
        ["wmpfx-battery"] = "wmp-battery",
    };

    /// <summary>Returns the current id for <paramref name="id"/>, or it unchanged if no rename applies.</summary>
    public static string? Migrate(string? id)
        => id is not null && Renames.TryGetValue(id, out var replacement) ? replacement : id;
}
