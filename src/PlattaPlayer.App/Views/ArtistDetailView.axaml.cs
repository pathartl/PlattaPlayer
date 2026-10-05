using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

namespace PlattaPlayer.App.Views;

public partial class ArtistDetailView : UserControl
{
    public ArtistDetailView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnAlbumContextRequested(object? sender, ContextRequestedEventArgs e)
        => AlbumContextMenu.OnTileContextRequested(sender, e);
}
