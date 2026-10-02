using System.ComponentModel;

namespace PlattaPlayer.Visualizations.Abstractions;

/// <summary>
/// Live tuning the host exposes to a visualizer plugin. Implements <see cref="INotifyPropertyChanged"/>
/// so a plugin can bind its control's properties straight onto these and pick up changes immediately.
/// </summary>
public interface IVisualizerHostSettings : INotifyPropertyChanged
{
    /// <summary>Seconds each preset is shown before auto-advancing. Zero disables auto-cycling.</summary>
    double PresetDuration { get; }

    /// <summary>Seconds a preset switch crossfades over. Zero switches instantly.</summary>
    double BlendDuration { get; }
}
