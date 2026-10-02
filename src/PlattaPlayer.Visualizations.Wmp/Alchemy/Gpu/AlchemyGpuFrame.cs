namespace PlattaPlayer.Visualizations.Wmp.Alchemy.Gpu;

/// <summary>
/// Everything one advanced frame hands the GPU, in FIELD units: the warp the feedback pass reads, the
/// morph state, and the strokes and discs to draw on top. <see cref="AlchemyGpuEngine"/> fills it and
/// the host converts it to device pixels (<see cref="GpuWarpScaler"/>). Reused every frame.
/// </summary>
public sealed class AlchemyGpuFrame
{
    public int FieldWidth;
    public int FieldHeight;

    /// <summary>
    /// False when no warp kernel was in force. The CPU engine then skips the feedback pass altogether,
    /// so the field is left as it was and only the overlay is drawn.
    /// </summary>
    public bool HasWarp;

    /// <summary>The kernels in force this frame (<see cref="WarpMap.Build"/>'s inputs).</summary>
    public readonly GpuWarpSet Warp = new();

    /// <summary>
    /// <see cref="WarpStage.Promotions"/>. When it changes, the previous frame's warp becomes the one a
    /// morph blends away from.
    /// </summary>
    public int Promotions;

    /// <summary>True while the warp stage is morphing from the frozen warp to <see cref="Warp"/>.</summary>
    public bool Morphing;

    /// <summary>Which of the 22 interpolation steps the morph is showing (<see cref="WarpMap.Morph"/>).</summary>
    public int MorphIndex;

    public readonly AlchemyStrokeList Strokes = new();

    public string CurrentName = "";
}
