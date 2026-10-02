using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlattaPlayer.App.Services;
using PlattaPlayer.App.ViewModels.Items;
using PlattaPlayer.Core.Abstractions;

namespace PlattaPlayer.App.ViewModels;

public sealed partial class PlaylistsViewModel : PageViewModelBase
{
    private readonly ILibraryRepository _repository;
    private readonly INavigationService _navigation;

    public PlaylistsViewModel(ILibraryRepository repository, INavigationService navigation)
    {
        _repository = repository;
        _navigation = navigation;
    }

    public override string Title => "Playlists";

    public ObservableCollection<PlaylistItemViewModel> Playlists { get; } = new();

    [ObservableProperty] private string _newPlaylistName = string.Empty;

    public bool IsEmpty => Playlists.Count == 0 && !IsBusy;

    public override async Task InitializeAsync()
    {
        IsBusy = true;
        try
        {
            Playlists.Clear();
            foreach (var p in await _repository.GetPlaylistsAsync())
                Playlists.Add(new PlaylistItemViewModel(p));
        }
        finally { IsBusy = false; OnPropertyChanged(nameof(IsEmpty)); }
    }

    [RelayCommand]
    private async Task CreatePlaylist()
    {
        var name = string.IsNullOrWhiteSpace(NewPlaylistName) ? "New Playlist" : NewPlaylistName.Trim();
        var created = await _repository.CreatePlaylistAsync(name);
        Playlists.Add(new PlaylistItemViewModel(created));
        NewPlaylistName = string.Empty;
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private void OpenPlaylist(PlaylistItemViewModel item)
        => _navigation.NavigateTo<PlaylistDetailViewModel>(vm => vm.PlaylistId = item.Id);

    [RelayCommand]
    private async Task DeletePlaylist(PlaylistItemViewModel item)
    {
        await _repository.DeletePlaylistAsync(item.Id);
        Playlists.Remove(item);
        OnPropertyChanged(nameof(IsEmpty));
    }
}
