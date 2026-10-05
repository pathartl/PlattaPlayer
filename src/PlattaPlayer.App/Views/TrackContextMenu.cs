using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using PlattaPlayer.App.ViewModels;
using PlattaPlayer.App.ViewModels.Items;

namespace PlattaPlayer.App.Views;

/// <summary>
/// The track row context menu shared by track lists (Songs, Album, Playlist, Home): Play next / Add to queue,
/// plus "Add to playlist" where the page supplies playlists. Queue commands live on the shell view-model,
/// found through the anchor's window since the menu's popup is outside the visual tree.
/// </summary>
internal static class TrackContextMenu
{
    public static bool Open(
        Control anchor,
        TrackItemViewModel track,
        IReadOnlyList<PlaylistItemViewModel>? playlists = null,
        Func<TrackItemViewModel, int, Task>? addToPlaylist = null)
    {
        if (TopLevel.GetTopLevel(anchor)?.DataContext is not MainWindowViewModel shell) return false;

        var menu = new ContextMenu();

        var playNext = new MenuItem { Header = "Play next" };
        playNext.Click += (_, _) => shell.Queue.PlayNext(new[] { track.Track });
        menu.Items.Add(playNext);

        var addToQueue = new MenuItem { Header = "Add to queue" };
        addToQueue.Click += (_, _) => shell.Queue.AddToQueue(new[] { track.Track });
        menu.Items.Add(addToQueue);

        if (playlists is not null && addToPlaylist is not null)
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

            menu.Items.Add(new Separator());
            menu.Items.Add(addTo);
        }

        menu.Open(anchor);
        return true;
    }
}
