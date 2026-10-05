using System;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlattaPlayer.App.Services;
using PlattaPlayer.App.ViewModels.Items;
using PlattaPlayer.Codecs.Abstractions;
using PlattaPlayer.Core.Abstractions;
using PlattaPlayer.Core.Models;
using PlattaPlayer.Sources.Jellyfin;

namespace PlattaPlayer.App.ViewModels;

public sealed partial class SettingsViewModel : PageViewModelBase
{
    private readonly ILibraryRepository _repository;
    private readonly IMediaSourceManager _sources;
    private readonly ILibrarySyncService _sync;
    private readonly JellyfinAuthenticator _jellyfinAuth;
    private readonly CodecRegistry _codecs;
    private readonly IAppSettings _appSettings;

    public SettingsViewModel(
        ILibraryRepository repository,
        IMediaSourceManager sources,
        ILibrarySyncService sync,
        JellyfinAuthenticator jellyfinAuth,
        CodecRegistry codecs,
        IAppSettings appSettings,
        VisualizationSettingsViewModel visualization,
        NavRailLayoutViewModel rail)
    {
        Rail = rail;
        _repository = repository;
        _sources = sources;
        _sync = sync;
        _jellyfinAuth = jellyfinAuth;
        _codecs = codecs;
        _appSettings = appSettings;
        Visualization = visualization;
        _showRemoteWaveforms = appSettings.ShowRemoteWaveforms;

        foreach (var (name, color) in AccentPalette.Presets)
            AccentSwatches.Add(new AccentSwatch(name, color));
        _accentHex = AccentPalette.ToHex(AccentPalette.Resolve(appSettings.AccentColor));
        MarkSelectedSwatch();
        _accentSaveTimer.Tick += (_, _) =>
        {
            _accentSaveTimer.Stop();
            _appSettings.Save();
        };
    }

    public override string Title => "Settings";

    /// <summary>Shared visualizer settings (the active-visualizer picker binds to this).</summary>
    public VisualizationSettingsViewModel Visualization { get; }

    /// <summary>Shared nav rail layout: which entries show and in what order.</summary>
    public NavRailLayoutViewModel Rail { get; }

    public ObservableCollection<SourceItemViewModel> Sources { get; } = new();

    // --- Codec plugin settings (e.g. the MIDI device) -------------------------------------------

    public ObservableCollection<CodecSettingViewModel> CodecSettings { get; } = new();

    private void LoadCodecSettings()
    {
        CodecSettings.Clear();
        foreach (var plugin in _codecs.Plugins)
        {
            if (plugin is not ICodecSettings configurable) continue;
            IReadOnlyList<CodecSetting> settings;
            try { settings = configurable.Settings; }
            catch { continue; }
            foreach (var setting in settings)
                CodecSettings.Add(new CodecSettingViewModel(plugin, setting, _appSettings));
        }
    }

    /// <summary>Seek-bar waveforms for Jellyfin tracks (each track is fetched twice to draw one). Applies from the next track.</summary>
    [ObservableProperty] private bool _showRemoteWaveforms;

    partial void OnShowRemoteWaveformsChanged(bool value)
    {
        _appSettings.ShowRemoteWaveforms = value;
        _appSettings.Save();
    }

    // --- Accent color ---------------------------------------------------------------------------

    public ObservableCollection<AccentSwatch> AccentSwatches { get; } = new();

    /// <summary>The accent as <c>#RRGGBB</c>; edited directly or set by a swatch. Applied live once it parses.</summary>
    [ObservableProperty] private string _accentHex;

    partial void OnAccentHexChanged(string value)
    {
        if (!AccentPalette.TryParse(value, out var color)) return;
        AccentPalette.Apply(color);
        // The default is stored as null so it tracks any future change to the design's accent.
        _appSettings.AccentColor = color == AccentPalette.DefaultColor ? null : AccentPalette.ToHex(color);
        // Dragging in the color picker changes this many times a second; write the file once it settles.
        _accentSaveTimer.Stop();
        _accentSaveTimer.Start();
        MarkSelectedSwatch();
        OnPropertyChanged(nameof(AccentColorValue));
    }

    private readonly DispatcherTimer _accentSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };

    /// <summary>The accent as a color, for the custom color picker (two-way with <see cref="AccentHex"/>).</summary>
    public Color AccentColorValue
    {
        get => AccentPalette.Resolve(AccentHex);
        set => AccentHex = AccentPalette.ToHex(value);
    }

    /// <summary>True when the accent isn't one of the presets (the custom swatch shows as selected).</summary>
    public bool IsCustomAccent => !AccentSwatches.Any(s => s.IsSelected);

    [RelayCommand]
    private void SelectAccent(AccentSwatch swatch) => AccentHex = AccentPalette.ToHex(swatch.Color);

    [RelayCommand]
    private void ResetAccent() => AccentHex = AccentPalette.ToHex(AccentPalette.DefaultColor);

    private void MarkSelectedSwatch()
    {
        AccentPalette.TryParse(AccentHex, out var current);
        foreach (var s in AccentSwatches)
            s.IsSelected = s.Color == current;
        OnPropertyChanged(nameof(IsCustomAccent));
    }

    [ObservableProperty] private bool _isSyncing;
    [ObservableProperty] private string _syncStatus = string.Empty;

    // Jellyfin login form.
    [ObservableProperty] private string _jellyfinServerUrl = string.Empty;
    [ObservableProperty] private string _jellyfinUsername = string.Empty;
    [ObservableProperty] private string _jellyfinPassword = string.Empty;
    [ObservableProperty] private bool _isConnecting;
    [ObservableProperty] private string _jellyfinStatus = string.Empty;

    /// <summary>True while the Jellyfin login form is open (picked from the "Add source" menu).</summary>
    [ObservableProperty] private bool _isAddingJellyfin;

    [RelayCommand]
    private void BeginAddJellyfin()
    {
        JellyfinStatus = string.Empty;
        IsAddingJellyfin = true;
    }

    [RelayCommand]
    private void CancelAddJellyfin()
    {
        if (IsConnecting) return;
        IsAddingJellyfin = false;
        JellyfinPassword = string.Empty;
        JellyfinStatus = string.Empty;
    }

    public override async Task InitializeAsync()
    {
        Sources.Clear();
        foreach (var s in await _repository.GetSourcesAsync())
            Sources.Add(new SourceItemViewModel(s));

        LoadCodecSettings();
    }

    /// <summary>Registers a local music folder as a source (invoked by the view after folder selection) and scans it.</summary>
    public async Task AddLocalFolderAsync(string folder)
    {
        var config = new SourceConfig
        {
            Type = SourceType.Local,
            DisplayName = folder,
            SettingsJson = JsonSerializer.Serialize(new LocalSourceSettings { Folders = { folder } })
        };
        await _repository.UpsertSourceAsync(config);
        var item = new SourceItemViewModel(config);
        Sources.Add(item);
        await RunSyncAsync(item);
    }

    [RelayCommand]
    private async Task ConnectJellyfin()
    {
        if (IsConnecting) return;
        if (string.IsNullOrWhiteSpace(JellyfinServerUrl) || string.IsNullOrWhiteSpace(JellyfinUsername))
        {
            JellyfinStatus = "Enter a server URL and username.";
            return;
        }

        IsConnecting = true;
        JellyfinStatus = "Connecting…";
        try
        {
            var settings = await _jellyfinAuth.AuthenticateAsync(JellyfinServerUrl, JellyfinUsername, JellyfinPassword);

            var config = new SourceConfig
            {
                Type = SourceType.Jellyfin,
                DisplayName = $"Jellyfin · {settings.Username}",
                SettingsJson = JsonSerializer.Serialize(settings)
            };
            await _repository.UpsertSourceAsync(config);
            _sources.Invalidate(config.Id);
            var item = new SourceItemViewModel(config);
            Sources.Add(item);

            // The source now shows in the list (with its own scan progress), so close the form.
            JellyfinPassword = string.Empty;
            JellyfinStatus = string.Empty;
            IsAddingJellyfin = false;
            await RunSyncAsync(item);
        }
        catch (Exception ex)
        {
            JellyfinStatus = $"Connection failed: {ex.Message}";
        }
        finally
        {
            IsConnecting = false;
        }
    }

    [RelayCommand]
    private async Task RemoveSource(SourceItemViewModel source)
    {
        await _repository.DeleteSourceAsync(source.Config.Id);
        _sources.Invalidate(source.Config.Id);
        Sources.Remove(source);
    }

    [RelayCommand]
    private async Task RescanAll()
    {
        foreach (var source in Sources.ToList())
            await RunSyncAsync(source);
    }

    /// <summary>Reindexes a single source (invoked by the per-source reindex button).</summary>
    [RelayCommand]
    private Task RescanSource(SourceItemViewModel source) => RunSyncAsync(source);

    private async Task RunSyncAsync(SourceItemViewModel item)
    {
        if (IsSyncing) return;
        IsSyncing = true;
        item.IsSyncing = true;
        var source = item.Config;
        try
        {
            var progress = new Progress<SyncProgress>(p =>
                SyncStatus = p.Total is { } total
                    ? $"Scanned {p.Processed} tracks from {p.SourceName}"
                    : $"Scanning {p.SourceName}… {p.Processed} tracks");

            // Off the UI thread: tag reading and cover extraction are largely synchronous. Progress
            // was created here, so its reports still land on the UI thread.
            await Task.Run(() => _sync.SyncSourceAsync(source, progress));
            SyncStatus = $"Finished scanning {source.DisplayName}";
        }
        catch (Exception ex)
        {
            SyncStatus = $"Scan failed: {ex.Message}";
        }
        finally
        {
            item.IsSyncing = false;
            IsSyncing = false;
        }
    }
}
