namespace PlattaPlayer.Visualizations.Abstractions;

/// <summary>
/// A pluggable full-window visualizer. Each implementation contributes one entry to the Settings
/// picker and knows how to build its own Avalonia surface. The host discovers built-in plugins through
/// dependency injection and external ones by loading assemblies from the plugins folder; both kinds are
/// treated identically. Implementations must have a public parameterless constructor so the external
/// loader can instantiate them.
/// </summary>
public interface IVisualizerPlugin
{
    /// <summary>Stable identifier persisted in settings (see <c>IAppSettings.ActiveVisualizerId</c>).</summary>
    string Id { get; }

    /// <summary>Human-readable name shown in the Settings picker.</summary>
    string DisplayName { get; }

    /// <summary>Builds this visualizer's control and wires it to the host-provided context.</summary>
    VisualizerInstance Create(VisualizerHostContext context);
}
