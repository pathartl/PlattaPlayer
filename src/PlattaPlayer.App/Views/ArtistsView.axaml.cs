using System;
using Avalonia.Controls;
using PlattaPlayer.App.ViewModels;

namespace PlattaPlayer.App.Views;

public partial class ArtistsView : UserControl
{
    private ArtistsViewModel? _vm;

    public ArtistsView() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.JumpRequested -= OnJump;
        _vm = DataContext as ArtistsViewModel;
        if (_vm is not null) _vm.JumpRequested += OnJump;
    }

    private void OnJump(int index)
    {
        if (CardGrid.IsVisible) CardGrid.ScrollToItem(index);
        else RowList.ScrollToItem(index);
    }
}
