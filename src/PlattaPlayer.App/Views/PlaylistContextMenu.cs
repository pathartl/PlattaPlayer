using System;
using System.Collections.Generic;
using Avalonia.Controls;
using PlattaPlayer.App.ViewModels.Items;

namespace PlattaPlayer.App.Views;

/// <summary>
/// Builds the "Add to playlist" row context menu shared by track lists (Songs, Album detail).
/// The chosen track is captured in the click handler so a single menu can target any playlist.
/// </summary>
internal static class PlaylistContextMenu
{
    public static ContextMenu Build(
        TrackItemViewModel track,
        IReadOnlyList<PlaylistItemViewModel> playlists,
        Func<TrackItemViewModel, int, System.Threading.Tasks.Task> addToPlaylist)
    {
        var addTo = new MenuItem { Header = "Add to playlist" };

        if (playlists.Count == 0)
        {
            addTo.Items.Add(new MenuItem { Header = "(no playlists yet)", IsEnabled = false });
        }
        else
        {
            foreach (var playlist in playlists)
            {
                var playlistId = playlist.Id;
                var item = new MenuItem { Header = playlist.Name };
                item.Click += async (_, _) => await addToPlaylist(track, playlistId);
                addTo.Items.Add(item);
            }
        }

        var menu = new ContextMenu();
        menu.Items.Add(addTo);
        return menu;
    }
}
