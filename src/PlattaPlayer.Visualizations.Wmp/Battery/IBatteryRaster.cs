using PlattaPlayer.Visualizations.Wmp.Battery.Shifts;

namespace PlattaPlayer.Visualizations.Wmp.Battery;

/// <summary>
/// <c>CRenderData</c>'s per-frame pixel stages: the warp gather and the plus blur. The default
/// <see cref="CpuRaster"/> runs them on the field. The GPU path records them for replay.
/// </summary>
public interface IBatteryRaster
{
    /// <summary>Where the effects draw this frame.</summary>
    IBatteryCanvas Canvas { get; }

    /// <summary><c>CShiftTransform::Perform</c> after GetData: swap, then <c>dst[i] = src[map[i]]</c>.
    /// <paramref name="table"/> is the shift that produced <paramref name="map"/> (its live table or one of
    /// its transition steps).</summary>
    void Gather(ShiftTable table, int[] map);

    /// <summary><c>CPlusBlur::Perform</c>: swap, then the five-tap blur that is also the fade.</summary>
    void Blur();
}

/// <summary>The original's pixel stages, on the render data's two CPU surfaces.</summary>
public sealed class CpuRaster(BatteryRenderData rd) : IBatteryRaster
{
    public IBatteryCanvas Canvas => rd.Front;

    public void Gather(ShiftTable table, int[] map)
    {
        rd.Swap();
        var dst = rd.Front.Bits;
        var src = rd.Back.Bits;
        var n = rd.W * rd.H;
        for (var i = 0; i < n; i++) dst[i] = src[(uint)map[i]];
    }

    public void Blur()
    {
        rd.Swap();
        PlusBlur.Perform(rd.Back.Bits, rd.Front.Bits, rd.W, rd.H);
    }
}
