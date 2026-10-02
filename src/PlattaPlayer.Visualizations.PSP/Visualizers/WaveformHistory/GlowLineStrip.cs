using System.Runtime.InteropServices;
using PlattaPlayer.Visualizations.PSP.Gu;
using static PlattaPlayer.Visualizations.PSP.Common.VisMath;
using static PlattaPlayer.Visualizations.PSP.Gu.GuConstants;

namespace PlattaPlayer.Visualizations.PSP.Visualizers;

/// <summary>vtype 0x183 = GU_TEXTURE_32BITF | GU_VERTEX_32BITF (0x14 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct GlowVertex
{
    public float U, V;    // +0x00
    public float X, Y, Z; // +0x08
}

/// <summary>
/// Textured "glow" line strip used by the waveform history visualizer (type 4). Original class: size 0x38,
/// vtable 0x14a08 = [0xda80 dtor, 0xda90 deleting dtor]. Port of src/vis/waveform_history/glow_line_strip.
///
/// A polyline is turned into a ribbon 8 units wide (+-4 along the normal). Each vertex is (u, v, x, y, z)
/// floats; u runs 0 -> 1 across the ribbon (the glow texture's profile is along u), v is a constant supplied
/// by the caller (0.5). Drawn as GU_TRIANGLES: every interior point adds 12 vertices (one quad for the
/// segment that ends there, plus a 2-triangle miter/bevel joint), the end point adds 6 (closing quad).
///
/// The vertex memory is a caller-owned slice of a shared pool (the original's raw pointer becomes
/// array + offset).
/// </summary>
public sealed class GlowLineStrip
{
    public const int VertsPerSegment = 12; // 0xf0 bytes
    public const float HalfWidth = 4.0f;

    private readonly GlowVertex[] _pool; // +0x04 vertices (pool + offset)
    private readonly int _base;
    private int _segments;               // +0x08
    private float _lastX;                // +0x0c  previous point
    private float _lastY;                // +0x10
    private float _edgeAX;               // +0x14  pending ribbon edge at the previous point ("+normal" side, u = 0)
    private float _edgeAY;               // +0x18
    private float _edgeBX;               // +0x1c  pending ribbon edge ("-normal" side, u = 1)
    private float _edgeBY;               // +0x20
    private float _lastV;                // +0x24
    private float _normalX;              // +0x28  (-dirY, dirX) of the previous segment
    private float _normalY;              // +0x2c
    private float _dirX;                 // +0x30  unit direction of the previous segment
    private float _dirY;                 // +0x34

    /// <summary>0xda14: bytes needed for <paramref name="segments"/> segments: segments*0xf0 + 0x78 (end cap).</summary>
    public static int BufferBytes(int segments) => segments * 0xf0 + 0x78;

    /// <summary>
    /// 0xda28: <paramref name="pool"/>[<paramref name="offset"/>..] is caller-owned memory; zeroes
    /// BufferBytes(maxSegments) bytes of it. The original leaves the other fields uninitialised (operator
    /// new memory); they start at zero here.
    /// </summary>
    public GlowLineStrip(int maxSegments, GlowVertex[] pool, int offset)
    {
        _pool = pool;
        _base = offset;
        _segments = 0;
        // sceKernelMemset
        pool.AsSpan(offset, BufferBytes(maxSegments) / Marshal.SizeOf<GlowVertex>()).Clear();
    }

    private static void SetVertex(ref GlowVertex vtx, float u, float v, float x, float y)
    {
        vtx.U = u;
        vtx.V = v;
        vtx.X = x;
        vtx.Y = y;
        vtx.Z = 0.0f;
    }

    /// <summary>
    /// 0xdab8: start a new strip with the first segment (x0,y0) -> (x1,y1). <paramref name="z0"/> arrives in
    /// $f14 but is never read. Resets the segment count.
    /// </summary>
    public void Begin(float x0, float y0, float z0, float x1, float y1, float v)
    {
        _ = z0;
        var dx = x1 - x0;
        var ndy = -(y1 - y0);
        _segments = 0;
        _lastX = x1;
        _lastY = y1;
        var len = Sqrt(ndy * ndy + dx * dx);
        if (len < 0.0001f)
        {
            _normalY = 0.0f;
            _normalX = 1.0f;
            return; // lastV, edges and dir keep their old values
        }
        _lastV = v;
        var inv = 1.0f / len;
        dx = dx * inv;   // = dirX
        ndy = ndy * inv; // = -dirY
        _dirX = dx;
        _edgeBY = (float)(int)(y0 - dx * 4.0f);
        _edgeAX = (float)(int)(x0 + ndy * 4.0f);
        _edgeAY = (float)(int)(y0 + dx * 4.0f);
        _edgeBX = (float)(int)(x0 - ndy * 4.0f);
        _dirY = -ndy;
        _normalX = ndy;
        _normalY = dx;
    }

    /// <summary>0xdbdc: append a point (calls AddSegment; counts it if it was long enough).</summary>
    public void AddPoint(float x, float y, float v)
    {
        if (AddSegment(x, y, v, _segments))
        {
            _segments = _segments + 1;
        }
    }

    /// <summary>
    /// 0xdd68: writes the 12 vertices of segment <paramref name="index"/>; returns false (writes nothing) when
    /// the point is closer than 0.0001 to the previous one.
    /// </summary>
    private bool AddSegment(float x, float y, float v, int index)
    {
        var ex = x - _lastX;
        var ey = y - _lastY;
        var len = Sqrt(ex * ex + ey * ey);
        if (len < 0.0001f) return false;

        var inv = 1.0f / len;
        ey = ey * inv;
        ex = ex * inv;
        // cosine of the turn angle between the previous and the new segment
        var c = _dirX * ex + _dirY * ey;

        var k = 0.0f; // how far the joint is pulled back along the previous direction
        var bend = (0.0f < c) ? (c < 0.999f) : (-0.999f < c);
        if (bend)
        {
            k = (1.0f - c) * 4.0f;
            k = k * Rsqrt(1.0f - c * c);
        }
        // (when !bend the original still multiplies 0 * dir, kept for -0/NaN parity)
        var py = _lastY - k * _dirY;
        var px = _lastX - k * _dirX;

        var n4y = _normalY * 4.0f;
        var n4x = _normalX * 4.0f;
        var side = ex * _normalX + ey * _normalY; // >0: turning towards +normal
        var bY = py - n4y;        // f27  -normal side at the joint
        var fy = _lastY + k * ey; // f23  point pushed forward along the new direction
        var bX = px - n4x;        // f26
        var fx = _lastX + k * ex; // f20
        var aX = n4x + px;        // f28  +normal side at the joint
        var aY = n4y + py;        // f29

        var h = (0.0f < c) ? (c * 0.5f + 0.5f) : (0.5f - c * 0.5f);
        var s = Sqrt(h);
        s = s + s;

        float mx, my, qx, qy;
        if (0.0f < side)
        {
            qx = (fx + fx) - aX;
            qy = (fy + fy) - aY;
            mx = s * (_lastX - aX) + aX;
            my = s * (_lastY - aY) + aY;
        }
        else
        {
            qx = (fx + fx) - bX;
            qy = (fy + fy) - bY;
            mx = s * (_lastX - bX) + bX;
            my = s * (_lastY - bY) + bY;
        }

        var o = _pool.AsSpan(_base + index * VertsPerSegment, VertsPerSegment);
        // quad from the pending edges to the joint
        SetVertex(ref o[0], 0.0f, _lastV, _edgeAX, _edgeAY);
        SetVertex(ref o[1], 1.0f, v, bX, bY);
        SetVertex(ref o[2], 1.0f, _lastV, _edgeBX, _edgeBY);
        SetVertex(ref o[3], 0.0f, _lastV, _edgeAX, _edgeAY);
        SetVertex(ref o[4], 0.0f, v, aX, aY);
        SetVertex(ref o[5], 1.0f, v, bX, bY);
        if (0.0f < side)
        {
            SetVertex(ref o[6], 1.0f, v, bX, bY);
            SetVertex(ref o[7], 0.0f, v, aX, aY);
            SetVertex(ref o[8], 1.0f, v, mx, my);
            SetVertex(ref o[9], 0.0f, v, aX, aY);
            SetVertex(ref o[10], 1.0f, v, qx, qy);
            SetVertex(ref o[11], 1.0f, v, mx, my);
            _edgeAX = aX;
            _edgeAY = aY;
            _edgeBX = qx;
            _edgeBY = qy;
        }
        else
        {
            SetVertex(ref o[6], 1.0f, v, bX, bY);
            SetVertex(ref o[7], 0.0f, v, aX, aY);
            SetVertex(ref o[8], 0.0f, v, mx, my);
            SetVertex(ref o[9], 1.0f, v, bX, bY);
            SetVertex(ref o[10], 0.0f, v, mx, my);
            SetVertex(ref o[11], 0.0f, v, qx, qy);
            _edgeAX = qx;
            _edgeAY = qy;
            _edgeBX = bX;
            _edgeBY = bY;
        }
        _lastV = v;
        _lastX = x;
        _lastY = y;
        _normalX = -ey;
        _normalY = ex;
        _dirX = ex;
        _dirY = ey;
        return true;
    }

    /// <summary>0xdc14: close the strip at (x, y) (writes 6 vertices, does not count a segment).</summary>
    public void End(float x, float y, float v)
    {
        var o = _pool.AsSpan(_base + _segments * VertsPerSegment, 6);
        var n4y = _normalY * 4.0f;
        var n4x = _normalX * 4.0f;
        var bY = y - n4y;
        var bX = x - n4x;
        var aX = n4x + x;
        var aY = n4y + y;
        SetVertex(ref o[0], 0.0f, _lastV, _edgeAX, _edgeAY);
        SetVertex(ref o[1], 1.0f, v, bX, bY);
        SetVertex(ref o[2], 1.0f, _lastV, _edgeBX, _edgeBY);
        SetVertex(ref o[3], 0.0f, _lastV, _edgeAX, _edgeAY);
        SetVertex(ref o[4], 0.0f, v, aX, aY);
        SetVertex(ref o[5], 1.0f, v, bX, bY);
    }

    /// <summary>0xdd24: sceGumDrawArray(GU_TRIANGLES, 0x183, segments*12 + 6, 0, vertices) if any segment.</summary>
    public void Draw(GuContext gu)
    {
        if (_segments > 0)
        {
            var count = _segments * 12 + 6;
            gu.GumDrawArray<GlowVertex>(GU_TRIANGLES, GU_TEXTURE_32BITF | GU_VERTEX_32BITF, count,
                _pool.AsSpan(_base, count));
        }
    }
}
