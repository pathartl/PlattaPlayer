namespace PlattaPlayer.Visualizations.Wmp.Alchemy.Gpu;

/// <summary>
/// Receives the renderers' strokes and discs instead of having them rasterized into the field, so the GPU
/// path can redraw them at any resolution. It is set on <see cref="DrawPrimitives.Recorder"/>, and then
/// the renderers' own <see cref="AlchemyEffect.Draw"/> runs unchanged: the same spline points, the same
/// colours, and the same random draws as the exact CPU path. Only the final rasterization is swapped
/// out. Nothing a renderer decides depends on pixels already in the field, so skipping the plots changes
/// nothing upstream.
///
/// All coordinates are in FIELD units, where field pixel <c>(x, y)</c> covers <c>[x, x+1) × [y, y+1)</c>.
/// </summary>
public interface IStrokeRecorder
{
    /// <summary>
    /// One spline segment, from (<paramref name="x0"/>,<paramref name="y0"/>) to
    /// (<paramref name="x1"/>,<paramref name="y1"/>) at FLOAT precision: the points before the original
    /// rounds them to whole pixels. <paramref name="mode"/> is the plot mode, and
    /// <paramref name="neighbourAlpha"/> and <paramref name="clipMargin"/> are the primitives' state when
    /// the segment was drawn.
    /// </summary>
    void Segment(float x0, float y0, float x1, float y1, int colour, int mode, float neighbourAlpha, int clipMargin);

    /// <summary>One soft disc, exactly as <see cref="DrawPrimitives.Disc"/> receives it.</summary>
    void Disc(int cx, int cy, int radius, float alpha, int fill, int rim, bool invert);
}
