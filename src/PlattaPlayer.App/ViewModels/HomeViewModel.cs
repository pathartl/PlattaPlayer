using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using PlattaPlayer.App.ViewModels.Items;
using PlattaPlayer.Core.Abstractions;

namespace PlattaPlayer.App.ViewModels;

public sealed partial class HomeViewModel : PageViewModelBase
{
    private readonly ILibraryRepository _repository;
    private readonly ICoverArtCache _covers;
    private readonly IPlaybackService _playback;

    public HomeViewModel(ILibraryRepository repository, ICoverArtCache covers, IPlaybackService playback)
    {
        _repository = repository;
        _covers = covers;
        _playback = playback;
    }

    public override string Title => "Home";

    public ObservableCollection<AlbumItemViewModel> RecentlyAdded { get; } = new();
    public ObservableCollection<TrackItemViewModel> RecentlyPlayed { get; } = new();
    public ObservableCollection<TrackItemViewModel> MostPlayed { get; } = new();

    public bool HasContent => RecentlyAdded.Count > 0 || RecentlyPlayed.Count > 0 || MostPlayed.Count > 0;

    public override async Task InitializeAsync()
    {
        IsBusy = true;
        try
        {
            FillAlbums(RecentlyAdded, await _repository.GetRecentlyAddedAlbumsAsync(20));
            await Fill(RecentlyPlayed, await _repository.GetRecentlyPlayedAsync(20));
            await Fill(MostPlayed, await _repository.GetMostPlayedAsync(20));
            OnPropertyChanged(nameof(HasContent));
        }
        finally { IsBusy = false; }
    }

    private Task Fill(ObservableCollection<TrackItemViewModel> target, System.Collections.Generic.IReadOnlyList<Core.Models.Track> tracks)
    {
        target.Clear();
        foreach (var t in tracks)
            target.Add(new TrackItemViewModel(t, _covers));
        return Task.CompletedTask;
    }

    private void FillAlbums(ObservableCollection<AlbumItemViewModel> target, System.Collections.Generic.IReadOnlyList<Core.Models.Album> albums)
    {
        target.Clear();
        foreach (var a in albums)
            target.Add(new AlbumItemViewModel(a, _covers));
    }

    [RelayCommand]
    private Task Play(TrackItemViewModel item) => _playback.PlayTrackAsync(item.Track);
}
