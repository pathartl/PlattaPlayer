using PlattaPlayer.Visualizations.Abstractions;

namespace PlattaPlayer.Visualizations.Wmp.Alchemy.Plugin;

/// <summary>
/// The Windows Media Player "Alchemy" visualization at the window's resolution
/// (<see cref="WmpAlchemyVisualizer"/>, on the GPU).
/// </summary>
public sealed class WmpAlchemyPlugin : IVisualizerPlugin
{
    public string Id => "wmp-alchemy";

    public string DisplayName => "WMP Alchemy";

    public VisualizerInstance Create(VisualizerHostContext context)
    {
        var view = new WmpAlchemyVisualizer
        {
            Tap = context.Tap,
            DataDirectory = context.DataDirectory,
        };
        view.NameChanged += context.ReportName;
        return new VisualizerInstance { View = view, Controller = view };
    }
}

/// <summary>
/// Alchemy exactly as WMP shows it: the CPU port verified bit-exact against mpvis.DLL, rendering the
/// fixed 640x480 field and stretching it to the window (<see cref="WmpAlchemyCpuVisualizer"/>).
/// </summary>
public sealed class WmpAlchemyClassicPlugin : IVisualizerPlugin
{
    public string Id => "wmp-alchemy-classic";

    public string DisplayName => "WMP Alchemy (Classic 640x480)";

    public VisualizerInstance Create(VisualizerHostContext context)
    {
        var view = new WmpAlchemyCpuVisualizer
        {
            Tap = context.Tap,
            DataDirectory = context.DataDirectory,
        };
        view.NameChanged += context.ReportName;
        return new VisualizerInstance { View = view, Controller = view };
    }
}
