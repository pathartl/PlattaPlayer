using System;

namespace PlattaPlayer.Visualizations.Wmp.Alchemy.Gpu;

/// <summary>
/// Converts the engine's warp parameters from FIELD units to DEVICE pixels for the feedback shader,
/// and keeps the warp the morph starts from.
///
/// The shader runs each kernel as if the original had allocated a field the size of the window, with
/// every length (shifts, stripe widths, radii, centres, pull) multiplied by the scale. Angles, zoom
/// factors and loop counts have no unit and pass through. The kernels keep their integer truncations,
/// now at device-pixel granularity, so feedback samples whole pixels and picks up no resampling blur.
///
/// The constant per-frame shifts need care. Linear scrolls by a whole number of pixels per frame and
/// Snafu pushes its stripes by one, so a 1-unit shift at a scale of 2.25 is 2.25 pixels. Truncated
/// every frame, that moves content 11% too slowly. Instead each kernel carries the remainder from frame
/// to frame (2, 2, 2, 3, ...), so the average speed is exact. At a scale of 1 every shift is already an
/// integer and the carry changes nothing.
/// </summary>
public sealed class GpuWarpScaler
{
    // Device-unit layouts. Indices past the field layout are additions the shader reads.
    //   Linear  0 falloff, 1 xShift (whole px, carried), 2 yShift (carried), 3 zx, 4 zy, 5 fallDir,
    //           6 sinShake, 7 sinLoops, 8 shear amplitude from yShift (yShift*3*s), 9 from xShift.
    //   Snafu   0 vertical, 1 width*s, 2 speed (whole px, carried).
    //   Stretch 0 cx, 1 cy (whole px), 2 pull*s, 3 maxR*s, 4 rotation, 5 sinShake, 6 sinLoops.
    //   OScope  0 reflectionMode, 1 shiftMode, 2 spin, 3 centerR*s, 4 littleR*s, 5 boxSize*s,
    //           6 focalX, 7 focalY (whole px), 8 halfDiagonal*s, 9 (littleR+2)*s, 10 2*centerR/littleR.

    private struct Carry
    {
        public object? Source;
        public GpuWarpKind Kind;
        public double X;
        public double Y;

        /// <summary>Restart at a half, so the first frame ROUNDS the scaled shift.</summary>
        public void Track(object? source, GpuWarpKind kind)
        {
            if (ReferenceEquals(Source, source) && Kind == kind) return;
            Source = source;
            Kind = kind;
            X = 0.5;
            Y = 0.5;
        }

        /// <summary>The whole-pixel step this frame for a shift of <paramref name="v"/> device pixels.</summary>
        public static float Step(ref double acc, double v)
        {
            var before = Math.Floor(acc);
            acc += v;
            var step = Math.Floor(acc) - before;
            // Keep the accumulator small; only its fraction matters.
            acc -= before;
            return (float)step;
        }
    }

    private Carry _a, _aChild, _b, _bChild;
    private int _promotionsSeen;

    /// <summary>This frame's warp, in device units.</summary>
    public GpuWarpSet Current { get; } = new();

    /// <summary>The warp a morph blends away from: the last frame's <see cref="Current"/> before the promotion.</summary>
    public GpuWarpSet From { get; } = new();

    public void Reset()
    {
        Current.Clear();
        From.Clear();
        _a = _aChild = _b = _bChild = default;
    }

    /// <summary>Convert <paramref name="frame"/>'s warp for a device field scaled by <paramref name="scale"/>.</summary>
    public void Update(AlchemyGpuFrame frame, double scale)
    {
        // A promotion swaps in new kernels. Last frame's warp belonged to the old ones, so freeze it
        // before it is overwritten. The CPU engine does the same with its map.
        if (frame.Promotions != _promotionsSeen)
        {
            _promotionsSeen = frame.Promotions;
            From.CopyFrom(Current);
        }

        var field = frame.Warp;
        Current.HasB = field.HasB;
        Convert(field.A, Current.A, scale, ref _a, ref _aChild);
        if (field.HasB) Convert(field.B, Current.B, scale, ref _b, ref _bChild);
        else Current.B.Clear();
    }

    private static void Convert(GpuWarpSlot src, GpuWarpSlot dst, double s, ref Carry carry, ref Carry childCarry)
    {
        dst.Kind = src.Kind;
        dst.ToOrigin = src.ToOrigin;
        dst.ChildKind = src.ChildKind;
        dst.Source = src.Source;
        carry.Track(src.Source, src.Kind);
        childCarry.Track(src.Source, src.ChildKind);
        ConvertParams(src.Kind, src.Params, dst.Params, s, ref carry);
        ConvertParams(src.ChildKind, src.ChildParams, dst.ChildParams, s, ref childCarry);
    }

    private static void ConvertParams(GpuWarpKind kind, float[] p, float[] d, double s, ref Carry carry)
    {
        Array.Clear(d);
        switch (kind)
        {
            case GpuWarpKind.Linear:
                d[0] = p[0];
                d[1] = Carry.Step(ref carry.X, p[1] * s);
                d[2] = Carry.Step(ref carry.Y, p[2] * s);
                d[3] = p[3];
                d[4] = p[4];
                d[5] = p[5];
                d[6] = p[6];
                d[7] = p[7];
                d[8] = (float)(p[2] * 3 * s);
                d[9] = (float)(p[1] * 3 * s);
                break;

            case GpuWarpKind.Snafu:
                d[0] = p[0];
                d[1] = (float)(p[1] * s);
                d[2] = Carry.Step(ref carry.X, p[2] * s);
                break;

            case GpuWarpKind.Stretch:
                d[0] = (float)Math.Round(p[0] * s);
                d[1] = (float)Math.Round(p[1] * s);
                d[2] = (float)(p[2] * s);
                d[3] = (float)(p[3] * s);
                d[4] = p[4];
                d[5] = p[5];
                d[6] = p[6];
                break;

            case GpuWarpKind.OScope:
                d[0] = p[0];
                d[1] = p[1];
                d[2] = p[2];
                d[3] = (float)(p[3] * s);
                d[4] = (float)(p[4] * s);
                d[5] = (float)(p[5] * s);
                d[6] = (float)Math.Round(p[6] * s);
                d[7] = (float)Math.Round(p[7] * s);
                d[8] = (float)(p[8] * s);
                d[9] = (float)((p[4] + 2) * s);
                d[10] = p[4] != 0 ? (float)((p[3] + (double)p[3]) / p[4]) : 0f;
                break;
        }
    }
}
