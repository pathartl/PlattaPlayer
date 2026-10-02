using System;
using System.Collections.Generic;

namespace PlattaPlayer.Visualizations.Wmp.Alchemy.Gpu;

/// <summary>
/// One frame's strokes and discs, in the order the renderers drew them, ready to upload. Consecutive
/// segments share a <see cref="Batch"/>, and a disc breaks the run, because draw order is part of the
/// look: each primitive blends over whatever came before it.
///
/// A segment becomes a parallelogram (two triangles) in FIELD units. It is three cells thick across the
/// line (the row before, the line, the row after, weighted by <see cref="StrokeProfile"/>) and runs along
/// the major axis over exactly the cells the original's walk would plot. Consecutive segments share
/// their end edge, so a spline tiles into one seamless band with no overlaps or gaps.
/// </summary>
public sealed class AlchemyStrokeList : IStrokeRecorder
{
    /// <summary>
    /// Floats per vertex: field x, y; the offset across the line in cells (−1.5..1.5); colour r, g, b
    /// (0..1); the three profile weights; and the clip margin.
    /// </summary>
    public const int FloatsPerVertex = 10;

    private const int VerticesPerSegment = 6;

    /// <summary>A run of segments (<see cref="Count"/> vertices from <see cref="Start"/>), or one disc.</summary>
    public readonly record struct Batch(bool IsDisc, int Start, int Count, DiscCommand Disc);

    /// <summary>A disc with the invert already applied to <see cref="Fill"/>.</summary>
    public readonly record struct DiscCommand(int Cx, int Cy, int Radius, float Alpha, int Fill, int Rim);

    private readonly List<Batch> _batches = [];
    private float[] _vertices = new float[FloatsPerVertex * VerticesPerSegment * 1024];
    private int _vertexCount;
    private int _runStart = -1;

    /// <summary>Interleaved segment vertices, <see cref="FloatsPerVertex"/> each.</summary>
    public float[] Vertices => _vertices;

    public int VertexCount => _vertexCount;

    public IReadOnlyList<Batch> Batches => _batches;

    public void Clear()
    {
        _batches.Clear();
        _vertexCount = 0;
        _runStart = -1;
    }

    /// <summary>Close the open run of segments. Call once after the engine has drawn everything.</summary>
    public void Finish()
    {
        if (_runStart >= 0 && _vertexCount > _runStart)
            _batches.Add(new Batch(false, _runStart, _vertexCount - _runStart, default));
        _runStart = -1;
    }

    public void Disc(int cx, int cy, int radius, float alpha, int fill, int rim, bool invert)
    {
        Finish();
        // The ONE'S COMPLEMENT of the fill, as DrawPrimitives.Disc applies it.
        if (invert) fill = unchecked((int)0xFF000000) | (0xFFFFFF - (fill & 0xFFFFFF));
        _batches.Add(new Batch(true, 0, 0, new DiscCommand(cx, cy, radius, alpha, fill, rim)));
    }

    public void Segment(float x0, float y0, float x1, float y1, int colour, int mode, float neighbourAlpha, int clipMargin)
    {
        var dx = x1 - x0;
        var dy = y1 - y0;
        // The walk's axis choice, tie included: at exactly 45 degrees it runs along Y.
        var xMajor = Math.Abs(dy) < Math.Abs(dx);
        var (m0, n0, dm, dn) = xMajor ? (x0, y0, dx, dy) : (y0, x0, dy, dx);
        if (dm == 0f) return;

        // The walk plots the start point and stops one short of the end, so the run covers the cells
        // [m0, m1) going forward and (m1, m0] going backward. Cell c spans [c, c + 1).
        var shift = dm > 0 ? 0f : 1f;
        var start = m0 + shift;
        var end = m0 + dm + shift;
        var slope = dn / dm;
        // The centre line through the plotted cells' centres.
        var lineStart = n0 + 0.5f + slope * (start - m0 - 0.5f);
        var lineEnd = n0 + 0.5f + slope * (end - m0 - 0.5f);

        var (before, on, after) = StrokeProfile.For(mode, neighbourAlpha, xMajor);
        var r = ((colour >> 16) & 0xFF) / 255f;
        var g = ((colour >> 8) & 0xFF) / 255f;
        var b = (colour & 0xFF) / 255f;

        EnsureCapacity((_vertexCount + VerticesPerSegment) * FloatsPerVertex);
        if (_runStart < 0) _runStart = _vertexCount;

        void Put(float major, float line, float across)
        {
            var i = _vertexCount * FloatsPerVertex;
            var cross = line + across;
            _vertices[i] = xMajor ? major : cross;
            _vertices[i + 1] = xMajor ? cross : major;
            _vertices[i + 2] = across;
            _vertices[i + 3] = r;
            _vertices[i + 4] = g;
            _vertices[i + 5] = b;
            _vertices[i + 6] = before;
            _vertices[i + 7] = on;
            _vertices[i + 8] = after;
            _vertices[i + 9] = clipMargin;
            _vertexCount++;
        }

        Put(start, lineStart, -1.5f);
        Put(end, lineEnd, -1.5f);
        Put(start, lineStart, 1.5f);
        Put(start, lineStart, 1.5f);
        Put(end, lineEnd, -1.5f);
        Put(end, lineEnd, 1.5f);
    }

    private void EnsureCapacity(int floats)
    {
        if (floats <= _vertices.Length) return;
        var n = _vertices.Length * 2;
        while (n < floats) n *= 2;
        Array.Resize(ref _vertices, n);
    }
}
