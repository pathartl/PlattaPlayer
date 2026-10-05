using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

namespace PlattaPlayer.App.Views;

public partial class GenreDetailView : UserControl
{
    public GenreDetailView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnAlbumContextRequested(object? sender, ContextRequestedEventArgs e)
        => AlbumContextMenu.OnTileContextRequested(sender, e);
}
