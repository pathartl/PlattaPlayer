using PlattaPlayer.Visualizations.Wmp.Battery.Shifts;

namespace PlattaPlayer.Visualizations.Wmp.Battery.Gpu;

/// <summary>
/// The GPU path's <see cref="IBatteryRaster"/>. Instead of touching pixels, it records what
/// <see cref="BatteryCore"/> asks for into a <see cref="BatteryGpuFrame"/>, which the plugin's GL renderer
/// replays at device resolution.
///
/// Primitives come in two flavours:
/// <list type="bullet">
/// <item><b>Exact</b> (scale 1): a line is rasterized here by the original's own <c>DrawLine</c> walk and
/// recorded pixel run by pixel run, so the GPU writes exactly the pixels the CPU would.</item>
/// <item><b>Scaled</b>: a line becomes a band one field unit thick between the end pixels' centres,
/// extended half a unit at each end along its major axis (the pixels the walk covers, as one continuous
/// shape), and a pixel becomes a one-unit square.</item>
/// </list>
/// Either way each primitive overwrites in draw order, as the original does.
/// </summary>
public sealed class BatteryFrameRecorder(BatteryRenderData rd) : IBatteryRaster, IBatteryCanvas
{
    private int _runStart;

    public BatteryGpuFrame Frame { get; } = new();

    /// <summary>True at scale 1: rasterize lines exactly.</summary>
    public bool ExactPixels { get; set; } = true;

    public IBatteryCanvas Canvas => this;

    public int W => rd.W;

    public int H => rd.H;

    /// <summary>Start recording a frame.</summary>
    public void Begin()
    {
        Frame.Clear();
        _runStart = 0;
    }

    /// <summary>Finish the frame.</summary>
    public void End() => Flush();

    public void Pixel(int x, int y, byte c) => Frame.Rect(x, y, x + 1, y + 1, c);

    public void Line(int x1, int y1, int x2, int y2, byte c)
    {
        // Axis-aligned lines (every border) cover a plain rectangle in both modes.
        if (x1 == x2 || y1 == y2)
        {
            Frame.Rect(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2) + 1, Math.Max(y1, y2) + 1, c);
            return;
        }

        if (ExactPixels) WalkLine(x1, y1, x2, y2, c);
        else Band(x1, y1, x2, y2, c);
    }

    /// <summary><see cref="BatterySurface.DrawLine"/>'s walk. Each run of pixels along the major axis
    /// becomes one rectangle.</summary>
    private void WalkLine(int x1, int y1, int x2, int y2, byte c)
    {
        int dy = y2 - y1, ystep = 1;
        if (dy < 0) { dy = -dy; ystep = -1; }
        int dx = x2 - x1, xstep = 1;
        if (dx < 0) { dx = -dx; xstep = -1; }

        int x = x1, y = y1, err = 0;
        if (dy < dx)
        {
            var runX = x;
            for (var n = dx + 1; n > 0; n--)
            {
                var last = x;
                x += xstep;
                err += dy;
                var stepped = err > dx;
                if (stepped || n == 1)
                {
                    Frame.Rect(Math.Min(runX, last), y, Math.Max(runX, last) + 1, y + 1, c);
                    runX = x;
                }
                if (stepped) { err -= dx; y += ystep; }
            }
        }
        else
        {
            var runY = y;
            for (var n = dy + 1; n > 0; n--)
            {
                var last = y;
                y += ystep;
                err += dx;
                var stepped = err > dy;
                if (stepped || n == 1)
                {
                    Frame.Rect(x, Math.Min(runY, last), x + 1, Math.Max(runY, last) + 1, c);
                    runY = y;
                }
                if (stepped) { err -= dy; x += xstep; }
            }
        }
    }

    /// <summary>A parallelogram one unit across the minor axis, from half a unit before the first pixel to
    /// half a unit past the last along the major axis. Ties go y-major, as in the walk.</summary>
    private void Band(int x1, int y1, int x2, int y2, byte c)
    {
        float ax = x1 + 0.5f, ay = y1 + 0.5f, bx = x2 + 0.5f, by = y2 + 0.5f;
        if (Math.Abs(y2 - y1) < Math.Abs(x2 - x1))
        {
            var sx = x2 > x1 ? 0.5f : -0.5f;
            ax -= sx;
            bx += sx;
            Frame.Quad(ax, ay - 0.5f, bx, by - 0.5f, bx, by + 0.5f, ax, ay + 0.5f, c);
        }
        else
        {
            var sy = y2 > y1 ? 0.5f : -0.5f;
            ay -= sy;
            by += sy;
            Frame.Quad(ax - 0.5f, ay, bx - 0.5f, by, bx + 0.5f, by, ax + 0.5f, ay, c);
        }
    }

    public void Gather(ShiftTable table, int[] map)
    {
        Flush();
        var step = -1;
        if (!ReferenceEquals(map, table.Table))
        {
            for (var i = 0; i < ShiftTable.TransitionCount; i++)
                if (ReferenceEquals(map, table.TransitionTable(i))) step = i;
        }
        Frame.Commands.Add(new BatteryGpuCommand(BatteryGpuOp.Gather, Table: table, Version: table.Version, Map: map, Step: step));
    }

    public void Blur()
    {
        Flush();
        Frame.Commands.Add(new BatteryGpuCommand(BatteryGpuOp.Blur));
    }

    private void Flush()
    {
        var n = Frame.VertexCount - _runStart;
        if (n > 0) Frame.Commands.Add(new BatteryGpuCommand(BatteryGpuOp.Prims, _runStart, n));
        _runStart = Frame.VertexCount;
    }
}
