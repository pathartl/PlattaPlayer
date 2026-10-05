using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlattaPlayer.App.Visualizations;
using PlattaPlayer.Core.Abstractions;
using PlattaPlayer.Visualizations.Abstractions;

namespace PlattaPlayer.App.ViewModels;

/// <summary>
/// Live, in-memory tuning for the full-window visualizer plus the choice of which visualizer is active.
/// Its properties are bound straight onto the visualizer controls so edits take effect immediately, and
/// are surfaced through a flyout on the now-playing bar. The active-visualizer choice is persisted to
/// <see cref="IAppSettings"/>. A single shared instance is used by both the main window (for visibility)
/// and the settings page (for the picker).
/// </summary>
public sealed partial class VisualizationSettingsViewModel : ObservableObject, IVisualizerHostSettings
{
    private readonly IAppSettings _settings;

    public VisualizationSettingsViewModel(IEnumerable<IVisualizerPlugin> plugins, IAppSettings settings)
    {
        _settings = settings;
        // Built-in plugins are registered first; if an external plugin reuses an id, keep the first seen.
        AvailableVisualizers = plugins
            .GroupBy(p => p.Id)
            .Select(g => g.First())
            .ToList();
        // Restore the persisted choice by id; fall back to the first registered plugin. Retired ids are
        // mapped to their replacement first, so a plugin being renamed or merged does not silently reset
        // the user's selection. This is the only place ActiveVisualizerId is read.
        var persistedId = VisualizerIdMigration.Migrate(settings.ActiveVisualizerId);
        _activeVisualizer = AvailableVisualizers.FirstOrDefault(p => p.Id == persistedId)
                            ?? AvailableVisualizers.FirstOrDefault();

        if (persistedId != settings.ActiveVisualizerId && _activeVisualizer?.Id == persistedId)
        {
            settings.ActiveVisualizerId = persistedId;
            settings.Save();
        }
    }

    /// <summary>Seconds each preset is shown before auto-advancing. Zero disables auto-cycling.</summary>
    [ObservableProperty]
    private double _presetDuration = 20.0;

    /// <summary>Seconds a preset switch crossfades over. Zero switches instantly.</summary>
    [ObservableProperty]
    private double _blendDuration = 2.5;

    /// <summary>Name of the preset currently on screen. Updated by the control via <see cref="Controller"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PresetLabel))]
    private string _currentPresetName = "";

    /// <summary>Which full-window background visualizer plugin is shown; persisted on change.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PresetLabel))]
    private IVisualizerPlugin? _activeVisualizer;

    /// <summary>Shown in the Now Playing preset switcher: the preset name, else the visualizer's name.</summary>
    public string PresetLabel => !string.IsNullOrWhiteSpace(CurrentPresetName)
        ? CurrentPresetName
        : ActiveVisualizer?.DisplayName ?? "Visualizer off";

    partial void OnActiveVisualizerChanged(IVisualizerPlugin? value)
    {
        _settings.ActiveVisualizerId = value?.Id;
        _settings.Save();
    }

    /// <summary>The registered visualizer plugins, for the settings picker.</summary>
    public IReadOnlyList<IVisualizerPlugin> AvailableVisualizers { get; }

    /// <summary>The active visualizer control, wired up once the main window is shown. Null until then.</summary>
    public IVisualizationController? Controller { get; set; }

    [RelayCommand]
    private void NextPreset() => Controller?.NextPreset();

    [RelayCommand]
    private void PreviousPreset() => Controller?.PreviousPreset();

    [RelayCommand]
    private void RandomPreset() => Controller?.RandomPreset();

    /// <summary>Raised when the user asks for the visualizer to be rebuilt; the main window handles it.</summary>
    public event EventHandler? RestartRequested;

    /// <summary>Rebuilds the active visualizer from its plugin, for one that has frozen or gone black.</summary>
    [RelayCommand]
    private void RestartVisualizer() => RestartRequested?.Invoke(this, EventArgs.Empty);
}
