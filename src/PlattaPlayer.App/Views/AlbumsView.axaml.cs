using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace PlattaPlayer.App.Views;

public partial class AlbumsView : UserControl
{
    public AlbumsView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
