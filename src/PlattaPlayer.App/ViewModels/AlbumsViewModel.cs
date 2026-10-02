using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using PlattaPlayer.App.Services;
using PlattaPlayer.App.ViewModels.Items;
using PlattaPlayer.Core.Abstractions;

namespace PlattaPlayer.App.ViewModels;

public sealed partial class AlbumsViewModel : PageViewModelBase
{
    private readonly ILibraryRepository _repository;
    private readonly ICoverArtCache _covers;
    private readonly INavigationService _navigation;

    public AlbumsViewModel(ILibraryRepository repository, ICoverArtCache covers, INavigationService navigation)
    {
        _repository = repository;
        _covers = covers;
        _navigation = navigation;
    }

    public override string Title => "Albums";

    public ObservableCollection<AlbumItemViewModel> Albums { get; } = new();

    public bool IsEmpty => Albums.Count == 0 && !IsBusy;

    public override async Task InitializeAsync()
    {
        IsBusy = true;
        try
        {
            Albums.Clear();
            foreach (var album in await _repository.GetAlbumsAsync())
                Albums.Add(new AlbumItemViewModel(album, _covers));
        }
        finally { IsBusy = false; OnPropertyChanged(nameof(IsEmpty)); }
    }

    [RelayCommand]
    private void OpenAlbum(AlbumItemViewModel item)
        => _navigation.NavigateTo<AlbumDetailViewModel>(vm => vm.AlbumId = item.Id);
}
