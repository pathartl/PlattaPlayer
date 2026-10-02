using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using PlattaPlayer.App.ViewModels;
using PlattaPlayer.App.ViewModels.Items;

namespace PlattaPlayer.App.Views;

public partial class PlaylistDetailView : UserControl
{
    public PlaylistDetailView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is TrackItemViewModel item &&
            DataContext is PlaylistDetailViewModel vm &&
            vm.PlayCommand.CanExecute(item))
            vm.PlayCommand.Execute(item);
    }
}
