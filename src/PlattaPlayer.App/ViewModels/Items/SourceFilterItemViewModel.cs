using System;
using CommunityToolkit.Mvvm.ComponentModel;
using PlattaPlayer.Core.Models;

namespace PlattaPlayer.App.ViewModels.Items;

/// <summary>One checkbox in the nav rail's source filter: whether the library views show this source's music.</summary>
public sealed partial class SourceFilterItemViewModel : ObservableObject
{
    private readonly Action _changed;

    public SourceFilterItemViewModel(SourceConfig config, bool isShown, Action changed)
    {
        Config = config;
        _isShown = isShown;
        _changed = changed;
    }

    public SourceConfig Config { get; }
    public string DisplayName => Config.DisplayName;

    [ObservableProperty] private bool _isShown;

    partial void OnIsShownChanged(bool value) => _changed();
}
