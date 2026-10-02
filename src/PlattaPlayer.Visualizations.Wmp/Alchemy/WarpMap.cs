using System;
using System.Threading.Tasks;

namespace PlattaPlayer.Visualizations.Wmp.Alchemy;

/// <summary>
/// Builds the per-pixel displacement map (analysis §B.4): an <c>int[w*h]</c> of flattened source indices.
/// For every destination pixel the active effect(s) transform (x,y)→(srcX,srcY); an out-of-field result
/// is resolved by the kernel's own policy (see <see cref="Resolve"/>), and when two effects are active
/// the second is applied on top and the result blended 50% back toward the identity (no-op). Rows are
/// built in parallel — effect transforms only read per-frame state, so this is safe.
/// </summary>
public sealed class WarpMap
{
    public int[] Map { get; private set; } = Array.Empty<int>();

    private int _w;
    private int _h;

    public void Resize(int w, int h)
    {
        if (w == _w && h == _h) return;
        _w = w;
        _h = h;
        Map = new int[w * h];
    }

    public void Build(EffectContext ctx, AlchemyEffect? a, AlchemyEffect? b)
    {
        var w = _w;
        var h = _h;
        var map = Map;
        var hasB = b is { IsWarp: true };

        Parallel.For(0, h, y =>
        {
            var rowBase = y * w;
            var aToOrigin = a is { IsWarp: true } && a.OutOfRangeToOrigin;
            var bToOrigin = hasB && b!.OutOfRangeToOrigin;

            for (var x = 0; x < w; x++)
            {
                int sx = x, sy = y;
                if (a is { IsWarp: true }) a.Transform(ctx, ref sx, ref sy);
                sx = Resolve(sx, x, w, aToOrigin);
                sy = Resolve(sy, y, h, aToOrigin);

                if (hasB)
                {
                    int bx = sx, by = sy;
                    b!.Transform(ctx, ref bx, ref by);
                    bx = Resolve(bx, sx, w, bToOrigin);
                    by = Resolve(by, sy, h, bToOrigin);
                    sx = (bx + x) >> 1;
                    sy = (by + y) >> 1;
                }

                map[rowBase + x] = sy * w + sx;
            }
        });
    }

    /// <summary>
    /// Out-of-range policy for a warped coordinate, per kernel (FUN_18000d354, and identically in the
    /// incremental builder FUN_18000cd20):
    /// <code>
    ///   if ((uint)sx >= (uint)kernel->width) { sx = incoming; if (kernel->flag_0x52 == 0) sx = 0; }
    /// </code>
    /// The unsigned compare catches negatives and overruns together. Note the C comma operator in the
    /// original: the incoming coordinate is assigned FIRST and the flag test then overrides it, so a
    /// CLEARED flag selects the fixed fallback — which is (0,0) for every kernel in this build.
    ///
    /// Two kernels clear it (<see cref="AlchemyEffect.OutOfRangeToOrigin"/>), and for them an escaped
    /// source samples black, which is what drains the feedback field. The other two stand still.
    ///
    /// This used to WRAP unconditionally, which tiled the previous frame back in from the opposite edge
    /// and was the direct cause of our saturated output.
    /// </summary>
    private static int Resolve(int value, int incoming, int limit, bool toOrigin)
        => (uint)value < (uint)limit ? value : toOrigin ? 0 : incoming;

    /// <summary>
    /// One of the morph's 22 interpolation tables, as <c>BuildNextTable</c> (<c>0x18000cd20</c>) fills them
    /// while the next table builds. For each pixel, <c>(cx, cy)</c> is the source in the OLD table and
    /// <c>(sx, sy)</c> in the new one, and the entry is <c>(cy + M(sy − cy))·w + cx + M(sx − cx)</c>.
    /// <c>M</c> comes from <c>BuildMultiplicationTables</c> (<c>0x18000cbdc</c>):
    /// <c>(short)(int)((double)d · (double)((float)(index + 1) · (float)(1/23)))</c>. The decompile shows
    /// that product in float; the disassembly (<c>cvtps2pd</c> then <c>mulsd</c>, <c>cvttsd2si</c>) has
    /// the per-pixel multiply in DOUBLE, truncated.
    /// </summary>
    public static void Morph(int[] from, int[] to, int[] destination, int w, int index)
    {
        var factor = (double)((float)(index + 1) * (float)(1.0 / 23));
        Parallel.For(0, destination.Length / w, y =>
        {
            var row = y * w;
            for (var x = 0; x < w; x++)
            {
                var p = row + x;
                var old = (uint)from[p];
                var cy = (int)(old / (uint)w);
                var cx = (int)(old % (uint)w);
                var next = (uint)to[p];
                var sy = (int)(next / (uint)w);
                var sx = (int)(next % (uint)w);
                var my = (short)(int)((sy - cy) * factor);
                var mx = (short)(int)((sx - cx) * factor);
                destination[p] = (cy + my) * w + cx + mx;
            }
        });
    }
}
