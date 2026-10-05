namespace PlattaPlayer.Visualizations.Wmp;

/// <summary>
/// How often Windows Media Player repaints a visualization.
///
/// This is not a preference — it is part of the effects' behaviour. Every motion constant in them is
/// PER FRAME (bar falloff is 4 px/frame, peak decay accelerates 0.2 px/frame^2, Alchemy's feedback warp
/// is re-applied once per frame), so the tick rate directly sets how fast everything moves. Running
/// uncapped makes the same effect animate several times too fast on a high-refresh display.
/// </summary>
public static class WmpFrameRate
{
    /// <summary>
    /// Windowed repaint interval, in milliseconds.
    ///
    /// wmp.dll arms its visualization pane with <c>SetTimer(hwnd, 0x10e1, 1000/fps)</c> (integer
    /// division, FUN_1804728f0). The fps field defaults to -1 and is only ever written through a COM
    /// property with no static caller, so the value cannot be read out of the binary and had to be
    /// measured: the harness's <c>measure-fps</c> verb polled a live wmplayer.exe far faster than its
    /// repaint rate and counted pixel transitions, cross-checked at three poll rates to prove the
    /// estimate had converged rather than saturated (2026-08-12, on a 239 Hz display so vsync was not a
    /// confound). Observed 46-64 fps depending on content, consistent with an fps property of 60 and
    /// hence a 16 ms interval.
    ///
    /// The observed rate sits below the timer rate because the renderers skip work when the TimedLevel
    /// timestamp has not advanced, so some ticks produce an identical frame. 16 ms is therefore the
    /// right target and an upper bound on what is observable.
    ///
    /// A DispatcherTimer at this interval does NOT achieve it (it fires ~37 times a second); count steps
    /// with <see cref="WmpFrameClock"/> instead.
    /// </summary>
    public const int WindowedIntervalMs = 16;

    public static TimeSpan WindowedInterval => TimeSpan.FromMilliseconds(WindowedIntervalMs);
}
