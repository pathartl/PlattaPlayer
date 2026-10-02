using PlattaPlayer.Visualizations.Abstractions;

namespace PlattaPlayer.Visualizations.Wmp.BarsAndWaves.Plugin;

/// <summary>
/// Windows Media Player's "Bars and Waves" visualization, exposed as a plugin. One plugin with four
/// presets — Bars, Ocean Mist, Fire Storm, Scope — mirroring how the real effect presents itself
/// (<c>GetPresetCount</c> returns 4). Next/Previous/Random cycle them.
/// </summary>
public sealed class WmpBarsAndWavesPlugin : IVisualizerPlugin
{
    public string Id => "wmp-bars-and-waves";

    public string DisplayName => "Bars and Waves";

    public VisualizerInstance Create(VisualizerHostContext context)
    {
        var view = new BarsAndWavesVisualizer { Tap = context.Tap };
        view.NameChanged += context.ReportName;
        return new VisualizerInstance { View = view, Controller = view };
    }
}
