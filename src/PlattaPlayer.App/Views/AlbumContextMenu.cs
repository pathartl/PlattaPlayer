using Avalonia.Controls;
using Avalonia.Input;
using PlattaPlayer.App.ViewModels;
using PlattaPlayer.App.ViewModels.Items;

namespace PlattaPlayer.App.Views;

/// <summary>
/// The album context menu shared by album tiles (Albums, Artist, Home) and the album page. Its commands live on
/// the shell view-model, found through the anchor's window since the menu's popup is outside the visual tree.
/// </summary>
internal static class AlbumContextMenu
{
    /// <summary><c>ContextRequested</c> handler for a control whose data context is an album tile.</summary>
    public static void OnTileContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is Control { DataContext: AlbumItemViewModel album } anchor)
            Open(anchor, album.Id, e);
    }

    public static void Open(Control anchor, int albumId, ContextRequestedEventArgs e)
    {
        if (Open(anchor, albumId)) e.Handled = true;
    }

    /// <summary>
    /// Opens the menu for <paramref name="albumId"/>: at the pointer by default, or under the anchor for a
    /// kebab button (<see cref="PlacementMode.BottomEdgeAlignedLeft"/>).
    /// </summary>
    public static bool Open(Control anchor, int albumId, PlacementMode placement = PlacementMode.Pointer)
    {
        if (albumId <= 0 || TopLevel.GetTopLevel(anchor)?.DataContext is not MainWindowViewModel shell) return false;

        var play = new MenuItem { Header = "Play" };
        play.Click += (_, _) => shell.PlayAlbumCommand.Execute(albumId);
        var playNext = new MenuItem { Header = "Play next" };
        playNext.Click += (_, _) => shell.PlayAlbumNextCommand.Execute(albumId);
        var addToQueue = new MenuItem { Header = "Add to queue" };
        addToQueue.Click += (_, _) => shell.AddAlbumToQueueCommand.Execute(albumId);

        var tagEditor = new MenuItem { Header = "Open in Tag Editor" };
        tagEditor.Click += (_, _) => shell.OpenAlbumInTagEditorCommand.Execute(albumId);

        var menu = new ContextMenu { Placement = placement };
        menu.Items.Add(play);
        menu.Items.Add(playNext);
        menu.Items.Add(addToQueue);
        menu.Items.Add(new Separator());
        menu.Items.Add(tagEditor);
        menu.Open(anchor);
        return true;
    }
}
