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

public enum GenreSort { Name, MostTracks }

public sealed partial class GenresViewModel : PageViewModelBase
{
    private readonly ILibraryRepository _repository;
    private readonly ICoverArtCache _covers;
    private readonly INavigationService _navigation;
    private List<GenreItemViewModel> _all = new();

    public GenresViewModel(ILibraryRepository repository, ICoverArtCache covers, INavigationService navigation)
    {
        _repository = repository;
        _covers = covers;
        _navigation = navigation;
    }

    public override string Title => "Genres";

    public ObservableCollection<GenreItemViewModel> Genres { get; } = new();

    public bool IsEmpty => Genres.Count == 0 && !IsBusy;

    /// <summary>"23 genres".</summary>
    [ObservableProperty] private string _stats = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortLabel))]
    private GenreSort _sort = GenreSort.Name;

    public string SortLabel => Sort == GenreSort.MostTracks ? "Sort: Most tracks" : "Sort: Name A–Z";

    public override async Task InitializeAsync()
    {
        IsBusy = true;
        try
        {
            _all = (await _repository.GetGenresAsync()).Select(g => new GenreItemViewModel(g, _covers)).ToList();
            Stats = AudioFormatText.Count(_all.Count, "genre", "genres");
            ApplySort();
        }
        finally { IsBusy = false; OnPropertyChanged(nameof(IsEmpty)); }
    }

    partial void OnSortChanged(GenreSort value) => ApplySort();

    private void ApplySort()
    {
        IEnumerable<GenreItemViewModel> sorted = Sort == GenreSort.MostTracks
            ? _all.OrderByDescending(g => g.TrackCount).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            : _all.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase);
        Genres.Clear();
        foreach (var g in sorted) Genres.Add(g);
    }

    [RelayCommand]
    private void SetSort(GenreSort sort) => Sort = sort;

    [RelayCommand]
    private void OpenGenre(GenreItemViewModel item)
        => _navigation.NavigateTo<GenreDetailViewModel>(vm => vm.Genre = item.Name);
}
