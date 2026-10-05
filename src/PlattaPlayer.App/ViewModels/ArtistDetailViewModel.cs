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
using PlattaPlayer.Core.Models;

namespace PlattaPlayer.App.ViewModels;

/// <summary>A discography filter chip ("All 6", "EPs 2").</summary>
public sealed record ReleaseFilter(string Key, string Label, int Count)
{
    public string Text => $"{Label} {Count}";
}

public sealed partial class ArtistDetailViewModel : PageViewModelBase
{
    private static readonly (string Key, string Label)[] FilterOrder =
        { ("Album", "Albums"), ("EP", "EPs"), ("Single", "Singles"), ("Live", "Live") };

    private readonly ILibraryRepository _repository;
    private readonly ICoverArtCache _covers;
    private readonly INavigationService _navigation;
    private readonly IPlaybackService _playback;
    private List<AlbumItemViewModel> _all = new();
    private IReadOnlyList<Track> _tracks = Array.Empty<Track>();

    public ArtistDetailViewModel(ILibraryRepository repository, ICoverArtCache covers,
        INavigationService navigation, IPlaybackService playback)
    {
        _repository = repository;
        _covers = covers;
        _navigation = navigation;
        _playback = playback;
    }

    public int ArtistId { get; set; }

    public override string Title => ArtistName;

    [ObservableProperty] private string _artistName = string.Empty;

    /// <summary>"6 releases · 61 tracks · 4 h 12 min · Ambient, Dream pop".</summary>
    [ObservableProperty] private string _meta = string.Empty;

    /// <summary>Releases shown under the current filter, newest first.</summary>
    public ObservableCollection<AlbumItemViewModel> Albums { get; } = new();

    public ObservableCollection<ReleaseFilter> Filters { get; } = new();

    [ObservableProperty] private ReleaseFilter? _selectedFilter;

    public override async Task InitializeAsync()
    {
        IsBusy = true;
        try
        {
            var artist = await _repository.GetArtistAsync(ArtistId);
            if (artist is null) return;

            ArtistName = artist.Name;
            OnPropertyChanged(nameof(Title));

            _tracks = await _repository.GetArtistTracksAsync(ArtistId);
            var byAlbum = _tracks.GroupBy(t => t.AlbumId).ToDictionary(g => g.Key, g => (IReadOnlyList<Track>)g.ToList());

            _all = artist.Albums
                .OrderByDescending(a => a.Year ?? 0).ThenBy(a => a.SortTitle)
                .Select(a => new AlbumItemViewModel(a, _covers, byAlbum.TryGetValue(a.Id, out var t) ? t : null))
                .ToList();

            var runtime = TimeSpan.FromTicks(_tracks.Sum(t => t.Duration.Ticks));
            var genres = _tracks.SelectMany(t => TagValues.Split(t.Genre ?? t.Album?.Genre))
                .GroupBy(g => g, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .Take(2)
                .Select(g => g.Key);
            var parts = new List<string>
            {
                AudioFormatText.Count(_all.Count, "release", "releases"),
                AudioFormatText.Count(_tracks.Count, "track", "tracks"),
            };
            if (runtime > TimeSpan.Zero) parts.Add(AudioFormatText.Runtime(runtime));
            var genreText = string.Join(", ", genres);
            if (genreText.Length > 0) parts.Add(genreText);
            Meta = string.Join(" · ", parts);

            Filters.Clear();
            Filters.Add(new ReleaseFilter("All", "All", _all.Count));
            foreach (var (key, label) in FilterOrder)
            {
                var n = _all.Count(a => a.ReleaseType == key);
                if (n > 0) Filters.Add(new ReleaseFilter(key, label, n));
            }
            SelectedFilter = Filters[0];
        }
        finally { IsBusy = false; }
    }

    partial void OnSelectedFilterChanged(ReleaseFilter? value)
    {
        Albums.Clear();
        foreach (var a in _all.Where(a => value is null || value.Key == "All" || a.ReleaseType == value.Key))
            Albums.Add(a);
    }

    [RelayCommand]
    private void SelectFilter(ReleaseFilter filter) => SelectedFilter = filter;

    [RelayCommand]
    private Task PlayAll()
    {
        _playback.ShuffleEnabled = false;
        return _tracks.Count == 0 ? Task.CompletedTask : _playback.PlayQueueAsync(_tracks, 0);
    }

    [RelayCommand]
    private Task Shuffle()
    {
        if (_tracks.Count == 0) return Task.CompletedTask;
        _playback.ShuffleEnabled = true;
        return _playback.PlayQueueAsync(_tracks, Random.Shared.Next(_tracks.Count));
    }

    [RelayCommand]
    private void OpenAlbum(AlbumItemViewModel item)
        => _navigation.NavigateTo<AlbumDetailViewModel>(vm => vm.AlbumId = item.Id);

    [RelayCommand]
    private void OpenArtists() => _navigation.NavigateTo<ArtistsViewModel>();
}
