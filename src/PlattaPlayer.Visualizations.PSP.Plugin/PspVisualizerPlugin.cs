using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PlattaPlayer.Visualizations.Abstractions;
using PlattaPlayer.Visualizations.PSP.Firmware;

namespace PlattaPlayer.Visualizations.PSP.Plugin;

/// <summary>
/// The Sony PSP's music visualizers (<see cref="PspVisualizer"/>). They need the user's own PSP firmware
/// update: an official EBOOT.PBP in the ROMs folder, from which the textures and images are extracted on
/// first use (and cached in the plugin's data folder). Until then a status line explains what is missing.
/// </summary>
public sealed class PspVisualizerPlugin : IVisualizerPlugin
{
    public string Id => "psp";

    public string DisplayName => "PSP";

    public VisualizerInstance Create(VisualizerHostContext context)
    {
        var view = new PspVisualizer
        {
            Tap = context.Tap,
            DataDirectory = context.DataDirectory,
        };
        view.NameChanged += context.ReportName;

        var status = new TextBlock
        {
            Text = "Extracting PSP visualizer resources…",
            Foreground = Brushes.White,
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 520,
        };
        var root = new Grid { Background = Brushes.Black };
        root.Children.Add(view);
        root.Children.Add(status);

        var romsDirectory = context.RomsDirectory;
        var cacheDirectory = Path.Combine(context.DataDirectory, "firmware-cache");
        Task.Run(() =>
        {
            var result = romsDirectory is null ? null : PspFirmwareAssets.TryLoad(romsDirectory, cacheDirectory);
            Dispatcher.UIThread.Post(() =>
            {
                if (result?.Assets is { } assets)
                {
                    status.IsVisible = false;
                    view.Assets = assets;
                }
                else
                {
                    status.Text = result?.Error ?? "This host has no ROMs folder to look for a PSP firmware update in.";
                }
            });
        });

        return new VisualizerInstance { View = root, Controller = view };
    }
}
