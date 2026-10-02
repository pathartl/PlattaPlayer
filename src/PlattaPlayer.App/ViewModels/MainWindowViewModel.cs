using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlattaPlayer.App.Services;
using PlattaPlayer.App.ViewModels.Items;
using PlattaPlayer.Core.Abstractions;

namespace PlattaPlayer.App.ViewModels;

/// <summary>
/// Root shell view-model: the nav rail (sections, counts, playlists), the library content region, the
/// transport bar, and the Now Playing mode that hides the library chrome and gives the window to the
/// visualizer. Now Playing is a mode over the current library page rather than a page of its own, so
/// "‹ Library" returns to exactly where the user was.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly INavigationService _navigation;
    private readonly ILibraryRepository _repository;

    public MainWindowViewModel(
        INavigationService navigation,
        ILibraryRepository repository,
        NowPlayingViewModel nowPlaying,
        LyricsViewModel lyrics,
        VisualizationSettingsViewModel visualization)
    {
        _navigation = navigation;
        _repository = repository;
        NowPlaying = nowPlaying;
        Lyrics = lyrics;
        Visualization = visualization;
        _navigation.CurrentPageChanged += () => Dispatcher.UIThread.Post(OnPageChanged);

        NowPlaying.PropertyChanged += OnNowPlayingChanged;
    }

    public NowPlayingViewModel NowPlaying { get; }
    public LyricsViewModel Lyrics { get; }

    /// <summary>Live tuning for the full-window visualizer, and the active preset name for the switcher pill.</summary>
    public VisualizationSettingsViewModel Visualization { get; }

    [ObservableProperty] private PageViewModelBase? _currentPage;

    /// <summary>Which nav-rail entry is highlighted ("Artists", "Albums", "Songs", "Playlist:{id}", …).</summary>
    [ObservableProperty] private string _selectedSection = "Artists";

    // ---- Now Playing mode ---------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLibraryVisible), nameof(VisualizerOpacity))]
    private bool _isNowPlayingOpen;

    /// <summary>Lyrics column shown in Now Playing.</summary>
    [ObservableProperty] private bool _isLyricsOpen;

    /// <summary>Nav rail, transport bar and library dimming are shown outside Now Playing.</summary>
    public bool IsLibraryVisible => !IsNowPlayingOpen;

    /// <summary>The visualizer runs dimmed (60%) behind library screens and at full intensity in Now Playing.</summary>
    public double VisualizerOpacity => IsNowPlayingOpen ? 1.0 : 0.6;

    /// <summary>The visualizer is the background whenever there is something to visualize.</summary>
    public bool IsVisualizerVisible => NowPlaying.HasTrack;

    // ---- Nav rail data ------------------------------------------------------------------------------

    [ObservableProperty] private string _artistCount = string.Empty;
    [ObservableProperty] private string _albumCount = string.Empty;
    [ObservableProperty] private string _trackCount = string.Empty;

    public ObservableCollection<PlaylistItemViewModel> Playlists { get; } = new();

    [ObservableProperty] private string _searchText = string.Empty;

    /// <summary>Performs the initial navigation once the window is shown.</summary>
    public void Start()
    {
        Navigate("Artists");
        _ = RefreshLibraryAsync();
    }

    /// <summary>Reloads the nav rail counts and playlists (after navigation, scans, playlist edits).</summary>
    public async Task RefreshLibraryAsync()
    {
        var counts = await _repository.GetLibraryCountsAsync();
        ArtistCount = counts.Artists.ToString("N0", CultureInfo.CurrentCulture);
        AlbumCount = counts.Albums.ToString("N0", CultureInfo.CurrentCulture);
        TrackCount = counts.Tracks.ToString("N0", CultureInfo.CurrentCulture);

        var playlists = await _repository.GetPlaylistsAsync();
        Playlists.Clear();
        foreach (var p in playlists)
            Playlists.Add(new PlaylistItemViewModel(p));
    }

    [RelayCommand]
    private void OpenNowPlaying() => IsNowPlayingOpen = true;

    [RelayCommand]
    private void CloseNowPlaying() => IsNowPlayingOpen = false;

    [RelayCommand]
    private void ToggleNowPlaying() => IsNowPlayingOpen = !IsNowPlayingOpen;

    /// <summary>Lyrics live in Now Playing: opening them from the library opens Now Playing too.</summary>
    [RelayCommand]
    private void ToggleLyrics()
    {
        if (!IsNowPlayingOpen) { IsLyricsOpen = true; IsNowPlayingOpen = true; }
        else IsLyricsOpen = !IsLyricsOpen;
    }

    [RelayCommand]
    private void GoBack()
    {
        if (IsNowPlayingOpen) IsNowPlayingOpen = false;
        else _navigation.GoBack();
    }

    /// <summary>Navigates to an artist's page from any clickable artist name. No-op when unknown.</summary>
    [RelayCommand]
    private void GoToArtist(int artistId)
    {
        if (artistId <= 0) return;
        IsNowPlayingOpen = false;
        _navigation.NavigateTo<ArtistDetailViewModel>(vm => vm.ArtistId = artistId);
    }

    /// <summary>Navigates to an album's page from any clickable album name or cover. No-op when unknown.</summary>
    [RelayCommand]
    private void GoToAlbum(int albumId)
    {
        if (albumId <= 0) return;
        IsNowPlayingOpen = false;
        _navigation.NavigateTo<AlbumDetailViewModel>(vm => vm.AlbumId = albumId);
    }

    [RelayCommand]
    private void OpenPlaylist(PlaylistItemViewModel playlist)
    {
        IsNowPlayingOpen = false;
        _navigation.NavigateTo<PlaylistDetailViewModel>(vm => vm.PlaylistId = playlist.Id);
    }

    /// <summary>Runs the rail search: shows Songs filtered to the query (a dedicated results page is not designed yet).</summary>
    [RelayCommand]
    private void Search()
    {
        IsNowPlayingOpen = false;
        var query = SearchText.Trim();
        _navigation.NavigateTo<SongsViewModel>(vm => vm.Filter = query);
    }

    [RelayCommand]
    private void Navigate(string section)
    {
        IsNowPlayingOpen = false;
        switch (section)
        {
            case "Home": _navigation.NavigateTo<HomeViewModel>(); break;
            case "Artists": _navigation.NavigateTo<ArtistsViewModel>(); break;
            case "Albums": _navigation.NavigateTo<AlbumsViewModel>(); break;
            case "Songs": _navigation.NavigateTo<SongsViewModel>(); break;
            case "Playlists": _navigation.NavigateTo<PlaylistsViewModel>(); break;
            case "TagEditor": _navigation.NavigateTo<MetadataEditorViewModel>(); break;
            case "Settings": _navigation.NavigateTo<SettingsViewModel>(); break;
        }
    }

    private void OnPageChanged()
    {
        CurrentPage = _navigation.CurrentPage;
        SelectedSection = CurrentPage switch
        {
            HomeViewModel => "Home",
            ArtistsViewModel or ArtistDetailViewModel => "Artists",
            AlbumsViewModel => "Albums",
            // Albums are reached from either section; keep whichever the user came from.
            AlbumDetailViewModel => SelectedSection == "Albums" ? "Albums" : "Artists",
            SongsViewModel => "Songs",
            PlaylistsViewModel => "Playlists",
            PlaylistDetailViewModel p => $"Playlist:{p.PlaylistId}",
            MetadataEditorViewModel => "TagEditor",
            SettingsViewModel => "Settings",
            _ => SelectedSection,
        };
        _ = RefreshLibraryAsync();
    }

    private void OnNowPlayingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NowPlayingViewModel.HasTrack))
        {
            OnPropertyChanged(nameof(IsVisualizerVisible));
            if (!NowPlaying.HasTrack) IsNowPlayingOpen = false;
        }
    }
}
