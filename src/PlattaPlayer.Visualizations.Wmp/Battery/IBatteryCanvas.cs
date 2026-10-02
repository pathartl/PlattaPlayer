namespace PlattaPlayer.Visualizations.Wmp.Battery;

/// <summary>
/// The two drawing primitives every Battery effect is built from. <see cref="BatterySurface"/> writes them
/// into the 8-bit field. The GPU path's <see cref="Gpu.BatteryFrameRecorder"/> records them instead.
/// Everything else (<see cref="BatteryDraw.DrawClippedLine"/>, <see cref="BatteryDraw.DrawJCurve"/>, the
/// effects) is written over this interface, so both paths run the very same algorithms.
/// </summary>
public interface IBatteryCanvas
{
    int W { get; }

    int H { get; }

    /// <summary>Overwrite one in-range pixel.</summary>
    void Pixel(int x, int y, byte c);

    /// <summary><c>CBatterySurface::DrawLine</c>: overwrite, no clipping (see <see cref="BatterySurface.DrawLine"/>).</summary>
    void Line(int x1, int y1, int x2, int y2, byte c);
}
