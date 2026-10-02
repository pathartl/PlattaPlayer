using PlattaPlayer.Visualizations.Abstractions;

namespace PlattaPlayer.Visualizations.Wmp.Battery.Plugin;

/// <summary>
/// The Windows Media Player "Battery" visualization at the window's resolution
/// (<see cref="WmpBatteryVisualizer"/>, on the GPU).
/// </summary>
public sealed class WmpBatteryPlugin : IVisualizerPlugin
{
    public string Id => "wmp-battery";

    public string DisplayName => "WMP Battery";

    public VisualizerInstance Create(VisualizerHostContext context)
    {
        var view = new WmpBatteryVisualizer
        {
            Tap = context.Tap,
            DataDirectory = context.DataDirectory,
        };
        view.NameChanged += context.ReportName;
        return new VisualizerInstance { View = view, Controller = view };
    }
}

/// <summary>
/// Battery exactly as WMP shows it: the CPU port verified bit-exact against wmp.dll, rendering the
/// fixed 384x288 field and stretching it to the window (<see cref="WmpBatteryCpuVisualizer"/>).
/// </summary>
public sealed class WmpBatteryClassicPlugin : IVisualizerPlugin
{
    public string Id => "wmp-battery-classic";

    public string DisplayName => "WMP Battery (Classic 384x288)";

    public VisualizerInstance Create(VisualizerHostContext context)
    {
        var view = new WmpBatteryCpuVisualizer
        {
            Tap = context.Tap,
            DataDirectory = context.DataDirectory,
        };
        view.NameChanged += context.ReportName;
        return new VisualizerInstance { View = view, Controller = view };
    }
}
