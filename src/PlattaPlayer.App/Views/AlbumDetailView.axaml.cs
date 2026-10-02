using System;
using Avalonia.Controls;
using Avalonia.Input;
using PlattaPlayer.App.ViewModels;
using PlattaPlayer.App.ViewModels.Items;

namespace PlattaPlayer.App.Views;

public partial class AlbumDetailView : UserControl
{
    public AlbumDetailView()
    {
        InitializeComponent();
        // The art column is 400px at full size and narrows on smaller windows so the track list keeps room.
        SizeChanged += (_, e) =>
        {
            var side = Math.Clamp(Math.Floor(e.NewSize.Width * 0.34), 240, 400);
            ArtSlot.Width = ArtSlot.Height = side;
        };
    }

    private void Play(TrackItemViewModel item)
    {
        if (DataContext is AlbumDetailViewModel vm && vm.PlayCommand.CanExecute(item))
            vm.PlayCommand.Execute(item);
    }

    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is TrackItemViewModel item) Play(item);
    }

    private void OnTrackListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None && TrackList.SelectedItem is TrackItemViewModel item)
        {
            Play(item);
            e.Handled = true;
        }
    }

    private void OnRowContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not TrackItemViewModel item ||
            DataContext is not AlbumDetailViewModel vm)
            return;

        PlaylistContextMenu.Build(item, vm.Playlists, (track, playlistId) => vm.AddToPlaylistAsync(track, playlistId))
            .Open((Control)sender);
        e.Handled = true;
    }
}
