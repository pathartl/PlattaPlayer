using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlattaPlayer.App.ViewModels.Items;
using PlattaPlayer.Core.Abstractions;

namespace PlattaPlayer.App.ViewModels;

public sealed partial class PlaylistDetailViewModel : PageViewModelBase
{
    private readonly ILibraryRepository _repository;
    private readonly ICoverArtCache _covers;
    private readonly IPlaybackService _playback;

    public PlaylistDetailViewModel(ILibraryRepository repository, ICoverArtCache covers, IPlaybackService playback)
    {
        _repository = repository;
        _covers = covers;
        _playback = playback;
    }

    public int PlaylistId { get; set; }

    public override string Title => PlaylistName;

    [ObservableProperty] private string _playlistName = "Playlist";

    public ObservableCollection<TrackItemViewModel> Tracks { get; } = new();

    public override async Task InitializeAsync()
    {
        IsBusy = true;
        try
        {
            var playlist = await _repository.GetPlaylistAsync(PlaylistId);
            if (playlist is not null)
            {
                PlaylistName = playlist.Name;
                OnPropertyChanged(nameof(Title));
            }

            Tracks.Clear();
            foreach (var t in await _repository.GetPlaylistTracksAsync(PlaylistId))
                Tracks.Add(new TrackItemViewModel(t, _covers));
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private Task PlayAll()
        => _playback.PlayQueueAsync(Tracks.Select(t => t.Track).ToList(), 0);

    [RelayCommand]
    private Task Play(TrackItemViewModel item)
        => _playback.PlayQueueAsync(Tracks.Select(t => t.Track).ToList(), Tracks.IndexOf(item));

    [RelayCommand]
    private async Task Remove(TrackItemViewModel item)
    {
        var index = Tracks.IndexOf(item);
        if (index < 0) return;
        await _repository.RemovePlaylistEntryAsync(PlaylistId, index);
        Tracks.RemoveAt(index);
    }

    [RelayCommand]
    private async Task MoveUp(TrackItemViewModel item)
    {
        var index = Tracks.IndexOf(item);
        if (index <= 0) return;
        await _repository.MovePlaylistEntryAsync(PlaylistId, index, index - 1);
        Tracks.Move(index, index - 1);
    }

    [RelayCommand]
    private async Task MoveDown(TrackItemViewModel item)
    {
        var index = Tracks.IndexOf(item);
        if (index < 0 || index >= Tracks.Count - 1) return;
        await _repository.MovePlaylistEntryAsync(PlaylistId, index, index + 1);
        Tracks.Move(index, index + 1);
    }
}
