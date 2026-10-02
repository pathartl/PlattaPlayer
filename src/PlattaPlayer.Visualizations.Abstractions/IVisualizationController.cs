namespace PlattaPlayer.Visualizations.Abstractions;

/// <summary>
/// Manual preset control for a full-window visualizer. Implemented by the visualizer's control and
/// driven from the now-playing flyout (next / previous / random).
/// </summary>
public interface IVisualizationController
{
    /// <summary>Advance to the next preset (wraps around).</summary>
    void NextPreset();

    /// <summary>Go back to the previous preset (wraps around).</summary>
    void PreviousPreset();

    /// <summary>Switch to a randomly chosen preset.</summary>
    void RandomPreset();
}
