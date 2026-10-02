using Avalonia.Controls;

namespace PlattaPlayer.Visualizations.Abstractions;

/// <summary>
/// A live visualizer built by an <see cref="IVisualizerPlugin"/>: the Avalonia control to host plus the
/// controller used to drive manual preset changes from the now-playing flyout.
/// </summary>
public sealed class VisualizerInstance
{
    /// <summary>The full-window visualizer surface to place in the window.</summary>
    public required Control View { get; init; }

    /// <summary>Manual preset/effect control (next / previous / random).</summary>
    public required IVisualizationController Controller { get; init; }
}
