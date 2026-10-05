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

/// <summary>One genre: the albums with tracks in it, newest first, and play/shuffle of all those tracks.</summary>
public sealed partial class GenreDetailViewModel : PageViewModelBase
{
    private readonly ILibraryRepository _repository;
    private readonly ICoverArtCache _covers;
    private readonly INavigationService _navigation;
    private readonly IPlaybackService _playback;
    private IReadOnlyList<Track> _tracks = Array.Empty<Track>();

    public GenreDetailViewModel(ILibraryRepository repository, ICoverArtCache covers,
        INavigationService navigation, IPlaybackService playback)
    {
        _repository = repository;
        _covers = covers;
        _navigation = navigation;
        _playback = playback;
    }

    /// <summary>The genre value shown (set before navigation).</summary>
    public string Genre { get; set; } = string.Empty;

    public override string Title => Genre;

    /// <summary>"12 albums · 140 tracks · 9 h 3 min".</summary>
    [ObservableProperty] private string _meta = string.Empty;

    public ObservableCollection<AlbumItemViewModel> Albums { get; } = new();

    public override async Task InitializeAsync()
    {
        IsBusy = true;
        try
        {
            _tracks = await _repository.GetGenreTracksAsync(Genre);

            // Tracks come newest album first, so grouping keeps that order.
            Albums.Clear();
            foreach (var group in _tracks.Where(t => t.Album is not null).GroupBy(t => t.AlbumId))
                Albums.Add(new AlbumItemViewModel(group.First().Album!, _covers, group.ToList()));

            var runtime = TimeSpan.FromTicks(_tracks.Sum(t => t.Duration.Ticks));
            var parts = new List<string>
            {
                AudioFormatText.Count(Albums.Count, "album", "albums"),
                AudioFormatText.Count(_tracks.Count, "track", "tracks"),
            };
            if (runtime > TimeSpan.Zero) parts.Add(AudioFormatText.Runtime(runtime));
            Meta = string.Join(" · ", parts);
        }
        finally { IsBusy = false; }
    }

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
    private void OpenGenres() => _navigation.NavigateTo<GenresViewModel>();
}
