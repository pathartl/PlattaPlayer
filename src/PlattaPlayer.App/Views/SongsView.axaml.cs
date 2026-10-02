using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using PlattaPlayer.App.ViewModels;
using PlattaPlayer.App.ViewModels.Items;

namespace PlattaPlayer.App.Views;

public partial class SongsView : UserControl
{
    public SongsView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is TrackItemViewModel item &&
            DataContext is SongsViewModel vm &&
            vm.PlayCommand.CanExecute(item))
            vm.PlayCommand.Execute(item);
    }

    private void OnRowContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not TrackItemViewModel item ||
            DataContext is not SongsViewModel vm)
            return;

        PlaylistContextMenu.Build(item, vm.Playlists, (track, playlistId) => vm.AddToPlaylistAsync(track, playlistId))
            .Open((Control)sender);
        e.Handled = true;
    }
}
