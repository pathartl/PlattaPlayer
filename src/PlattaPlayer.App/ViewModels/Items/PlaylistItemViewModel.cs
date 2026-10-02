using PlattaPlayer.Core.Models;

namespace PlattaPlayer.App.ViewModels.Items;

/// <summary>Display wrapper around a <see cref="Playlist"/>.</summary>
public sealed class PlaylistItemViewModel
{
    public PlaylistItemViewModel(Playlist playlist) => Playlist = playlist;

    public Playlist Playlist { get; }
    public int Id => Playlist.Id;
    public string Name => Playlist.Name;

    /// <summary>Nav-rail section key, matched against <c>MainWindowViewModel.SelectedSection</c>.</summary>
    public string SectionKey => $"Playlist:{Id}";
}
