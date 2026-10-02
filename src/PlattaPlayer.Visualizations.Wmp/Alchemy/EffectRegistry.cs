using System;
using System.Collections.Generic;
using PlattaPlayer.Visualizations.Wmp.Alchemy.Effects;

namespace PlattaPlayer.Visualizations.Wmp.Alchemy;

/// <summary>
/// Factory for the Alchemy effects. The original builds every effect ONCE at start-up and afterwards only
/// re-randomizes them when a scheduler slot selects them, so this hands out persistent instances; the
/// per-effect selection weights and categories live with the pool in <see cref="EffectScheduler"/>.
/// </summary>
public sealed class EffectRegistry
{
    /// <summary>
    /// The four shift kernel TYPES, in a stable order the harness pins against
    /// (<c>AlchemyCore.Pin</c> / <c>warp-profile</c>): 0 Linear, 1 Snafu, 2 Stretch, 3 OScope.
    /// </summary>
    private readonly Func<AlchemyEffect>[] _warpTypes =
    [
        () => new LinearShift(),
        () => new SnafuShift(),
        () => new StretchShift(),
        () => new OScopeShift(),
    ];

    private static readonly Type[] WarpTypeClasses =
        [typeof(LinearShift), typeof(SnafuShift), typeof(StretchShift), typeof(OScopeShift)];

    /// <summary>
    /// The warp stage's ten kernel instances, as TYPE indices in the exact order
    /// <c>CToleranceEffects::AddShiftEffect</c> adds them: four OScope, two Linear, two Stretch, two Snafu.
    /// <c>SelectRandomShiftEffect</c> draws an INDEX into this array, so the order matters as much as
    /// the counts — with a shared rand() script, a different order picks a different kernel.
    /// Distribution: OScope 40%, Linear / Stretch / Snafu 20% each.
    /// </summary>
    private static readonly int[] KernelInstanceTypes = [3, 3, 3, 3, 0, 0, 2, 2, 1, 1];

    private readonly Func<Random, AlchemyEffect>[] _draws =
    [
        r => new SuperStarRender(r),
        r => new AtomBallsRender(r),
        r => new WonderWaveRender(r),
    ];

    /// <summary>Number of distinct kernel TYPES — the index space the harness pins against.</summary>
    public int WarpCount => _warpTypes.Length;

    /// <summary>Create one kernel by TYPE index (diagnostics, and the harness's pinned profiling).</summary>
    public AlchemyEffect CreateWarp(int index, Random random)
    {
        var effect = _warpTypes[index]();
        effect.Randomize(random);
        return effect;
    }

    /// <summary>True if <paramref name="kernel"/> is of registry kernel type <paramref name="type"/>.</summary>
    public static bool IsWarpType(int type, AlchemyEffect kernel) =>
        (uint)type < (uint)WarpTypeClasses.Length && kernel.GetType() == WarpTypeClasses[type];

    /// <summary>
    /// The warp stage with its ten persistent kernel instances, built in <c>AddShiftEffect</c>'s order.
    /// The OScope constructor (<c>0x18000df64</c>) runs its own randomizer, so building the four of them
    /// consumes draws. The other kernels' constructors draw nothing (harness <c>probe-ctors</c>).
    /// </summary>
    public WarpStage CreateWarpStage(Random random)
    {
        var kernels = new List<AlchemyEffect>(KernelInstanceTypes.Length);
        foreach (var type in KernelInstanceTypes)
        {
            var kernel = _warpTypes[type]();
            if (kernel is OScopeShift) kernel.Randomize(random);
            kernels.Add(kernel);
        }
        return new WarpStage(kernels, IsWarpType);
    }

    /// <summary>Renderer TYPES in registry order: 0 SuperStar, 1 AtomBalls, 2 WonderWave.</summary>
    public int DrawCount => _draws.Length;

    /// <summary>A renderer instance, NOT yet randomized (its slot randomizes it on selection).</summary>
    public AlchemyEffect NewDraw(int index, Random random) => _draws[index](random);

    /// <summary>A renderer instance, randomized once (diagnostics).</summary>
    public AlchemyEffect CreateDraw(int index, Random random)
    {
        var effect = NewDraw(index, random);
        effect.Randomize(random);
        return effect;
    }

    /// <summary>The bass-bounce zoom, NOT yet randomized.</summary>
    public AlchemyEffect NewBassBounce() => new BassBounceZoom();
}
