using System;
using System.Collections.Generic;
using Avalonia;
using PlattaPlayer.Core.Abstractions;

namespace PlattaPlayer.Visualizations.Abstractions;

/// <summary>
/// Everything the host provides to a visualizer plugin when building its control: the live audio
/// signal, the shared tuning, the chrome panels to frost, and a callback to surface the current
/// preset/effect name. Plugins consume only what they need.
/// </summary>
public sealed class VisualizerHostContext
{
    /// <summary>Live audio signal to visualize. May be null until playback starts.</summary>
    public IAudioTap? Tap { get; init; }

    /// <summary>Shared, live tuning (preset/blend durations).</summary>
    public required IVisualizerHostSettings Settings { get; init; }

    /// <summary>Chrome panels whose backdrops should show a blurred copy of the visualization.</summary>
    public required IReadOnlyList<Visual> BlurTargets { get; init; }

    /// <summary>Reports the current preset/effect name; the host marshals it to the UI thread.</summary>
    public required Action<string> ReportName { get; init; }

    /// <summary>
    /// A private, writable folder the plugin may use for its own data (presets, caches, error logs). The
    /// host creates it and scopes it per plugin (so two plugins never collide). Already exists when passed.
    /// </summary>
    public required string DataDirectory { get; init; }

    /// <summary>
    /// The user's ROMs folder: firmware dumps the user supplies themselves, which plugins that emulate
    /// original hardware may read (never write). Searched with its subfolders. May be null if the host has
    /// no such folder.
    /// </summary>
    public string? RomsDirectory { get; init; }
}
