using System;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using PlattaPlayer.App.Plugins;
using PlattaPlayer.App.Services;
using PlattaPlayer.App.ViewModels;
using PlattaPlayer.Codecs.Abstractions;
using PlattaPlayer.Visualizations.Abstractions;
using PlattaPlayer.Core.Abstractions;
using PlattaPlayer.Data;
using PlattaPlayer.Playback;
using PlattaPlayer.Sources.Local;
using PlattaPlayer.Sources.Jellyfin;

namespace PlattaPlayer.App;

/// <summary>Composition root: builds the application's dependency-injection container.</summary>
internal static class AppServices
{
    public static IServiceProvider Build()
    {
        var services = new ServiceCollection();

        // Plugins (visualizers and codecs) are all drop-ins, the built-ins included. The built-ins are
        // staged into an app-relative plugins\ folder by the build; user drop-ins live in %LOCALAPPDATA%.
        // The built-ins load first so they win where ids (or codec extensions) collide: the settings VM
        // dedupes visualizers by Id, and the codec registry keeps the first codec for an id or extension.
        var builtIn = PluginLoader.LoadFrom(Path.Combine(AppContext.BaseDirectory, "plugins"));
        var dropIns = PluginLoader.LoadFrom(AppPaths.PluginsDirectory);
        var codecs = new CodecRegistry([.. builtIn.Codecs, .. dropIns.Codecs]);
        services.AddSingleton(codecs);

        // Settings are needed before the container exists: each codec plugin reads its own (e.g. the MIDI
        // device) through its host.
        var appSettings = new AppSettingsStore();
        services.AddSingleton<IAppSettings>(appSettings);
        foreach (var codec in codecs.Plugins)
            InitializeCodec(codec, appSettings);

        // Infrastructure
        services.AddLibraryData();
        services.AddPlayback();

        // BASS audio engine. Formats BASS can't decode (MIDI, SPC, …) are played by their codec plugin.
        // The one instance is shared as the playback engine and as the visualization audio tap.
        services.AddSingleton<BassPlaybackEngine>(sp => new BassPlaybackEngine(
            sp.GetRequiredService<IAppSettings>(),
            AppPaths.WaveformsDirectory,
            codecs));
        services.AddSingleton<IPlaybackEngine>(sp => sp.GetRequiredService<BassPlaybackEngine>());
        services.AddSingleton<IAudioTap>(sp => sp.GetRequiredService<BassPlaybackEngine>());
        services.AddSingleton<IWaveformSource>(sp => sp.GetRequiredService<BassPlaybackEngine>());

        // Media sources
        services.AddSingleton<ILyricsProvider, LocalLyricsProvider>();
        services.AddSingleton<IMediaSourceFactory>(_ => new LocalMediaSourceFactory(codecs));
        services.AddSingleton<IMediaSourceFactory, JellyfinMediaSourceFactory>();
        services.AddSingleton<JellyfinAuthenticator>();

        // OS media controls — Windows implementation is added by the platform module when present.
        services.AddSingleton<ISystemMediaControls>(_ => PlatformIntegration.CreateSystemMediaControls());
        services.AddSingleton<SystemMediaControlsBridge>();

        // UI services
        services.AddSingleton<INavigationService, NavigationService>();

        // Every discovered visualizer is offered in Settings; the active one is hosted full-window.
        foreach (var plugin in builtIn.Visualizers)
            services.AddSingleton<IVisualizerPlugin>(plugin);
        foreach (var plugin in dropIns.Visualizers)
            services.AddSingleton<IVisualizerPlugin>(plugin);

        // Shell view-models (singletons reflect global state)
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<NowPlayingViewModel>();
        services.AddSingleton<VisualizationSettingsViewModel>();
        services.AddSingleton<LyricsViewModel>();

        // Page view-models (fresh instance per navigation)
        services.AddTransient<HomeViewModel>();
        services.AddTransient<ArtistsViewModel>();
        services.AddTransient<AlbumsViewModel>();
        services.AddTransient<SongsViewModel>();
        services.AddTransient<PlaylistsViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<MetadataEditorViewModel>();
        services.AddTransient<ArtistDetailViewModel>();
        services.AddTransient<AlbumDetailViewModel>();
        services.AddTransient<PlaylistDetailViewModel>();

        return services.BuildServiceProvider();
    }

    private static void InitializeCodec(ICodecPlugin codec, IAppSettings settings)
    {
        try
        {
            codec.Initialize(new CodecHost(codec.Id, settings));
        }
        catch (Exception ex)
        {
            // A plugin that fails to initialize still gets its calls; it just runs without the host.
            PluginLoader.Log($"{codec.GetType().FullName} (Initialize)", ex);
        }
    }
}
