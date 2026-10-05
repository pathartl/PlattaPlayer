using System;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PlattaPlayer.App.ViewModels.Items;

/// <summary>
/// One configurable nav rail entry: a section link (Artists, Albums, …) or, as
/// <see cref="NavRailPlaylistsItemViewModel"/>, the playlists block. The same instance backs both the rail and
/// its row in Settings.
/// </summary>
public partial class NavRailItemViewModel : ObservableObject
{
    private readonly Action _changed;

    public NavRailItemViewModel(string key, string label, string iconKey, bool isShown, Action changed)
    {
        Key = key;
        Label = label;
        Icon = Application.Current?.TryGetResource(iconKey, null, out var icon) == true ? icon as Geometry : null;
        _isShown = isShown;
        _changed = changed;
    }

    /// <summary>The section key passed to <c>NavigateCommand</c> and matched against <c>SelectedSection</c>.</summary>
    public string Key { get; }
    public string Label { get; }
    public Geometry? Icon { get; }

    [ObservableProperty] private bool _isShown;

    partial void OnIsShownChanged(bool value) => _changed();

    /// <summary>Item count shown at the right of the link (blank for entries without one).</summary>
    [ObservableProperty] private string _count = string.Empty;

    // ---- Rail layout, recomputed whenever the shown entries change ----------------------------------

    /// <summary>The first run of section links gets the "LIBRARY" caption.</summary>
    [ObservableProperty] private bool _showLibraryCaption;

    /// <summary>Space above the entry where it starts a new group (after the playlists block or before it).</summary>
    [ObservableProperty] private Thickness _groupMargin;

    // ---- Settings row ---------------------------------------------------------------------------------

    [ObservableProperty] private bool _canMoveUp;
    [ObservableProperty] private bool _canMoveDown;
}

/// <summary>The rail's playlists block (its header plus one link per playlist).</summary>
public sealed class NavRailPlaylistsItemViewModel(string key, string label, string iconKey, bool isShown, Action changed)
    : NavRailItemViewModel(key, label, iconKey, isShown, changed);
