using System;

namespace PlattaPlayer.Visualizations.Wmp.Framebuffer;

/// <summary>
/// A managed 32-bit pixel buffer. Pixels are packed as 0xAARRGGBB ints which, in little-endian memory,
/// lay out as B,G,R,A bytes — exactly Avalonia's <c>Bgra8888</c>, so the host can copy the backing
/// array straight into a locked <c>WriteableBitmap</c> (the original used BGRX surfaces, analysis §B.1).
///
/// Besides storage this carries the simple software-raster primitives the GDI+ "Bars and Waves" family
/// needs — alpha-blended solid/vertical-gradient rectangles and lines (analysis §A).
/// </summary>
public sealed class PixelBuffer
{
    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>Row-major 0xAARRGGBB pixels, length <see cref="Width"/>×<see cref="Height"/>.</summary>
    public int[] Pixels { get; private set; } = Array.Empty<int>();

    public PixelBuffer() { }

    public PixelBuffer(int width, int height) => Resize(width, height);

    public void Resize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width == Width && height == Height) return;
        Width = width;
        Height = height;
        Pixels = new int[width * height];
    }

    /// <summary>Overwrites every pixel with an opaque colour.</summary>
    public void Fill(int argb)
    {
        var p = Pixels;
        for (var i = 0; i < p.Length; i++) p[i] = argb;
    }

    /// <summary>Alpha-composites <paramref name="argb"/> over the existing pixel at (x,y); clipped.</summary>
    public void Blend(int x, int y, int argb)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return;
        BlendIndex(y * Width + x, argb);
    }

    private void BlendIndex(int index, int argb)
    {
        var a = (argb >> 24) & 0xFF;
        if (a == 0) return;

        var p = Pixels;
        if (a == 255)
        {
            p[index] = unchecked((int)0xFF000000) | (argb & 0x00FFFFFF);
            return;
        }

        var dst = p[index];
        var inv = 255 - a;
        var r = (((argb >> 16) & 0xFF) * a + ((dst >> 16) & 0xFF) * inv) / 255;
        var g = (((argb >> 8) & 0xFF) * a + ((dst >> 8) & 0xFF) * inv) / 255;
        var b = ((argb & 0xFF) * a + (dst & 0xFF) * inv) / 255;
        p[index] = unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
    }

    /// <summary>Alpha-blended solid rectangle, clipped to the buffer.</summary>
    public void FillRect(int x, int y, int w, int h, int argb)
    {
        if (w <= 0 || h <= 0) return;
        var x0 = Math.Max(0, x);
        var y0 = Math.Max(0, y);
        var x1 = Math.Min(Width, x + w);
        var y1 = Math.Min(Height, y + h);
        for (var yy = y0; yy < y1; yy++)
        {
            var row = yy * Width;
            for (var xx = x0; xx < x1; xx++)
                BlendIndex(row + xx, argb);
        }
    }

    /// <summary>
    /// Alpha-blended rectangle with a vertical gradient from <paramref name="topArgb"/> at its top edge
    /// to <paramref name="bottomArgb"/> at its bottom edge. Interpolates colour AND alpha per row, which
    /// is how the original bar bodies and sheens are shaded (analysis §A.1 colour table).
    /// </summary>
    public void FillRectVGradient(int x, int y, int w, int h, int topArgb, int bottomArgb)
    {
        if (w <= 0 || h <= 0) return;
        var x0 = Math.Max(0, x);
        var x1 = Math.Min(Width, x + w);
        if (x0 >= x1) return;

        for (var yy = Math.Max(0, y); yy < Math.Min(Height, y + h); yy++)
        {
            var t = h <= 1 ? 0.0 : (double)(yy - y) / (h - 1);
            var c = LerpArgb(topArgb, bottomArgb, t);
            var row = yy * Width;
            for (var xx = x0; xx < x1; xx++)
                BlendIndex(row + xx, c);
        }
    }

    /// <summary>
    /// Alpha-blended Bresenham line, clipped per-pixel. <paramref name="thickness"/> is the stroke width
    /// in pixels (1 = a single-pixel line); thicker strokes plot a filled square at each step so lines
    /// keep a consistent on-screen weight as the surface scales up.
    /// </summary>
    public void DrawLine(int x0, int y0, int x1, int y1, int argb, int thickness = 1)
    {
        var rad = Math.Max(0, (thickness - 1) / 2);
        var dx = Math.Abs(x1 - x0);
        var dy = -Math.Abs(y1 - y0);
        var sx = x0 < x1 ? 1 : -1;
        var sy = y0 < y1 ? 1 : -1;
        var err = dx + dy;

        while (true)
        {
            if (rad == 0) Blend(x0, y0, argb);
            else FillRect(x0 - rad, y0 - rad, 2 * rad + 1, 2 * rad + 1, argb);
            if (x0 == x1 && y0 == y1) break;
            var e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }

    /// <summary>
    /// Per-channel 8-bit ARGB lerp, matching <c>CTColor::InterpolateRGB</c> (mpvis <c>FUN_18000b518</c>)
    /// exactly: <c>from + round((to - from) * t)</c>, with the blend factor narrowed to a FLOAT and the
    /// rounding done in float by <see cref="MpvisRound"/>.
    ///
    /// It used to TRUNCATE a double product, which biases every interpolated channel downward by up to a
    /// whole level for the entire length of a transition — a systematic darkening of every stroke the
    /// effects draw, invisible in any single frame.
    /// </summary>
    public static int LerpArgb(int a, int b, double t)
    {
        var f = (float)t;
        var aa = Channel((a >> 24) & 0xFF, (b >> 24) & 0xFF, f);
        var rr = Channel((a >> 16) & 0xFF, (b >> 16) & 0xFF, f);
        var gg = Channel((a >> 8) & 0xFF, (b >> 8) & 0xFF, f);
        var bb = Channel(a & 0xFF, b & 0xFF, f);
        return (aa << 24) | (rr << 16) | (gg << 8) | bb;
    }

    private static int Channel(int from, int to, float t) =>
        (byte)(from + MpvisRound((to - from) * t));

    /// <summary>
    /// mpvis's rounding helper (<c>FUN_18000bef4</c>): round half AWAY FROM ZERO, computed in float, with
    /// exactly zero taking the negative branch. Every rounded quantity in the effect library goes through
    /// this rather than through a double-precision round, and the two disagree at every .5 boundary.
    /// </summary>
    public static int MpvisRound(float value) => (int)(value <= 0f ? value - 0.5f : value + 0.5f);
}
