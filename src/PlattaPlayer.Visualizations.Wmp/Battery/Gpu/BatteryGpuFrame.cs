using PlattaPlayer.Visualizations.Wmp.Battery.Shifts;

namespace PlattaPlayer.Visualizations.Wmp.Battery.Gpu;

/// <summary>One step of a recorded frame, in the order <see cref="BatteryCore"/> ran them.</summary>
public enum BatteryGpuOp
{
    /// <summary>Draw vertices <see cref="BatteryGpuCommand.First"/>.. as triangles, overwriting.</summary>
    Prims,

    /// <summary><c>dst = src[map]</c> through <see cref="BatteryGpuCommand.Table"/>.</summary>
    Gather,

    /// <summary>The plus blur that is also the fade.</summary>
    Blur,
}

/// <param name="Table">Gather: the shift whose table was used.</param>
/// <param name="Version">Gather: the shift's <see cref="MemoryEffect.Version"/> when it was used.</param>
/// <param name="Map">Gather: the exact field-resolution map (the live table or a transition step).</param>
/// <param name="Step">Gather: the transition step 0..17 the map is, or -1 for the finished table.</param>
public readonly record struct BatteryGpuCommand(
    BatteryGpuOp Op, int First = 0, int Count = 0, ShiftTable? Table = null, int Version = 0, int[]? Map = null, int Step = -1)
{
    /// <summary>A transition step's share of the way from the old table to the new one: (step + 1)/19,
    /// formed as the original forms it.</summary>
    public double TransitionFactor => Step < 0 ? 1.0 : (Step + 1) * BatteryMath.TransitionStep;
}

/// <summary>
/// Everything the GPU needs to replay one <see cref="BatteryCore.Render(BatteryLevels)"/>: the pixel stages
/// in order, the primitives they draw, and the palette the frame is presented with.
///
/// Primitives are triangles in FIELD units, three floats per vertex (x, y, palette index). Field row 0
/// is y = 0.
/// </summary>
public sealed class BatteryGpuFrame
{
    public const int FloatsPerVertex = 3;

    public float[] Vertices { get; private set; } = new float[FloatsPerVertex * 6 * 4096];

    public int VertexCount { get; private set; }

    public List<BatteryGpuCommand> Commands { get; } = [];

    /// <summary><see cref="BatteryCore.PresentedPalette"/> for this frame (PALETTEENTRY: R | G&lt;&lt;8 | B&lt;&lt;16).</summary>
    public uint[] Palette { get; } = new uint[256];

    /// <summary>False when the original fills the window with <see cref="StopFill"/> instead of the field.</summary>
    public bool Visible { get; set; }

    /// <summary><see cref="BatteryCore.StopFillColor"/>.</summary>
    public uint StopFill { get; set; }

    public void Clear()
    {
        VertexCount = 0;
        Commands.Clear();
    }

    /// <summary>An axis-aligned rectangle, x0 &lt; x1 and y0 &lt; y1.</summary>
    public void Rect(float x0, float y0, float x1, float y1, byte c) => Quad(x0, y0, x1, y0, x1, y1, x0, y1, c);

    /// <summary>A convex quad, its corners given in order round its edge.</summary>
    public void Quad(float ax, float ay, float bx, float by, float cx, float cy, float dx, float dy, byte c)
    {
        if ((VertexCount + 6) * FloatsPerVertex > Vertices.Length)
        {
            var grown = Vertices;
            Array.Resize(ref grown, Vertices.Length * 2);
            Vertices = grown;
        }

        var v = Vertices;
        var o = VertexCount * FloatsPerVertex;
        float col = c;
        v[o] = ax; v[o + 1] = ay; v[o + 2] = col;
        v[o + 3] = bx; v[o + 4] = by; v[o + 5] = col;
        v[o + 6] = cx; v[o + 7] = cy; v[o + 8] = col;
        v[o + 9] = ax; v[o + 10] = ay; v[o + 11] = col;
        v[o + 12] = cx; v[o + 13] = cy; v[o + 14] = col;
        v[o + 15] = dx; v[o + 16] = dy; v[o + 17] = col;
        VertexCount += 6;
    }
}
