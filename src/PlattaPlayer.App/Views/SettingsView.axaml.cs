using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using PlattaPlayer.App.ViewModels;

namespace PlattaPlayer.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async void OnAddLocalFolder(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm)
            return;

        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
            return;

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select a music folder",
            AllowMultiple = false
        });

        if (folders.Count == 0)
            return;

        var path = folders[0].TryGetLocalPath();
        if (!string.IsNullOrEmpty(path))
            await vm.AddLocalFolderAsync(path);
    }

    /// <summary>Signs in to Plex in the system browser; the view model waits for the approval.</summary>
    private async void OnPlexBrowserSignIn(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm)
            return;

        var launcher = TopLevel.GetTopLevel(this)?.Launcher;
        if (launcher is null)
            return;

        await vm.ConnectPlexInBrowserAsync(uri => launcher.LaunchUriAsync(uri));
    }

    /// <summary>Opens the folder a codec setting asks the user to fill (its path is the button's Tag).</summary>
    private async void OnOpenCodecFolder(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not string path)
            return;

        var launcher = TopLevel.GetTopLevel(this)?.Launcher;
        if (launcher is null)
            return;

        await launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path));
    }
}
