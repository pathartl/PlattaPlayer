using System;
using System.ComponentModel;
using PlattaPlayer.Core.Abstractions;
using PlattaPlayer.Core.Models;

namespace PlattaPlayer.App.Services;

/// <summary>
/// Connects the OS media controls to the playback service: pushes now-playing metadata, state and
/// timeline out to <see cref="ISystemMediaControls"/>, and routes transport-button events back into
/// <see cref="IPlaybackService"/>. Activated once the main window handle is available.
/// </summary>
internal sealed class SystemMediaControlsBridge : IDisposable
{
    private readonly IPlaybackService _playback;
    private readonly ISystemMediaControls _smtc;
    private readonly ICoverArtCache _covers;

    private Track? _lastTrack;
    private bool _attached;

    public SystemMediaControlsBridge(IPlaybackService playback, ISystemMediaControls smtc, ICoverArtCache covers)
    {
        _playback = playback;
        _smtc = smtc;
        _covers = covers;
    }

    /// <summary>Binds the controls to the main window's native handle and starts observing playback.</summary>
    public void Attach(nint windowHandle)
    {
        if (_attached) return;
        _attached = true;

        _smtc.Initialize(windowHandle);

        // SMTC sends Play while paused/stopped and Pause while playing; a toggle satisfies both.
        _smtc.PlayRequested += (_, _) => _ = _playback.TogglePlayPauseAsync();
        _smtc.PauseRequested += (_, _) => _ = _playback.TogglePlayPauseAsync();
        _smtc.NextRequested += (_, _) => _ = _playback.NextAsync();
        _smtc.PreviousRequested += (_, _) => _ = _playback.PreviousAsync();

        _playback.PropertyChanged += OnPlaybackChanged;
    }

    private void OnPlaybackChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(IPlaybackService.CurrentTrack):
                PushMetadata();
                break;
            case nameof(IPlaybackService.State):
                _smtc.UpdatePlaybackState(_playback.State);
                break;
            case nameof(IPlaybackService.Position):
            case nameof(IPlaybackService.Duration):
                _smtc.UpdateTimeline(_playback.Position, _playback.Duration);
                break;
        }
    }

    private void PushMetadata()
    {
        var track = _playback.CurrentTrack;
        if (track is null || ReferenceEquals(track, _lastTrack)) return;
        _lastTrack = track;

        var artist = TagValues.Display(!string.IsNullOrEmpty(track.TrackArtist)
            ? track.TrackArtist
            : track.Album?.ArtistCredit);
        var album = track.Album?.Title ?? string.Empty;
        var cover = _covers.GetPath(track.CoverArtKey ?? track.Album?.CoverArtKey);

        _smtc.UpdateMetadata(new MediaMetadata(track.Title, artist, album, cover));
    }

    public void Dispose()
    {
        if (!_attached) return;
        _playback.PropertyChanged -= OnPlaybackChanged;
        _smtc.Dispose();
    }
}
