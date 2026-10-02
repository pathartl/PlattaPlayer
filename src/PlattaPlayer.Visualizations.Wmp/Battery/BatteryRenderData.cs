using PlattaPlayer.Visualizations.Wmp.Battery.Effects;
using PlattaPlayer.Visualizations.Wmp.Battery.Shifts;

namespace PlattaPlayer.Visualizations.Wmp.Battery;

/// <summary>
/// <c>CRenderData</c>: the field (two surfaces and a front/back pair), its size, and the shift and
/// render-effect pools in the order the constructor builds them.
/// </summary>
public sealed class BatteryRenderData
{
    /// <summary>Battery's resolution table <c>{256x192, 384x288, 512x384}</c> (.data 0x1808ec9f0). The
    /// windowed effect uses index 1.</summary>
    public static readonly (int W, int H)[] Resolutions = [(256, 192), (384, 288), (512, 384)];

    public BatteryRenderData(CrtRand rand, int width = 384, int height = 288)
    {
        W = width;
        H = height;
        Front = new BatterySurface(width, height);
        Back = new BatterySurface(width, height);
        Raster = new CpuRaster(this);
        Shifts = ShiftTable.CreatePool();
        Effects = RenderEffect.CreatePool(rand); // CJDar and CGalaxy draw in their constructors
    }

    /// <summary>+0x5c/+0x60.</summary>
    public int W { get; private set; }

    public int H { get; private set; }

    /// <summary>The per-frame pixel stages. <see cref="CpuRaster"/> unless the GPU path replaces it.</summary>
    public IBatteryRaster Raster { get; set; }

    /// <summary>Where the effects draw: the front surface on the CPU path.</summary>
    public IBatteryCanvas Canvas => Raster.Canvas;

    /// <summary>+0xb8: the latest image. Effects draw into it, and it is what is presented.</summary>
    public BatterySurface Front { get; private set; }

    /// <summary>+0xc0: the previous image.</summary>
    public BatterySurface Back { get; private set; }

    /// <summary>+0x5a: passed to <see cref="ShiftTable.SetSize"/>. When false, no warp morphs are generated
    /// or played.</summary>
    public bool UseTransitions { get; set; }

    public BatteryPalette Palette { get; } = new();

    public ShiftTable[] Shifts { get; }

    public RenderEffect[] Effects { get; }

    public void Swap() => (Front, Back) = (Back, Front);

    /// <summary>The resolution change in <c>AllocateSurfaces</c>: new (erased) surfaces at the new size.</summary>
    public void Resize(int width, int height)
    {
        W = width;
        H = height;
        Front = new BatterySurface(width, height);
        Back = new BatterySurface(width, height);
    }

    /// <summary><c>CRenderData::FindShiftTable</c>: the first pool entry with that class name.</summary>
    public ShiftTable? FindShift(string name) =>
        Array.Find(Shifts, s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary><c>CRenderData::FindRenderEffect</c>.</summary>
    public RenderEffect? FindEffect(string name) =>
        Array.Find(Effects, e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
}
