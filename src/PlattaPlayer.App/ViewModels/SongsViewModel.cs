using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using PlattaPlayer.App.ViewModels.Items;
using PlattaPlayer.Core.Abstractions;

namespace PlattaPlayer.App.ViewModels;

public sealed partial class SongsViewModel : PageViewModelBase
{
    private readonly ILibraryRepository _repository;
    private readonly ICoverArtCache _covers;
    private readonly IPlaybackService _playback;

    public SongsViewModel(ILibraryRepository repository, ICoverArtCache covers, IPlaybackService playback)
    {
        _repository = repository;
        _covers = covers;
        _playback = playback;
    }

    public override string Title => "Songs";

    /// <summary>Optional search query from the nav rail; matches title, artist or album.</summary>
    public string Filter { get; set; } = string.Empty;

    public bool IsFiltered => !string.IsNullOrWhiteSpace(Filter);

    public string Heading => IsFiltered ? $"Results for “{Filter}”" : "Songs";

    public ObservableCollection<TrackItemViewModel> Tracks { get; } = new();

    /// <summary>Playlists available as "Add to playlist" targets in the row context menu.</summary>
    public ObservableCollection<PlaylistItemViewModel> Playlists { get; } = new();

    public bool IsEmpty => Tracks.Count == 0 && !IsBusy;

    public override async Task InitializeAsync()
    {
        IsBusy = true;
        try
        {
            Tracks.Clear();
            foreach (var t in await _repository.GetAllTracksAsync())
            {
                var item = new TrackItemViewModel(t, _covers);
                if (!IsFiltered || Matches(item, Filter))
                    Tracks.Add(item);
            }

            Playlists.Clear();
            foreach (var p in await _repository.GetPlaylistsAsync())
                Playlists.Add(new PlaylistItemViewModel(p));
        }
        finally { IsBusy = false; OnPropertyChanged(nameof(IsEmpty)); }
    }

    private static bool Matches(TrackItemViewModel t, string query)
        => t.Title.Contains(query, System.StringComparison.CurrentCultureIgnoreCase)
           || t.Artist.Contains(query, System.StringComparison.CurrentCultureIgnoreCase)
           || t.AlbumTitle.Contains(query, System.StringComparison.CurrentCultureIgnoreCase);

    [RelayCommand]
    private Task Play(TrackItemViewModel item)
    {
        var queue = Tracks.Select(t => t.Track).ToList();
        var index = Tracks.IndexOf(item);
        return _playback.PlayQueueAsync(queue, index);
    }

    public Task AddToPlaylistAsync(TrackItemViewModel track, int playlistId)
        => _repository.AddTracksToPlaylistAsync(playlistId, new[] { track.Track.Id });
}
