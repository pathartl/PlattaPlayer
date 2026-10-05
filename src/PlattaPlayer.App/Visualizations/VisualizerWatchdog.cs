using System;
using System.Diagnostics;
using System.IO;
using Avalonia.Threading;
using PlattaPlayer.Data;
using PlattaPlayer.Visualizations.Abstractions;

namespace PlattaPlayer.App.Visualizations;

/// <summary>
/// Notices when the active visualizer has stopped animating and brings it back. Every second, while the
/// visualizer should be on screen, it reads the plugin's <see cref="IVisualizerHealth.Heartbeat"/>. When the
/// count has not moved for <see cref="ResumeAfter"/> it asks the plugin to re-arm its render loop; if that
/// does not get it moving within <see cref="RebuildAfter"/>, the host rebuilds the visualizer from its plugin
/// (a fresh control, so fresh GL resources). Automatic rebuilds are capped so a plugin that fails every time
/// is left alone instead of being rebuilt in a loop; a manual restart refills the budget.
///
/// Plugins that do not implement <see cref="IVisualizerHealth"/> are not watched. Stalls and recoveries are
/// logged to <c>visualizer-watchdog.log</c> in the data folder.
/// </summary>
internal sealed class VisualizerWatchdog
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ResumeAfter = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RebuildAfter = TimeSpan.FromSeconds(5);

    /// <summary>Automatic rebuilds allowed before giving up; refilled after <see cref="HealthyFor"/>.</summary>
    private const int MaxRebuilds = 3;
    private static readonly TimeSpan HealthyFor = TimeSpan.FromMinutes(1);

    private readonly DispatcherTimer _timer;
    private readonly Func<bool> _shouldBeRendering;
    private readonly Action _rebuild;

    private IVisualizerHealth? _health;
    private string _name = "";
    private long _lastBeat;
    private long _lastProgress;
    private bool _resumed;
    private int _rebuilds;
    private long _lastRebuild;
    private bool _gaveUp;

    /// <param name="shouldBeRendering">True while the visualizer is on screen and expected to animate.</param>
    /// <param name="rebuild">Replaces the visualizer with a fresh one from the same plugin, then calls
    /// <see cref="Attach"/> with <c>recovery: true</c>.</param>
    public VisualizerWatchdog(Func<bool> shouldBeRendering, Action rebuild)
    {
        _shouldBeRendering = shouldBeRendering;
        _rebuild = rebuild;
        _timer = new DispatcherTimer(CheckInterval, DispatcherPriority.Background, (_, _) => Check());
        _timer.Start();
    }

    /// <summary>
    /// Starts watching a newly built visualizer. A rebuild the watchdog itself asked for passes
    /// <paramref name="recovery"/> so the rebuild budget carries over; anything else starts a clean slate.
    /// </summary>
    public void Attach(VisualizerInstance? instance, string name, bool recovery = false)
    {
        _health = instance?.Controller as IVisualizerHealth ?? instance?.View as IVisualizerHealth;
        _name = name;
        _lastBeat = _health?.Heartbeat ?? 0;
        _lastProgress = Stopwatch.GetTimestamp();
        _resumed = false;
        if (recovery) return;
        _rebuilds = 0;
        _gaveUp = false;
    }

    /// <summary>
    /// Re-arms the render loop straight away, for moments known to drop frame requests (the window coming
    /// back from minimized). Restarts the stall clock so the visualizer gets a full grace period.
    /// </summary>
    public void Resume()
    {
        _lastProgress = Stopwatch.GetTimestamp();
        _resumed = false;
        _health?.Resume();
    }

    private void Check()
    {
        if (_health is not { } health || _gaveUp) return;

        var now = Stopwatch.GetTimestamp();
        // Time spent hidden (minimized, paused, another page) is not a stall.
        if (!_shouldBeRendering())
        {
            _lastProgress = now;
            return;
        }

        var beat = health.Heartbeat;
        if (beat != _lastBeat)
        {
            if (_resumed) Log($"{_name}: running again after resume");
            _lastBeat = beat;
            _lastProgress = now;
            _resumed = false;
            if (_rebuilds > 0 && Stopwatch.GetElapsedTime(_lastRebuild, now) >= HealthyFor) _rebuilds = 0;
            return;
        }

        var stalled = Stopwatch.GetElapsedTime(_lastProgress, now);
        if (!_resumed && stalled >= ResumeAfter)
        {
            _resumed = true;
            Log($"{_name}: no frames for {stalled.TotalSeconds:0.0}s, resuming render loop");
            health.Resume();
        }
        else if (stalled >= RebuildAfter)
        {
            if (_rebuilds >= MaxRebuilds)
            {
                _gaveUp = true;
                Log($"{_name}: still stalled after {MaxRebuilds} rebuilds, giving up until restarted manually");
                return;
            }
            _rebuilds++;
            _lastRebuild = now;
            Log($"{_name}: no frames for {stalled.TotalSeconds:0.0}s after resume, rebuilding ({_rebuilds}/{MaxRebuilds})");
            _rebuild();
        }
    }

    private static void Log(string message)
    {
        Debug.WriteLine($"Visualizer watchdog: {message}");
        try
        {
            File.AppendAllText(Path.Combine(AppPaths.DataDirectory, "visualizer-watchdog.log"),
                               $"[{DateTime.Now:O}] {message}\n");
        }
        catch { /* ignore */ }
    }
}
