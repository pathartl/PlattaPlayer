using System.Runtime.InteropServices;

namespace PlattaPlayer.Visualizations.PSP.Gu;

/// <summary>
/// One vertex as the renderer receives it: already transformed to clip space (perspective divide left to
/// the GPU so interpolation stays perspective-correct), texture coordinates normalised with the texture
/// offset/scale applied, and the colour resolved (vertex colour or the material colour). 28 bytes.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct GuVertex
{
    public float X, Y, Z, W;
    public float U, V;

    /// <summary>0xAABBGGRR, i.e. bytes R, G, B, A in memory (the GE's order).</summary>
    public uint Color;
}

/// <summary>What a recorded primitive is, in GL terms. GU_SPRITES are expanded to triangles when recorded.</summary>
public enum GuPrimitive
{
    Points,
    Lines,
    LineStrip,
    Triangles,
    TriangleStrip,
    TriangleFan,
}

public enum GuCommandKind
{
    /// <summary>sceGuClear: fill the colour buffer with <see cref="GuCommand.ClearColor"/>.</summary>
    Clear,

    /// <summary>A draw of <see cref="GuCommand.Count"/> vertices from <see cref="GuCommand.First"/>.</summary>
    Draw,
}

/// <summary>The fixed-function state a draw was issued with (GE semantics, not GL).</summary>
public readonly record struct GuDrawState(
    bool Blend,
    int BlendOp,
    int BlendSrc,
    int BlendDst,
    uint BlendSrcFix,
    uint BlendDstFix,
    bool AlphaTest,
    int AlphaFunc,
    int AlphaRef,
    bool Textured,
    int TexFunc,
    int TexColorComponent);

public struct GuCommand
{
    public GuCommandKind Kind;
    public uint ClearColor;
    public int ClearFlags;
    public GuPrimitive Primitive;
    public int First;
    public int Count;
    public GuDrawState State;

    /// <summary>The bound texture when <see cref="GuDrawState.Textured"/>.</summary>
    public GuTexture? Texture;
}

/// <summary>
/// A texture as the renderer sees it: linear 0xAABBGGRR pixels (row pitch = <see cref="Width"/>) and a
/// version that changes whenever the pixels do, so a GPU copy can be refreshed lazily.
/// </summary>
public sealed class GuTexture
{
    private static int _nextId;

    internal GuTexture(int width, int height, uint[] pixels)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    /// <summary>Stable identity for the renderer's GPU-side cache.</summary>
    public int Id { get; } = Interlocked.Increment(ref _nextId);

    public int Width { get; internal set; }

    public int Height { get; internal set; }

    public uint[] Pixels { get; internal set; }

    public int Version { get; internal set; }
}

/// <summary>Everything one Render() issued, in order. Owned and reused by the <see cref="GuContext"/>.</summary>
public sealed class GuFrame
{
    private GuVertex[] _vertices = new GuVertex[4096];

    public List<GuCommand> Commands { get; } = new();

    public GuVertex[] Vertices => _vertices;

    public int VertexCount { get; private set; }

    internal void Reset()
    {
        Commands.Clear();
        VertexCount = 0;
    }

    internal Span<GuVertex> Allocate(int count, out int first)
    {
        if (VertexCount + count > _vertices.Length)
            Array.Resize(ref _vertices, Math.Max(_vertices.Length * 2, VertexCount + count));
        first = VertexCount;
        VertexCount += count;
        return _vertices.AsSpan(first, count);
    }
}
