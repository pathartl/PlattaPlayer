using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlattaPlayer.App.Formatting;
using PlattaPlayer.Core.Abstractions;
using PlattaPlayer.Core.Models;

namespace PlattaPlayer.App.ViewModels;

/// <summary>One stage of the signal-path popover (source → decoder → DSP → resampler → output).</summary>
public sealed record SignalStage(string Name, string Detail);

/// <summary>
/// Live playback state shared by the transport bar and Now Playing; mirrors <see cref="IPlaybackService"/> onto
/// the UI thread and derives the audio-quality badges.
/// </summary>
public sealed partial class NowPlayingViewModel : ObservableObject
{
    private readonly IPlaybackService _playback;
    private readonly ICoverArtCache _covers;
    private readonly IWaveformSource _waveformSource;
    private bool _updatingFromEngine;
    private int _volumeBeforeMute = 100;

    public NowPlayingViewModel(IPlaybackService playback, ICoverArtCache covers, IAudioTap audioTap, IWaveformSource waveformSource)
    {
        _playback = playback;
        _covers = covers;
        _waveformSource = waveformSource;
        AudioTap = audioTap;
        _volume = playback.Volume;
        _playback.PropertyChanged += OnPlaybackChanged;
        _waveformSource.WaveformChanged += (_, _) => Dispatcher.UIThread.Post(() => Waveform = _waveformSource.Waveform);
    }

    /// <summary>Live audio tap consumed by the visualizers.</summary>
    public IAudioTap AudioTap { get; }

    /// <summary>The playing track, or null.</summary>
    public Track? Track { get; private set; }

    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _artist = string.Empty;
    [ObservableProperty] private string _album = string.Empty;
    [ObservableProperty] private string _year = string.Empty;
    [ObservableProperty] private int _trackId;
    [ObservableProperty] private int _artistId;
    [ObservableProperty] private int _albumId;
    [ObservableProperty] private string? _coverPath;
    [ObservableProperty] private bool _hasTrack;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _shuffleEnabled;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRepeatOn), nameof(IsRepeatOne), nameof(RepeatLabel))]
    private RepeatMode _repeatMode;
    [ObservableProperty] private double _durationSeconds;
    [ObservableProperty] private string _positionText = "0:00";
    [ObservableProperty] private string _durationText = "0:00";

    /// <summary>Smoothed whole-track loudness envelope (0..1) drawn behind the seek bar; null until computed.</summary>
    [ObservableProperty] private float[]? _waveform;

    /// <summary>"FLAC", "MP3", "MIDI".</summary>
    [ObservableProperty] private string _codec = string.Empty;

    /// <summary>"24-bit / 192 kHz" (empty when unknown).</summary>
    [ObservableProperty] private string _depthRate = string.Empty;

    /// <summary>Compact signal-path badge for the transport bar, e.g. "24/96 · SHARED".</summary>
    [ObservableProperty] private string _signalPath = string.Empty;

    /// <summary>Output mode badge. Playback goes through the shared-mode Windows mixer today.</summary>
    public string OutputMode => "SHARED";

    /// <summary>
    /// True only when decoded samples reach the device unchanged (exclusive mode, no resampling/DSP, 100%
    /// volume). The engine has no exclusive-mode output yet, so this is never true; the accent badge stays off.
    /// </summary>
    public bool IsBitPerfect => false;

    [ObservableProperty] private IReadOnlyList<SignalStage> _signalStages = Array.Empty<SignalStage>();

    public bool IsRepeatOn => RepeatMode != RepeatMode.Off;
    public bool IsRepeatOne => RepeatMode == RepeatMode.One;
    public string RepeatLabel => RepeatMode switch
    {
        RepeatMode.All => "Repeat: all",
        RepeatMode.One => "Repeat: one",
        _ => "Repeat: off",
    };

    private double _positionSeconds;
    public double PositionSeconds
    {
        get => _positionSeconds;
        set
        {
            if (Math.Abs(_positionSeconds - value) < 0.01) return;
            _positionSeconds = value;
            OnPropertyChanged();
            if (!_updatingFromEngine)
                _playback.Seek(TimeSpan.FromSeconds(value));
        }
    }

    private int _volume;
    public int Volume
    {
        get => _volume;
        set
        {
            if (!SetProperty(ref _volume, value)) return;
            _playback.Volume = value;
            OnPropertyChanged(nameof(IsMuted));
            UpdateSignalPath();
        }
    }

    public bool IsMuted => Volume == 0;

    [RelayCommand] private Task PlayPause() => _playback.TogglePlayPauseAsync();
    [RelayCommand] private Task Next() => _playback.NextAsync();
    [RelayCommand] private Task Previous() => _playback.PreviousAsync();
    [RelayCommand] private void ToggleShuffle() => _playback.ShuffleEnabled = !_playback.ShuffleEnabled;
    [RelayCommand]
    private void CycleRepeat() => _playback.RepeatMode = _playback.RepeatMode switch
    {
        RepeatMode.Off => RepeatMode.All,
        RepeatMode.All => RepeatMode.One,
        _ => RepeatMode.Off
    };

    [RelayCommand]
    private void ToggleMute()
    {
        if (Volume > 0) { _volumeBeforeMute = Volume; Volume = 0; }
        else Volume = _volumeBeforeMute > 0 ? _volumeBeforeMute : 100;
    }

    /// <summary>Seeks relative to the current position (keyboard ←/→).</summary>
    public void SeekBy(double seconds)
        => PositionSeconds = Math.Clamp(PositionSeconds + seconds, 0, Math.Max(0, DurationSeconds - 0.5));

    public void SeekTo(TimeSpan position) => PositionSeconds = position.TotalSeconds;

    private void OnPlaybackChanged(object? sender, PropertyChangedEventArgs e)
        => Dispatcher.UIThread.Post(() => Apply(e.PropertyName));

    private void Apply(string? property)
    {
        switch (property)
        {
            case nameof(IPlaybackService.CurrentTrack):
                var t = _playback.CurrentTrack;
                Track = t;
                HasTrack = t is not null;
                TrackId = t?.Id ?? 0;
                Title = t?.Title ?? string.Empty;
                Artist = t is null ? string.Empty
                    : (string.IsNullOrEmpty(t.TrackArtist) ? t.Album?.AlbumArtist?.Name ?? "Unknown Artist" : t.TrackArtist);
                Album = t?.Album?.Title ?? string.Empty;
                Year = t?.Album?.Year?.ToString() ?? string.Empty;
                ArtistId = t?.Album?.AlbumArtistId ?? 0;
                AlbumId = t?.AlbumId ?? 0;
                CoverPath = _covers.GetPath(t?.CoverArtKey ?? t?.Album?.CoverArtKey);
                Codec = t is null ? string.Empty : AudioFormatText.Codec(t);
                DepthRate = t is null ? string.Empty : AudioFormatText.Long(t);
                OnPropertyChanged(nameof(Track));
                UpdateSignalPath();
                break;
            case nameof(IPlaybackService.State):
                IsPlaying = _playback.State == PlaybackState.Playing;
                break;
            case nameof(IPlaybackService.Position):
                _updatingFromEngine = true;
                PositionSeconds = _playback.Position.TotalSeconds;
                PositionText = AudioFormatText.Duration(_playback.Position);
                _updatingFromEngine = false;
                break;
            case nameof(IPlaybackService.Duration):
                DurationSeconds = _playback.Duration.TotalSeconds;
                DurationText = AudioFormatText.Duration(_playback.Duration);
                break;
            case nameof(IPlaybackService.ShuffleEnabled):
                ShuffleEnabled = _playback.ShuffleEnabled;
                break;
            case nameof(IPlaybackService.RepeatMode):
                RepeatMode = _playback.RepeatMode;
                break;
        }
    }

    private void UpdateSignalPath()
    {
        if (Track is not { } t)
        {
            SignalPath = string.Empty;
            SignalStages = Array.Empty<SignalStage>();
            return;
        }

        var quality = AudioFormatText.Short(t);
        SignalPath = string.IsNullOrEmpty(quality) ? $"{Codec} · {OutputMode}" : $"{quality} · {OutputMode}";

        var source = string.IsNullOrEmpty(DepthRate) ? Codec : $"{Codec} · {DepthRate}";
        SignalStages = new[]
        {
            new SignalStage("Source", source),
            new SignalStage("Decoder", string.IsNullOrEmpty(_playback.Renderer) ? "BASS" : _playback.Renderer),
            new SignalStage("DSP", Volume >= 100 ? "None" : $"Digital volume {Volume}%"),
            new SignalStage("Resampler", "Windows audio engine (shared mode)"),
            new SignalStage("Output", "Default device · Shared"),
        };
    }
}
