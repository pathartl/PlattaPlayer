using PlattaPlayer.Visualizations.MilkDrop;

namespace PlattaPlayer.Visualizations.MilkDrop.Plugin;

/// <summary>
/// One independently-rendered preset: its own <see cref="MilkdropEngine"/>, feedback ping-pong targets,
/// composited output texture, and (for MilkDrop2 presets) translated warp/comp shader programs. The
/// visualizer keeps two of these so it can run the outgoing and incoming presets side by side and
/// crossfade between them. All GL handles are owned and managed by the visualizer; this type is just the
/// per-preset state bundle.
/// </summary>
internal sealed class MilkLayer
{
    public readonly MilkdropEngine Engine = new();

    // Feedback ping-pong (previous frame in TexA, this frame written to TexB, then swapped).
    public int TexA, TexB, FboA, FboB;

    // This layer's composited result for the current frame (what the present step samples).
    public int OutTex, OutFbo;

    public int FbWidth, FbHeight;

    // Per-preset translated shaders (null when the preset is classic or translation failed).
    public MilkShaderProgram? WarpShader;
    public MilkShaderProgram? CompShader;

    public int ShaderFrame;
}
