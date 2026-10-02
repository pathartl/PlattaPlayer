using PlattaPlayer.Visualizations.PSP.Common;
using PlattaPlayer.Visualizations.PSP.Visualizers;

namespace PlattaPlayer.Visualizations.PSP;

/// <summary>One selectable PSP visualizer.</summary>
/// <param name="Type">The id Vis_CreateVisualizer (0x17f8) switches on; its thumbnail is music_tex_vis_thum_(Type-1).</param>
/// <param name="Name">Descriptive name (the XMB only ever showed the thumbnail).</param>
/// <param name="Create">Builds the visualizer as the factory does.</param>
public sealed record PspVisualizerInfo(int Type, string Name, Func<PspRuntime, Visualizer> Create);

/// <summary>
/// The visualizers of visualizer_plugin.prx that run outside the XMB. Type 1 is missing on purpose: it
/// animates the XMB theme background itself (paf PhSolidWave, the theme's GMO model and wallpaper), none
/// of which exists here.
/// </summary>
public static class PspVisualizerCatalog
{
    public static IReadOnlyList<PspVisualizerInfo> All { get; } =
    [
        new(2, "LED Spectrum", r => new LedSpectrum(r)),
        new(3, "Clouds", r => new SpriteTrails(r, SpriteTrails.VariantClouds)),
        new(4, "Waveform History", r => new WaveformHistory(r)),
        new(5, "Block Wave", r => new BlockWave(r)),
        new(6, "Dashed Curves", r => new SpriteTrails(r, SpriteTrails.VariantDashedCurves)),
        new(7, "Sand Trails", r => new SandTrails(r)),
    ];
}
