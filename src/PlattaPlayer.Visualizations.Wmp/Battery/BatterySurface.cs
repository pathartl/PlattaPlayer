namespace PlattaPlayer.Visualizations.Wmp.Battery;

/// <summary>
/// <c>CBatterySurface</c>: one 8-bit palette-indexed field, row-major and top-down, with stride = width.
/// Battery holds two of them and ping-pongs between them. Every full-frame pass swaps the pair first and
/// then writes the front from the back. The drawing primitives write in place.
///
/// Derivation: <c>Code\battery\02_pipeline.md</c> §5.
/// </summary>
public sealed class BatterySurface : IBatteryCanvas
{
    public BatterySurface(int width, int height)
    {
        W = width;
        H = height;
        Bits = new byte[width * height]; // Erase(0) (AllocateSurfaces); never cleared again
    }

    public int W { get; }

    public int H { get; }

    public byte[] Bits { get; }

    /// <summary>
    /// <c>DrawLine</c> (<c>0x180414050</c>): overwrite, with no clipping. This is not a symmetric
    /// Bresenham. The error starts at 0, and the minor step fires on <c>err &gt; major</c>, subtracting
    /// <c>major</c>. So it plots <c>major + 1</c> pixels and, when the minor delta is non-zero, never quite
    /// reaches the far endpoint. Ties (|dx| == |dy|) take the y-major branch.
    /// </summary>
    public void DrawLine(int x1, int y1, int x2, int y2, byte c)
    {
        var p = W * y1 + x1;
        int dy = y2 - y1, ystep = W;
        if (dy < 0) { dy = -dy; ystep = -W; }
        int dx = x2 - x1, xstep = 1;
        if (dx < 0) { dx = -dx; xstep = -1; }

        var err = 0;
        if (dy < dx)
        {
            for (var n = dx + 1; n > 0; n--)
            {
                Bits[p] = c;
                p += xstep;
                err += dy;
                if (err > dx) { err -= dx; p += ystep; }
            }
        }
        else
        {
            for (var n = dy + 1; n > 0; n--)
            {
                Bits[p] = c;
                p += ystep;
                err += dx;
                if (err > dy) { err -= dy; p += xstep; }
            }
        }
    }

    void IBatteryCanvas.Pixel(int x, int y, byte c) => Bits[W * y + x] = c;

    void IBatteryCanvas.Line(int x1, int y1, int x2, int y2, byte c) => DrawLine(x1, y1, x2, y2, c);
}
