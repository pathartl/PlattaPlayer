using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace PlattaPlayer.App.Views;

public partial class GenresView : UserControl
{
    public GenresView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
