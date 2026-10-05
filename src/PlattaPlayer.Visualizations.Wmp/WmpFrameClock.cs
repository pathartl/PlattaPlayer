using System;
using System.Diagnostics;

namespace PlattaPlayer.Visualizations.Wmp;

/// <summary>
/// A fixed-step clock for the per-frame WMP effects: ask it on every display refresh how many effect
/// steps are due, and it hands them out at an even <see cref="StepInterval"/> on average however the
/// refreshes fall.
///
/// It replaces pacing with a <c>DispatcherTimer</c>. On Windows a 16 ms DispatcherTimer is rounded to
/// the system timer tick: measured on Avalonia 12 (2026-10-02) it fired ~37 times a second with gaps
/// from 16 to 34 ms, so the effect ran slow and stepped unevenly.
/// </summary>
public sealed class WmpFrameClock
{
    /// <summary>
    /// After a stall (a hidden window, a debugger, a long table build) at most this many steps run in one
    /// refresh and the rest of the backlog is dropped, as WM_TIMER coalesces missed ticks in WMP.
    /// </summary>
    private const int MaxCatchUp = 4;

    private readonly long _stepTicks;
    private long _next;
    private bool _started;

    public WmpFrameClock(TimeSpan stepInterval)
    {
        StepInterval = stepInterval;
        _stepTicks = Math.Max(1, (long)(stepInterval.TotalSeconds * Stopwatch.Frequency));
    }

    public TimeSpan StepInterval { get; }

    /// <summary>The number of steps to run now. The first call returns 1 and starts the clock.</summary>
    public int TakeDueSteps()
    {
        var now = Stopwatch.GetTimestamp();
        if (!_started)
        {
            _started = true;
            _next = now + _stepTicks;
            return 1;
        }

        if (now < _next) return 0;
        var due = (int)Math.Min((now - _next) / _stepTicks + 1, int.MaxValue);
        if (due > MaxCatchUp)
        {
            _next = now + _stepTicks;
            return MaxCatchUp;
        }
        _next += due * _stepTicks;
        return due;
    }

    /// <summary>Restart from the next call, e.g. when the control is shown again.</summary>
    public void Reset() => _started = false;
}
