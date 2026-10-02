namespace PlattaPlayer.Visualizations.MilkDrop;

/// <summary>
/// The per-pixel warp grid the renderer draws each frame: a regular lattice of
/// (<c>MeshX+1</c>)×(<c>MeshY+1</c>) vertices spanning the screen. Each vertex carries a fixed clip-space
/// position and a per-frame source UV computed by MilkDrop's per-pixel warp (zoom/rotate/stretch/warp the
/// previous frame). The renderer samples the previous-frame texture at the interpolated UV, which is what
/// produces the classic flowing feedback — far richer than a single global transform.
///
/// Layout is interleaved <c>[x, y, u, v]</c> per vertex so a single buffer upload feeds the GPU. Vertex
/// positions never change, so they are filled once; only the UVs are rewritten each frame.
/// </summary>
public sealed class WarpMesh
{
    /// <summary>Vertices per row (<c>MeshX + 1</c>).</summary>
    public int Cols { get; }

    /// <summary>Vertices per column (<c>MeshY + 1</c>).</summary>
    public int Rows { get; }

    /// <summary>Interleaved <c>[x, y, u, v]</c> for every vertex (row-major, bottom-up).</summary>
    public float[] Vertices { get; }

    /// <summary>Triangle-list indices (two triangles per cell); static for a given grid size.</summary>
    public ushort[] Indices { get; }

    public int VertexCount => Cols * Rows;
    public int IndexCount => Indices.Length;

    public WarpMesh(int meshX, int meshY)
    {
        Cols = meshX + 1;
        Rows = meshY + 1;
        Vertices = new float[Cols * Rows * 4];

        // Fixed clip-space positions: i/meshX,j/meshY mapped to -1..1 (y up). UVs are written per frame.
        for (var j = 0; j < Rows; j++)
        {
            for (var i = 0; i < Cols; i++)
            {
                var n = (j * Cols + i) * 4;
                Vertices[n] = (float)(i / (double)meshX * 2.0 - 1.0);
                Vertices[n + 1] = (float)(j / (double)meshY * 2.0 - 1.0);
            }
        }

        Indices = BuildIndices(meshX, meshY, Cols);
    }

    private static ushort[] BuildIndices(int meshX, int meshY, int cols)
    {
        var indices = new ushort[meshX * meshY * 6];
        var k = 0;
        for (var j = 0; j < meshY; j++)
        {
            for (var i = 0; i < meshX; i++)
            {
                var v00 = (ushort)(j * cols + i);
                var v10 = (ushort)(j * cols + i + 1);
                var v01 = (ushort)((j + 1) * cols + i);
                var v11 = (ushort)((j + 1) * cols + i + 1);

                indices[k++] = v00; indices[k++] = v10; indices[k++] = v11;
                indices[k++] = v00; indices[k++] = v11; indices[k++] = v01;
            }
        }
        return indices;
    }
}
