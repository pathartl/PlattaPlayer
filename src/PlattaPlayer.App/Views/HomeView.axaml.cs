using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using PlattaPlayer.App.ViewModels.Items;

namespace PlattaPlayer.App.Views;

public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
        // The horizontal carousels otherwise convert a vertical wheel into sideways scrolling and
        // swallow it, so the page never scrolls past the first row. Intercept the wheel on the way
        // down and drive the page scroller ourselves; Shift still scrolls a row sideways.
        AddHandler(PointerWheelChangedEvent, OnPageWheel, RoutingStrategies.Tunnel);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnAlbumContextRequested(object? sender, ContextRequestedEventArgs e)
        => AlbumContextMenu.OnTileContextRequested(sender, e);

    private void OnTrackContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is Control { DataContext: TrackItemViewModel item } anchor && TrackContextMenu.Open(anchor, item))
            e.Handled = true;
    }

    private void OnPageWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) || e.Delta.Y == 0) return;
        if (this.FindControl<ScrollViewer>("PageScroller") is not { } scroller) return;

        var max = Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height);
        var y = Math.Clamp(scroller.Offset.Y - e.Delta.Y * 60, 0, max);
        scroller.Offset = scroller.Offset.WithY(y);
        e.Handled = true;
    }
}
