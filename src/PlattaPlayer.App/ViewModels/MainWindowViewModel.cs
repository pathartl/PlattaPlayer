using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
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
    private readonly IPlaybackService _playback;
    private readonly IAppSettings _settings;

    public MainWindowViewModel(
        INavigationService navigation,
        ILibraryRepository repository,
        IPlaybackService playback,
        IAppSettings settings,
        NowPlayingViewModel nowPlaying,
        LyricsViewModel lyrics,
        QueueViewModel queue,
        VisualizationSettingsViewModel visualization,
        NavRailLayoutViewModel rail)
    {
        _navigation = navigation;
        _repository = repository;
        _playback = playback;
        _settings = settings;
        NowPlaying = nowPlaying;
        Lyrics = lyrics;
        Queue = queue;
        Visualization = visualization;
        Rail = rail;
        _navigation.CurrentPageChanged += () => Dispatcher.UIThread.Post(OnPageChanged);

        NowPlaying.PropertyChanged += OnNowPlayingChanged;
    }

    public NowPlayingViewModel NowPlaying { get; }
    public LyricsViewModel Lyrics { get; }
    public QueueViewModel Queue { get; }

    /// <summary>Live tuning for the full-window visualizer, and the active preset name for the switcher pill.</summary>
    public VisualizationSettingsViewModel Visualization { get; }

    [ObservableProperty] private PageViewModelBase? _currentPage;

    /// <summary>Which nav-rail entry is highlighted ("Artists", "Albums", "Songs", "Playlist:{id}", …).</summary>
    [ObservableProperty] private string _selectedSection = "Artists";

    // ---- Now Playing mode ---------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLibraryVisible), nameof(VisualizerOpacity), nameof(IsQueueDocked))]
    private bool _isNowPlayingOpen;

    /// <summary>Lyrics column shown in Now Playing. Shares the right side with the queue, so one closes the other.</summary>
    [ObservableProperty] private bool _isLyricsOpen;

    /// <summary>
    /// Queue panel: docked to the right of the library page, or floating where the lyrics column would be in
    /// Now Playing.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsQueueDocked))]
    private bool _isQueueOpen;

    /// <summary>The queue is open over the library (full-height glass panel, rather than the Now Playing card).</summary>
    public bool IsQueueDocked => IsQueueOpen && !IsNowPlayingOpen;

    partial void OnIsLyricsOpenChanged(bool value)
    {
        if (value) IsQueueOpen = false;
    }

    partial void OnIsQueueOpenChanged(bool value)
    {
        if (value) IsLyricsOpen = false;
    }

    /// <summary>Nav rail, transport bar and library dimming are shown outside Now Playing.</summary>
    public bool IsLibraryVisible => !IsNowPlayingOpen;

    /// <summary>The visualizer runs dimmed (60%) behind library screens and at full intensity in Now Playing.</summary>
    public double VisualizerOpacity => IsNowPlayingOpen ? 1.0 : 0.6;

    /// <summary>The visualizer is the background whenever there is something to visualize.</summary>
    public bool IsVisualizerVisible => NowPlaying.HasTrack;

    // ---- Nav rail data ------------------------------------------------------------------------------

    /// <summary>Which rail entries are shown and in what order (edited in Settings).</summary>
    public NavRailLayoutViewModel Rail { get; }

    public ObservableCollection<PlaylistItemViewModel> Playlists { get; } = new();

    [ObservableProperty] private string _searchText = string.Empty;

    // ---- Source filter (rail dropdown) ---------------------------------------------------------------

    /// <summary>One checkbox per configured source; unchecked sources are left out of every library view.</summary>
    public ObservableCollection<SourceFilterItemViewModel> SourceFilters { get; } = new();

    /// <summary>The dropdown's label: "All sources", the one source shown, or "2 of 3 sources".</summary>
    [ObservableProperty] private string _sourceFilterSummary = "All sources";

    /// <summary>The filter only appears once there is a source to filter.</summary>
    public bool HasSources => SourceFilters.Count > 0;

    /// <summary>Rebuilds the checkboxes when sources were added, removed or renamed (left alone otherwise, so an
    /// open dropdown isn't rebuilt under the pointer).</summary>
    private async Task RefreshSourceFiltersAsync()
    {
        var sources = (await _repository.GetSourcesAsync())
            .OrderBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (sources.Select(s => (s.Id, s.DisplayName))
            .SequenceEqual(SourceFilters.Select(f => (f.Config.Id, f.DisplayName))))
            return;

        var hidden = _settings.HiddenSourceIds.ToHashSet();
        SourceFilters.Clear();
        foreach (var s in sources)
            SourceFilters.Add(new SourceFilterItemViewModel(s, !hidden.Contains(s.Id), OnSourceFilterChanged));
        OnPropertyChanged(nameof(HasSources));
        UpdateSourceFilterSummary();
    }

    private void UpdateSourceFilterSummary()
    {
        var shown = SourceFilters.Where(f => f.IsShown).ToList();
        SourceFilterSummary = shown.Count switch
        {
            _ when shown.Count == SourceFilters.Count => "All sources",
            0 => "No sources",
            1 => shown[0].DisplayName,
            _ => $"{shown.Count} of {SourceFilters.Count} sources",
        };
    }

    private void OnSourceFilterChanged()
    {
        if (_batchingSourceFilters) return;
        _settings.HiddenSourceIds = SourceFilters.Where(f => !f.IsShown).Select(f => f.Config.Id).ToList();
        _settings.Save();
        UpdateSourceFilterSummary();

        // Reload what's on screen; the editor and settings pages don't show library content.
        if (CurrentPage is not (null or MetadataEditorViewModel or SettingsViewModel))
            _ = CurrentPage.InitializeAsync();
        _ = RefreshLibraryAsync();
    }

    private bool _batchingSourceFilters;

    [RelayCommand]
    private void ShowAllSources()
    {
        if (SourceFilters.All(f => f.IsShown)) return;
        _batchingSourceFilters = true;
        try { foreach (var f in SourceFilters) f.IsShown = true; }
        finally { _batchingSourceFilters = false; }
        OnSourceFilterChanged();
    }

    /// <summary>Performs the initial navigation once the window is shown.</summary>
    public void Start()
    {
        Navigate(Rail.StartSection);
        _ = RefreshLibraryAsync();
    }

    /// <summary>Reloads the nav rail counts and playlists (after navigation, scans, playlist edits).</summary>
    public async Task RefreshLibraryAsync()
    {
        var counts = await _repository.GetLibraryCountsAsync();
        Rail.SetCount("Artists", counts.Artists.ToString("N0", CultureInfo.CurrentCulture));
        Rail.SetCount("Albums", counts.Albums.ToString("N0", CultureInfo.CurrentCulture));
        Rail.SetCount("Songs", counts.Tracks.ToString("N0", CultureInfo.CurrentCulture));
        Rail.SetCount("Genres", (await _repository.GetGenresAsync()).Count.ToString("N0", CultureInfo.CurrentCulture));

        var playlists = await _repository.GetPlaylistsAsync();
        Playlists.Clear();
        foreach (var p in playlists)
            Playlists.Add(new PlaylistItemViewModel(p));

        await RefreshSourceFiltersAsync();
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
    private void ToggleQueue() => IsQueueOpen = !IsQueueOpen;

    [RelayCommand]
    private void CloseQueue() => IsQueueOpen = false;

    [RelayCommand]
    private void GoBack()
    {
        if (IsQueueOpen) IsQueueOpen = false;
        else if (IsNowPlayingOpen) IsNowPlayingOpen = false;
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

    /// <summary>Opens the Tag Editor with an album's files (those of the formats it handles; others are skipped).</summary>
    [RelayCommand]
    private async Task OpenAlbumInTagEditor(int albumId)
    {
        if (albumId <= 0) return;
        var paths = (await _repository.GetAlbumTracksAsync(albumId))
            .Select(t => t.LocalPath)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        IsNowPlayingOpen = false;
        _navigation.NavigateTo<MetadataEditorViewModel>(vm =>
        {
            vm.AddFiles(paths);
            if (vm.Files.Count == 0)
                vm.Status = $"This album has no {vm.FormatsText} files to edit.";
        });
    }

    // ---- Album queueing (album context menu) ---------------------------------------------------------

    [RelayCommand]
    private async Task PlayAlbum(int albumId)
    {
        var tracks = await _repository.GetAlbumTracksAsync(albumId);
        if (tracks.Count > 0) await _playback.PlayQueueAsync(tracks);
    }

    [RelayCommand]
    private async Task PlayAlbumNext(int albumId) => Queue.PlayNext(await _repository.GetAlbumTracksAsync(albumId));

    [RelayCommand]
    private async Task AddAlbumToQueue(int albumId) => Queue.AddToQueue(await _repository.GetAlbumTracksAsync(albumId));

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
            case "Genres": _navigation.NavigateTo<GenresViewModel>(); break;
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
            // Albums are reached from several sections; keep whichever the user came from.
            AlbumDetailViewModel => SelectedSection is "Albums" or "Home" ? SelectedSection : "Artists",
            SongsViewModel => "Songs",
            GenresViewModel or GenreDetailViewModel => "Genres",
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
