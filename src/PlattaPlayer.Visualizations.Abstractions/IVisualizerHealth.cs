namespace PlattaPlayer.Visualizations.Abstractions;

/// <summary>
/// Optional liveness reporting for a visualizer, implemented by its <see cref="VisualizerInstance.Controller"/>.
/// The host polls <see cref="Heartbeat"/> while the visualizer should be animating; when it stops moving the
/// host first calls <see cref="Resume"/> and, if that does not help, rebuilds the visualizer from its plugin.
///
/// The usual cause of a stall is a self-scheduling render loop losing its one outstanding request (an
/// OpenGL control that asks for the next frame from inside its render, or a single animation-frame request),
/// for example across a minimize or a GPU context loss. Nothing then asks again and the last frame stays up.
/// </summary>
public interface IVisualizerHealth
{
    /// <summary>
    /// Counts render callbacks the visualizer has handled, including ones that drew nothing because it is
    /// still loading or too small. It must keep rising whenever the render loop is alive, and stop when the
    /// loop has stopped or the visualizer has given up after an error. Read from the UI thread; it may be
    /// written from the render thread.
    /// </summary>
    long Heartbeat { get; }

    /// <summary>
    /// Re-arms the render loop if it has stopped (called on the UI thread, also after the window is restored
    /// from minimized). Must be safe to call while the loop is running.
    /// </summary>
    void Resume();
}
