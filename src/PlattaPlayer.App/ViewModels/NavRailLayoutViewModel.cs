using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using CommunityToolkit.Mvvm.Input;
using PlattaPlayer.App.ViewModels.Items;
using PlattaPlayer.Core.Abstractions;

namespace PlattaPlayer.App.ViewModels;

/// <summary>
/// Which nav rail entries are shown and in what order, as picked in Settings and persisted to
/// <see cref="IAppSettings"/>. A single shared instance drives both the rail (<see cref="ShownItems"/>) and the
/// Settings editor (<see cref="Items"/>, <see cref="PinnedItems"/>). Settings itself is always in the rail, so
/// it can't be hidden out of reach.
/// </summary>
public sealed partial class NavRailLayoutViewModel
{
    private static readonly Thickness GroupGap = new(0, 20, 0, 0);

    private readonly IAppSettings _settings;
    private bool _loading = true;

    public NavRailLayoutViewModel(IAppSettings settings)
    {
        _settings = settings;
        var hidden = settings.HiddenNavRailItems.ToHashSet();

        var defaults = CreateDefaultItems(hidden);
        var saved = settings.NavRailOrder;
        // Saved order first; an entry it doesn't name (added since it was saved) goes right after the entry
        // it follows by default, so e.g. Genres lands next to Songs rather than at the end.
        foreach (var key in saved)
            if (defaults.FirstOrDefault(d => d.Key == key) is { } item && !Items.Contains(item))
                Items.Add(item);
        for (var i = 0; i < defaults.Count; i++)
        {
            if (Items.Contains(defaults[i])) continue;
            var after = i == 0 ? -1 : Items.IndexOf(defaults[i - 1]);
            Items.Insert(after + 1, defaults[i]);
        }

        SourceFilter = new NavRailItemViewModel("SourceFilter", "Source filter", "IconSources", !hidden.Contains("SourceFilter"), OnChanged);
        TagEditor = new NavRailItemViewModel("TagEditor", "Tag editor", "IconTag", !hidden.Contains("TagEditor"), OnChanged);
        PinnedItems = [SourceFilter, TagEditor];

        _loading = false;
        Relayout();
    }

    private List<NavRailItemViewModel> CreateDefaultItems(HashSet<string> hidden) =>
    [
        new("Artists", "Artists", "IconArtist", !hidden.Contains("Artists"), OnChanged),
        new("Albums", "Albums", "IconAlbum", !hidden.Contains("Albums"), OnChanged),
        new("Songs", "Songs", "IconSong", !hidden.Contains("Songs"), OnChanged),
        new("Genres", "Genres", "IconGenre", !hidden.Contains("Genres"), OnChanged),
        new("Home", "Recent", "IconRecent", !hidden.Contains("Home"), OnChanged),
        new NavRailPlaylistsItemViewModel("Playlists", "Playlists", "IconPlaylist", !hidden.Contains("Playlists"), OnChanged),
    ];

    /// <summary>The reorderable entries, in rail order, shown or not.</summary>
    public ObservableCollection<NavRailItemViewModel> Items { get; } = new();

    /// <summary>The entries the rail draws, in order.</summary>
    public ObservableCollection<NavRailItemViewModel> ShownItems { get; } = new();

    /// <summary>Entries pinned to the bottom of the rail: they can be hidden but not moved.</summary>
    public IReadOnlyList<NavRailItemViewModel> PinnedItems { get; }

    public NavRailItemViewModel SourceFilter { get; }
    public NavRailItemViewModel TagEditor { get; }

    /// <summary>The section to open at startup: the first shown section, or Settings when none is.</summary>
    public string StartSection => ShownItems.FirstOrDefault()?.Key ?? "Settings";

    /// <summary>Sets a section link's count (no-op for keys not in the rail).</summary>
    public void SetCount(string key, string count)
    {
        var item = Items.FirstOrDefault(i => i.Key == key);
        if (item is not null) item.Count = count;
    }

    [RelayCommand]
    private void MoveUp(NavRailItemViewModel item) => Move(item, -1);

    [RelayCommand]
    private void MoveDown(NavRailItemViewModel item) => Move(item, +1);

    private void Move(NavRailItemViewModel item, int delta)
    {
        var from = Items.IndexOf(item);
        var to = from + delta;
        if (from < 0 || to < 0 || to >= Items.Count) return;
        Items.Move(from, to);
        OnChanged();
    }

    [RelayCommand]
    private void ResetToDefault()
    {
        _loading = true;
        var defaults = CreateDefaultItems([]).Select(d => d.Key).ToList();
        var ordered = Items.OrderBy(i => defaults.IndexOf(i.Key)).ToList();
        for (var i = 0; i < ordered.Count; i++)
            Items.Move(Items.IndexOf(ordered[i]), i);
        foreach (var item in Items.Concat(PinnedItems))
            item.IsShown = true;
        _loading = false;
        OnChanged();
    }

    private void OnChanged()
    {
        if (_loading) return;
        _settings.NavRailOrder = Items.Select(i => i.Key).ToList();
        _settings.HiddenNavRailItems = Items.Concat(PinnedItems).Where(i => !i.IsShown).Select(i => i.Key).ToList();
        _settings.Save();
        Relayout();
    }

    /// <summary>
    /// Rebuilds <see cref="ShownItems"/> and the grouping: consecutive section links form a group (the first
    /// such group headed "LIBRARY"), the playlists block is a group of its own, and groups are spaced apart.
    /// </summary>
    private void Relayout()
    {
        for (var i = 0; i < Items.Count; i++)
        {
            Items[i].CanMoveUp = i > 0;
            Items[i].CanMoveDown = i < Items.Count - 1;
        }

        ShownItems.Clear();
        var captioned = false;
        NavRailItemViewModel? previous = null;
        foreach (var item in Items.Where(i => i.IsShown))
        {
            var isBlock = item is NavRailPlaylistsItemViewModel;
            var startsGroup = previous is not null && (isBlock || previous is NavRailPlaylistsItemViewModel);
            item.GroupMargin = startsGroup ? GroupGap : default;
            item.ShowLibraryCaption = !isBlock && !captioned && (previous is null || startsGroup);
            captioned |= item.ShowLibraryCaption;
            ShownItems.Add(item);
            previous = item;
        }
    }
}
