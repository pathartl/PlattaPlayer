using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
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

/// <summary>
/// The Home page: a launch pad into the library. Quick links to each library section with its count, then shelves
/// for recently played albums, newly added albums, the biggest genres and the most played songs.
/// </summary>
public sealed partial class HomeViewModel : PageViewModelBase
{
    private const int ShelfSize = 16;
    private const int TopGenreCount = 8;
    private const int MostPlayedCount = 9;

    private readonly ILibraryRepository _repository;
    private readonly ICoverArtCache _covers;
    private readonly IPlaybackService _playback;
    private readonly INavigationService _navigation;

    public HomeViewModel(ILibraryRepository repository, ICoverArtCache covers, IPlaybackService playback, INavigationService navigation)
    {
        _repository = repository;
        _covers = covers;
        _playback = playback;
        _navigation = navigation;
    }

    public override string Title => "Home";

    /// <summary>"Good evening".</summary>
    public string Greeting => DateTime.Now.Hour switch
    {
        < 5 => "Good evening",
        < 12 => "Good morning",
        < 18 => "Good afternoon",
        _ => "Good evening",
    };

    /// <summary>"1,204 songs · 96 albums · 41 artists".</summary>
    [ObservableProperty] private string _stats = string.Empty;

    [ObservableProperty] private string _artistCount = "0";
    [ObservableProperty] private string _albumCount = "0";
    [ObservableProperty] private string _songCount = "0";
    [ObservableProperty] private string _genreCount = "0";
    [ObservableProperty] private string _playlistCount = "0";

    /// <summary>Albums of the most recently played tracks, most recent first.</summary>
    public ObservableCollection<AlbumItemViewModel> RecentlyPlayed { get; } = new();
    public ObservableCollection<AlbumItemViewModel> RecentlyAdded { get; } = new();
    /// <summary>The genres with the most tracks.</summary>
    public ObservableCollection<GenreItemViewModel> TopGenres { get; } = new();
    public ObservableCollection<TrackItemViewModel> MostPlayed { get; } = new();

    public bool HasRecentlyPlayed => RecentlyPlayed.Count > 0;
    public bool HasRecentlyAdded => RecentlyAdded.Count > 0;
    public bool HasTopGenres => TopGenres.Count > 0;
    public bool HasMostPlayed => MostPlayed.Count > 0;

    /// <summary>The library is empty (or every source is filtered out).</summary>
    public bool IsEmpty => !IsBusy && RecentlyAdded.Count == 0;

    public override async Task InitializeAsync()
    {
        IsBusy = true;
        try
        {
            var counts = await _repository.GetLibraryCountsAsync();
            var genres = await _repository.GetGenresAsync();
            var playlists = await _repository.GetPlaylistsAsync();
            ArtistCount = Format(counts.Artists);
            AlbumCount = Format(counts.Albums);
            SongCount = Format(counts.Tracks);
            GenreCount = Format(genres.Count);
            PlaylistCount = Format(playlists.Count);
            Stats = $"{AudioFormatText.Count(counts.Tracks, "song", "songs")} · " +
                    $"{AudioFormatText.Count(counts.Albums, "album", "albums")} · " +
                    $"{AudioFormatText.Count(counts.Artists, "artist", "artists")}";

            // Recent plays are per track; collapse them to their albums so the shelf reads "jump back in".
            var recentAlbums = (await _repository.GetRecentlyPlayedAsync(ShelfSize * 12))
                .Where(t => t.Album is not null)
                .DistinctBy(t => t.AlbumId)
                .Take(ShelfSize)
                .Select(t => t.Album!)
                .ToList();
            Fill(RecentlyPlayed, recentAlbums.Select(a => new AlbumItemViewModel(a, _covers)));
            Fill(RecentlyAdded, (await _repository.GetRecentlyAddedAlbumsAsync(ShelfSize)).Select(a => new AlbumItemViewModel(a, _covers)));
            Fill(TopGenres, genres
                .OrderByDescending(g => g.TrackCount)
                .ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
                .Take(TopGenreCount)
                .Select(g => new GenreItemViewModel(g, _covers)));
            Fill(MostPlayed, (await _repository.GetMostPlayedAsync(MostPlayedCount)).Select(t => new TrackItemViewModel(t, _covers)));
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(Greeting));
            OnPropertyChanged(nameof(HasRecentlyPlayed));
            OnPropertyChanged(nameof(HasRecentlyAdded));
            OnPropertyChanged(nameof(HasTopGenres));
            OnPropertyChanged(nameof(HasMostPlayed));
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    private static string Format(int count) => count.ToString("N0", CultureInfo.CurrentCulture);

    private static void Fill<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items) target.Add(item);
    }

    [RelayCommand]
    private void OpenAlbum(AlbumItemViewModel item)
        => _navigation.NavigateTo<AlbumDetailViewModel>(vm => vm.AlbumId = item.Id);

    [RelayCommand]
    private void OpenGenre(GenreItemViewModel item)
        => _navigation.NavigateTo<GenreDetailViewModel>(vm => vm.Genre = item.Name);

    /// <summary>Plays the most played list as a queue, starting at <paramref name="item"/>.</summary>
    [RelayCommand]
    private Task PlayMostPlayed(TrackItemViewModel item)
    {
        var tracks = MostPlayed.Select(t => t.Track).ToList();
        return _playback.PlayQueueAsync(tracks, Math.Max(0, MostPlayed.IndexOf(item)));
    }
}
