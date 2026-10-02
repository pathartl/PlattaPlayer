using System;

namespace PlattaPlayer.Visualizations.Wmp.Alchemy.Gpu;

/// <summary>
/// Implemented by every Alchemy warp effect so its per-frame parameters can be uploaded to the GPU
/// feedback shader instead of running the per-pixel <see cref="AlchemyEffect.Transform"/> on the CPU.
/// The effect's whole scheduling/randomization machinery still runs on the CPU each frame (it is cheap);
/// only the per-pixel work moves to GLSL. <see cref="WriteParams"/> must read the same fields
/// <see cref="AlchemyEffect.Transform"/> reads (after <see cref="AlchemyEffect.Tick"/> has run) and must
/// not consume the engine RNG, so the GPU path stays in lock-step with the CPU reference for a fixed seed.
/// </summary>
public interface IGpuWarp
{
    /// <summary>The transform id the GLSL shader switches on for this effect.</summary>
    GpuWarpKind WarpKind { get; }

    /// <summary>
    /// Writes this effect's current frame parameters into <paramref name="dst"/> (in the layout the shader
    /// expects for <see cref="WarpKind"/>) and returns the number of floats written. Must not draw the RNG.
    /// </summary>
    int WriteParams(Span<float> dst, EffectContext ctx);

    /// <summary>
    /// The kernel this one DELEGATES to, if any. Only <c>CToleranceShiftOScope</c> has one: it embeds a
    /// Stretch, a Linear and a Snafu kernel and hands folded coordinates to whichever its ShiftMode
    /// selects, so the shader needs a second parameter block to evaluate it.
    /// </summary>
    GpuWarpKind ChildKind => GpuWarpKind.None;

    /// <summary>Parameters for <see cref="ChildKind"/>, in that kernel's own layout.</summary>
    int WriteChildParams(Span<float> dst, EffectContext ctx) => 0;
}

/// <summary>Shared constants for the GPU warp parameter upload.</summary>
public static class GpuWarp
{
    /// <summary>Floats reserved per warp slot (the GLSL <c>uWarpA[]</c>/<c>uWarpB[]</c> arrays).</summary>
    public const int ParamCapacity = 12;
}
