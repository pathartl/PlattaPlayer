using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using PlattaPlayer.App.Services;
using PlattaPlayer.App.ViewModels;
using PlattaPlayer.App.Views;
using PlattaPlayer.Core.Abstractions;
using PlattaPlayer.Data;

namespace PlattaPlayer.App;

public partial class App : Application
{
    private readonly IServiceProvider _services;

    public App(IServiceProvider services) => _services = services;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Ensure the database exists / is migrated before the UI queries it.
            _services.MigrateLibraryAsync().GetAwaiter().GetResult();

            AccentPalette.Apply(AccentPalette.Resolve(_services.GetRequiredService<IAppSettings>().AccentColor));

            var mainViewModel = _services.GetRequiredService<MainWindowViewModel>();
            var mainWindow = new MainWindow { DataContext = mainViewModel };
            desktop.MainWindow = mainWindow;

            // Bind OS media controls once the native window handle exists (after the window opens).
            mainWindow.Opened += (_, _) =>
            {
                if (mainWindow.TryGetPlatformHandle()?.Handle is { } hwnd)
                    _services.GetRequiredService<SystemMediaControlsBridge>().Attach(hwnd);
            };

            mainViewModel.Start();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
