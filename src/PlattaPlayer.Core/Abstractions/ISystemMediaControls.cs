using PlattaPlayer.Core.Models;

namespace PlattaPlayer.Core.Abstractions;

/// <summary>
/// Bridge to OS-level media transport controls (Windows SMTC, and later macOS/Linux/mobile).
/// The desktop head wires button events back into the playback service and pushes metadata out.
/// </summary>
public interface ISystemMediaControls : IDisposable
{
    /// <summary>Bind the controls to the main window. Pass the native window handle (HWND on Windows).</summary>
    void Initialize(nint windowHandle);

    void UpdateMetadata(MediaMetadata metadata);

    void UpdatePlaybackState(PlaybackState state);

    void UpdateTimeline(TimeSpan position, TimeSpan duration);

    event EventHandler? PlayRequested;
    event EventHandler? PauseRequested;
    event EventHandler? NextRequested;
    event EventHandler? PreviousRequested;
}

/// <summary>No-op implementation used on platforms without OS media-control integration.</summary>
public sealed class NullSystemMediaControls : ISystemMediaControls
{
    public void Initialize(nint windowHandle) { }
    public void UpdateMetadata(MediaMetadata metadata) { }
    public void UpdatePlaybackState(PlaybackState state) { }
    public void UpdateTimeline(TimeSpan position, TimeSpan duration) { }

    public event EventHandler? PlayRequested { add { } remove { } }
    public event EventHandler? PauseRequested { add { } remove { } }
    public event EventHandler? NextRequested { add { } remove { } }
    public event EventHandler? PreviousRequested { add { } remove { } }

    public void Dispose() { }
}
