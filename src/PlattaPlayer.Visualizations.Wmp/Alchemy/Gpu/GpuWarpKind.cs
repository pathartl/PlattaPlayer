namespace PlattaPlayer.Visualizations.Wmp.Alchemy.Gpu;

/// <summary>
/// Identifies which shift (warp) kernel a GPU-exported Alchemy warp effect represents. The numeric values
/// are the same ids the GLSL feedback shader switches on (<c>uWarpAKind</c>/<c>uWarpBKind</c>), so the
/// engine and the shader agree on the per-pixel transform without sharing code. <see cref="None"/> (0)
/// means "no warp" (identity), which the shader treats as a pass-through. The kernels mirror the real
/// <c>mpvis.dll</c> Alchemy shift classes: CTShiftLinear, CTShiftSnafu, CTShiftStretch,
/// CToleranceShiftOScope. (Id 5 was the CTRenderBassBounce zoom, removed: nothing in mpvis reads its rect.)
/// </summary>
public enum GpuWarpKind
{
    None = 0,
    Linear = 1,
    Snafu = 2,
    Stretch = 3,
    OScope = 4,
}
