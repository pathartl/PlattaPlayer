namespace PlattaPlayer.Visualizations.Wmp.BarsAndWaves;

/// <summary>
/// The raster surface "Bars and Waves" draws on: a plain BGRA <c>int[]</c> with exactly the primitives
/// wmp.dll uses and nothing else.
///
/// Deliberately NOT built on <c>Framebuffer.PixelBuffer</c>. That type alpha-blends every write and
/// offers gradients and stroked widths; the original does none of those things — it writes raw pixel
/// values into a DIB with no blending at all. Sharing it would quietly invite exactly the embellishments
/// ("add a gradient, soften the edge") that made the previous port inaccurate. It also works in
/// 0xAARRGGBB while the original works in COLORREF, which is a channel-swap bug waiting to happen, so
/// the conversion is done once here in <see cref="FromColorRef"/> and nowhere else.
/// </summary>
internal sealed class DibSurface
{
    private int[] _pixels = [];

    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>BGRA pixels, top-down, one int per pixel — the layout a WriteableBitmap wants.</summary>
    public int[] Pixels => _pixels;

    public void Resize(int width, int height)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        var needed = Width * Height;
        if (_pixels.Length != needed) _pixels = new int[needed];
    }

    /// <summary>
    /// COLORREF (0x00BBGGRR) to our packed BGRA int. Every colour in the preset table is stored as the
    /// literal COLORREF from the decompile, so this is the single point where byte order is decided.
    /// </summary>
    public static int FromColorRef(int colorRef)
    {
        var r = colorRef & 0xFF;
        var g = (colorRef >> 8) & 0xFF;
        var b = (colorRef >> 16) & 0xFF;
        return unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
    }

    public void Clear(int argb) => Array.Fill(_pixels, argb);

    /// <summary>
    /// Filled rectangle with the original's exact semantics (wmp.dll FUN_18044b140): the coordinate
    /// pairs are normalised so either order works, the low edge is clamped to 0 and the high edge to
    /// Width-1 / Height-1, and BOTH edges are INCLUSIVE.
    ///
    /// The original writes into a bottom-up DIB and indexes it as <c>(H - y - 1) * stride</c>; those two
    /// inversions cancel, so <paramref name="y0"/>/<paramref name="y1"/> are ordinary top-down rows here
    /// and no flip is needed.
    /// </summary>
    public void FillRect(int x0, int y0, int x1, int y1, int argb)
    {
        if (x0 > x1) (x0, x1) = (x1, x0);
        if (y0 > y1) (y0, y1) = (y1, y0);

        if (x0 < 0) x0 = 0;
        if (y0 < 0) y0 = 0;
        if (x1 > Width - 1) x1 = Width - 1;
        if (y1 > Height - 1) y1 = Height - 1;
        if (x0 > x1 || y0 > y1) return;

        for (var y = y0; y <= y1; y++)
        {
            var row = y * Width;
            for (var x = x0; x <= x1; x++) _pixels[row + x] = argb;
        }
    }

    /// <summary>Single-row horizontal run — the peak cap. Both x edges inclusive.</summary>
    public void HLine(int x0, int x1, int y, int argb)
    {
        if (y < 0 || y >= Height) return;
        if (x0 > x1) (x0, x1) = (x1, x0);
        if (x0 < 0) x0 = 0;
        if (x1 > Width - 1) x1 = Width - 1;
        if (x0 > x1) return;

        var row = y * Width;
        for (var x = x0; x <= x1; x++) _pixels[row + x] = argb;
    }

    /// <summary>
    /// The Scope polyline segment, matching wmp.dll's FUN_18044a780 exactly.
    ///
    /// Two details differ from a textbook Bresenham and both are visible in a pixel diff. First, the
    /// error term starts at <c>-majorAxisLength</c> and the step test is <c>2*minor + err &gt; 0</c>,
    /// which breaks ties the other way. Second, the original does NOT clip: if either endpoint falls
    /// outside the surface it rejects the whole segment and draws nothing, so a waveform excursion past
    /// the edge leaves a gap rather than a flattened run along it.
    /// </summary>
    public void Line(int x0, int y0, int x1, int y1, int argb)
    {
        if (x0 > x1) { (x0, x1) = (x1, x0); (y0, y1) = (y1, y0); }

        if (x0 < 0 || x1 >= Width) return;
        if (y0 < 0 || y0 >= Height || y1 < 0 || y1 >= Height) return;

        var dx = x1 - x0;
        var dy = y1 - y0;
        var stepY = dy < 0 ? -1 : 1;
        var ady = Math.Abs(dy);

        var x = x0;
        var y = y0;
        SetPixel(x, y, argb);
        if (dx == 0 && ady == 0) return;

        if (ady == 0)
        {
            while (x != x1) { x++; SetPixel(x, y, argb); }
            return;
        }
        if (dx == 0)
        {
            while (y != y1) { y += stepY; SetPixel(x, y, argb); }
            return;
        }

        if (dx < ady)
        {
            var err = -ady;
            do
            {
                var e2 = 2 * dx + err;
                if (e2 >= 1) { x++; err = e2 - 2 * ady; } else { err = e2; }
                y += stepY;
                SetPixel(x, y, argb);
            } while (x != x1 || y != y1);
        }
        else
        {
            var err = -dx;
            do
            {
                var e2 = 2 * ady + err;
                if (e2 >= 1) { y += stepY; err = e2 - 2 * dx; } else { err = e2; }
                x++;
                SetPixel(x, y, argb);
            } while (x != x1 || y != y1);
        }
    }

    public void SetPixel(int x, int y, int argb)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return;
        _pixels[y * Width + x] = argb;
    }
}
