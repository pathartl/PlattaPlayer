using System.Runtime.Versioning;
using Windows.Media;
using Windows.Storage;
using Windows.Storage.Streams;
using PlattaPlayer.Core.Abstractions;
using PlattaPlayer.Core.Models;
using CorePlaybackState = PlattaPlayer.Core.Models.PlaybackState;

namespace PlattaPlayer.Platform.Windows;

/// <summary>
/// Windows System Media Transport Controls (SMTC) integration: surfaces now-playing metadata to the
/// OS media overlay / lock screen and routes hardware/overlay transport buttons back into the app.
/// </summary>
[SupportedOSPlatform("windows10.0.10240.0")]
public sealed class WindowsSystemMediaControls : ISystemMediaControls
{
    private SystemMediaTransportControls? _smtc;

    public event EventHandler? PlayRequested;
    public event EventHandler? PauseRequested;
    public event EventHandler? NextRequested;
    public event EventHandler? PreviousRequested;

    public void Initialize(nint windowHandle)
    {
        _smtc = SmtcInterop.GetForWindow(windowHandle);
        _smtc.IsEnabled = true;
        _smtc.IsPlayEnabled = true;
        _smtc.IsPauseEnabled = true;
        _smtc.IsNextEnabled = true;
        _smtc.IsPreviousEnabled = true;
        _smtc.DisplayUpdater.Type = MediaPlaybackType.Music;
        _smtc.ButtonPressed += OnButtonPressed;
    }

    public void UpdateMetadata(MediaMetadata metadata)
    {
        if (_smtc is null) return;

        var updater = _smtc.DisplayUpdater;
        updater.Type = MediaPlaybackType.Music;
        updater.MusicProperties.Title = metadata.Title ?? string.Empty;
        updater.MusicProperties.Artist = metadata.Artist ?? string.Empty;
        updater.MusicProperties.AlbumTitle = metadata.Album ?? string.Empty;

        if (!string.IsNullOrEmpty(metadata.CoverArtPath) && File.Exists(metadata.CoverArtPath))
        {
            var file = StorageFile.GetFileFromPathAsync(metadata.CoverArtPath).AsTask().GetAwaiter().GetResult();
            updater.Thumbnail = RandomAccessStreamReference.CreateFromFile(file);
        }
        else
        {
            updater.Thumbnail = null;
        }

        updater.Update();
    }

    public void UpdatePlaybackState(CorePlaybackState state)
    {
        if (_smtc is null) return;
        _smtc.PlaybackStatus = state switch
        {
            CorePlaybackState.Playing => MediaPlaybackStatus.Playing,
            CorePlaybackState.Paused => MediaPlaybackStatus.Paused,
            _ => MediaPlaybackStatus.Stopped
        };
    }

    public void UpdateTimeline(TimeSpan position, TimeSpan duration)
    {
        if (_smtc is null) return;
        _smtc.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
        {
            StartTime = TimeSpan.Zero,
            MinSeekTime = TimeSpan.Zero,
            Position = position,
            MaxSeekTime = duration,
            EndTime = duration
        });
    }

    private void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        switch (args.Button)
        {
            case SystemMediaTransportControlsButton.Play: PlayRequested?.Invoke(this, EventArgs.Empty); break;
            case SystemMediaTransportControlsButton.Pause: PauseRequested?.Invoke(this, EventArgs.Empty); break;
            case SystemMediaTransportControlsButton.Next: NextRequested?.Invoke(this, EventArgs.Empty); break;
            case SystemMediaTransportControlsButton.Previous: PreviousRequested?.Invoke(this, EventArgs.Empty); break;
        }
    }

    public void Dispose()
    {
        if (_smtc is null) return;
        _smtc.ButtonPressed -= OnButtonPressed;
        _smtc.IsEnabled = false;
        _smtc = null;
    }
}
