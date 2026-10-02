using System.Threading.Tasks;

namespace PlattaPlayer.Visualizations.Wmp.Alchemy;

/// <summary>
/// The feedback gather + blur (analysis §B.5, <c>FUN_18000d940</c>): displaces the previous frame through
/// the warp map to make the motion trails, then runs the engine's weighted neighbour blur
/// (<c>(3·average(up,down,left,right) + centre) / 4</c> per channel). The persistence/fade is emergent —
/// it comes from edge loss and the slight contraction of most transforms, exactly as in the DLL; there is
/// no explicit decay multiply.
/// </summary>
public static class FeedbackPass
{
    /// <summary>
    /// Gathers <paramref name="frame"/> through <paramref name="map"/> into <paramref name="scratch"/>,
    /// then blurs <paramref name="scratch"/> back into <paramref name="frame"/>. On return the new
    /// frame is in <paramref name="frame"/> and <paramref name="scratch"/> holds the displaced copy.
    ///
    /// The original does the same thing by a route that reads like a bug and is not: it swaps the render
    /// data's two surface pointers, gathers into the new "current", then swaps them BACK before blurring.
    /// Net effect, no swap — the finished frame ends up in the surface the renderers draw into and the
    /// other is pure scratch.
    /// </summary>
    public static void GatherAndDecay(int[] frame, int[] scratch, int[] map, int w, int h, int edgeColor)
    {
        var count = w * h;

        // The whole pass is gated on the pixel count being a multiple of four — the gather is unrolled by
        // four with no remainder loop, and on a count that does not divide the original does NOTHING (it
        // still leaves its two surface pointers swapped, which we have no equivalent of). Unreachable in
        // practice because the field is a fixed 640x480, but it is measured behaviour, not a guess:
        // verify-feedback drives 30x25 through the real function and gets an untouched frame back.
        if ((count & 3) != 0) return;

        for (var i = 0; i < count; i++)
            scratch[i] = frame[map[i]];

        // The blur is a FLAT WALK, not a two-dimensional interior loop. It runs from the first pixel of
        // row 1 to the last pixel of row H-2 without ever looking at a column index, so at x = 0 the
        // "left" neighbour is the last pixel of the previous ROW and at x = W-1 the "right" neighbour is
        // the first pixel of the next one. This used to skip both border columns and copy them through
        // unblurred, which left two columns of every frame permanently undecayed.
        Parallel.For(1, h - 1, y =>
        {
            var row = y * w;
            for (var x = 0; x < w; x++)
            {
                var i = row + x;
                frame[i] = Blur(scratch[i - w], scratch[i + w], scratch[i - 1], scratch[i + 1], scratch[i]);
            }
        });

        // Edge rows take the edge colour, so displaced pixels bleed into darkness at the borders — but
        // only W-1 pixels of each. FUN_1800125e0 is a general line filler and is handed the row length as
        // a Bresenham STEP COUNT, so it plots x = 0 .. W-2 and the final pixel of rows 0 and H-1 keeps
        // whatever it already held. Two pixels a frame, and the only reason to reproduce it is that a
        // verifier which tolerates two pixels tolerates the next two as well.
        var last = (h - 1) * w;
        for (var x = 0; x < w - 1; x++)
        {
            frame[x] = edgeColor;
            frame[last + x] = edgeColor;
        }
    }

    // Per channel: (3 * average(up,down,left,right) + centre) / 4.
    private static int Blur(int up, int dn, int le, int ri, int ce)
    {
        var r = Channel(up, dn, le, ri, ce, 16);
        var g = Channel(up, dn, le, ri, ce, 8);
        var b = Channel(up, dn, le, ri, ce, 0);
        return unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
    }

    private static int Channel(int up, int dn, int le, int ri, int ce, int shift)
    {
        var avg = ((((up >> shift) & 0xFF) + ((dn >> shift) & 0xFF)
                  + ((le >> shift) & 0xFF) + ((ri >> shift) & 0xFF)) >> 2);
        return ((avg * 3) + ((ce >> shift) & 0xFF)) >> 2;
    }
}
