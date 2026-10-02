using Avalonia.Data;
using PlattaPlayer.Visualizations.Abstractions;

namespace PlattaPlayer.Visualizations.MilkDrop.Plugin;

/// <summary>The pure-managed MilkDrop (butterchurn-style) visualizer, exposed as a plugin.</summary>
public sealed class MilkdropVisualizerPlugin : IVisualizerPlugin
{
    public string Id => "milkdrop";

    public string DisplayName => "MilkDrop";

    public VisualizerInstance Create(VisualizerHostContext context)
    {
        var view = new MilkdropVisualizer
        {
            Tap = context.Tap,
            // The host's per-plugin data folder doubles as the presets folder (drop .milk files here);
            // empty/missing falls back to the built-in preset. Also used for the GL error log.
            PresetsPath = context.DataDirectory,
            DataDirectory = context.DataDirectory,
        };

        view.Bind(MilkdropVisualizer.PresetDurationProperty,
            new Binding(nameof(IVisualizerHostSettings.PresetDuration)) { Source = context.Settings });
        view.Bind(MilkdropVisualizer.BlendDurationProperty,
            new Binding(nameof(IVisualizerHostSettings.BlendDuration)) { Source = context.Settings });

        // Chrome panels paint a blurred copy of the viz behind their light tint.
        foreach (var target in context.BlurTargets)
            view.BlurTargets.Add(target);

        view.PresetChanged += context.ReportName;

        return new VisualizerInstance { View = view, Controller = view };
    }
}
