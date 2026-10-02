using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlattaPlayer.App.Formatting;
using PlattaPlayer.App.Services;
using PlattaPlayer.App.ViewModels.Items;
using PlattaPlayer.Core.Abstractions;

namespace PlattaPlayer.App.ViewModels;

public enum ArtistSort { NameAscending, NameDescending, MostTracks }

/// <summary>One entry of the A–Z jump bar; letters with no artists are dimmed and inert.</summary>
public sealed record JumpLetter(char Letter, bool IsAvailable)
{
    public string Text => Letter.ToString();
}

public sealed partial class ArtistsViewModel : PageViewModelBase
{
    private readonly ILibraryRepository _repository;
    private readonly ICoverArtCache _covers;
    private readonly INavigationService _navigation;
    private List<ArtistItemViewModel> _all = new();

    public ArtistsViewModel(ILibraryRepository repository, ICoverArtCache covers, INavigationService navigation)
    {
        _repository = repository;
        _covers = covers;
        _navigation = navigation;
    }

    public override string Title => "Artists";

    public ObservableCollection<ArtistItemViewModel> Artists { get; } = new();

    public ObservableCollection<JumpLetter> Letters { get; } = new();

    public bool IsEmpty => Artists.Count == 0 && !IsBusy;

    /// <summary>"48 artists · 212 albums · 2,931 songs".</summary>
    [ObservableProperty] private string _stats = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortLabel))]
    private ArtistSort _sort = ArtistSort.NameAscending;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsListView))]
    private bool _isGridView = true;

    public bool IsListView => !IsGridView;

    public string SortLabel => Sort switch
    {
        ArtistSort.NameDescending => "Sort: Name Z–A",
        ArtistSort.MostTracks => "Sort: Most tracks",
        _ => "Sort: Name A–Z",
    };

    /// <summary>Raised when the jump bar asks the view to scroll to an artist index.</summary>
    public event Action<int>? JumpRequested;

    public override async Task InitializeAsync()
    {
        IsBusy = true;
        try
        {
            var artists = await _repository.GetArtistsAsync();
            var trackCounts = await _repository.GetTrackCountsByArtistAsync();
            var counts = await _repository.GetLibraryCountsAsync();

            _all = artists
                .Select(a => new ArtistItemViewModel(a, _covers, trackCounts.TryGetValue(a.Id, out var n) ? n : 0))
                .ToList();

            Stats = $"{AudioFormatText.Count(counts.Artists, "artist", "artists")} · "
                    + $"{AudioFormatText.Count(counts.Albums, "album", "albums")} · "
                    + $"{AudioFormatText.Count(counts.Tracks, "song", "songs")}";
            ApplySort();
        }
        finally { IsBusy = false; OnPropertyChanged(nameof(IsEmpty)); }
    }

    partial void OnSortChanged(ArtistSort value) => ApplySort();

    private void ApplySort()
    {
        IEnumerable<ArtistItemViewModel> sorted = Sort switch
        {
            ArtistSort.NameDescending => _all.OrderByDescending(a => a.SortKey, StringComparer.CurrentCultureIgnoreCase),
            ArtistSort.MostTracks => _all.OrderByDescending(a => a.TrackCount).ThenBy(a => a.SortKey, StringComparer.CurrentCultureIgnoreCase),
            _ => _all.OrderBy(a => a.SortKey, StringComparer.CurrentCultureIgnoreCase),
        };

        Artists.Clear();
        foreach (var a in sorted) Artists.Add(a);

        // The jump bar only makes sense for alphabetical order.
        Letters.Clear();
        if (Sort != ArtistSort.MostTracks)
        {
            var present = _all.Select(a => a.Letter).ToHashSet();
            foreach (var c in "#ABCDEFGHIJKLMNOPQRSTUVWXYZ")
                Letters.Add(new JumpLetter(c, present.Contains(c)));
        }
    }

    [RelayCommand]
    private void SetSort(ArtistSort sort) => Sort = sort;

    [RelayCommand]
    private void JumpTo(JumpLetter letter)
    {
        if (!letter.IsAvailable) return;
        var index = Artists.ToList().FindIndex(a => a.Letter == letter.Letter);
        if (index >= 0) JumpRequested?.Invoke(index);
    }

    [RelayCommand]
    private void OpenArtist(ArtistItemViewModel item)
        => _navigation.NavigateTo<ArtistDetailViewModel>(vm => vm.ArtistId = item.Id);
}
