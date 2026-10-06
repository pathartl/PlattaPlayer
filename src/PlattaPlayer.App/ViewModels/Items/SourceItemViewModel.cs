using CommunityToolkit.Mvvm.ComponentModel;
using PlattaPlayer.Core.Models;

namespace PlattaPlayer.App.ViewModels.Items;

/// <summary>Display wrapper around a configured <see cref="SourceConfig"/> on the settings page.</summary>
public sealed partial class SourceItemViewModel : ObservableObject
{
    public SourceItemViewModel(SourceConfig config) => Config = config;

    public SourceConfig Config { get; }
    public string DisplayName => Config.DisplayName;
    public SourceType Type => Config.Type;

    /// <summary>The source type as shown in the "Add source" menu.</summary>
    public string TypeLabel => Type switch
    {
        SourceType.Jellyfin => "Jellyfin library",
        SourceType.Plex => "Plex library",
        SourceType.Emby => "Emby library",
        SourceType.Navidrome => "Navidrome library",
        _ => "Local path"
    };

    /// <summary>Segoe Fluent glyph for the source type: folder for local paths, globe for media servers.</summary>
    public string Glyph => Type switch
    {
        SourceType.Jellyfin or SourceType.Plex or SourceType.Emby or SourceType.Navidrome => "",
        _ => ""
    };

    /// <summary>True while this source is being scanned (spins its reindex button).</summary>
    [ObservableProperty] private bool _isSyncing;
}
