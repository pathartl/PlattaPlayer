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

    /// <summary>True while this source is being scanned (spins its reindex button).</summary>
    [ObservableProperty] private bool _isSyncing;
}
