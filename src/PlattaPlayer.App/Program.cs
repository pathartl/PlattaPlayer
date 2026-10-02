using System;
using Avalonia;
using Microsoft.Extensions.DependencyInjection;

namespace PlattaPlayer.App;

internal static class Program
{
    public static IServiceProvider Services { get; private set; } = default!;

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called.
    [STAThread]
    public static void Main(string[] args)
    {
        Services = AppServices.Build();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure(() => new App(Services))
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
