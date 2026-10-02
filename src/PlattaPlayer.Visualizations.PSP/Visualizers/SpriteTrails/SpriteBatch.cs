using System.Runtime.InteropServices;
using PlattaPlayer.Visualizations.PSP.Gu;
using static PlattaPlayer.Visualizations.PSP.Common.VisMath;
using static PlattaPlayer.Visualizations.PSP.Gu.GuConstants;

namespace PlattaPlayer.Visualizations.PSP.Visualizers;

// Drawing helpers owned by the sprite-trails visualizer (types 3 and 6), src/vis/sprite_trails/sprite_batch.*.

/// <summary>
/// One GU vertex of a sprite, vtype 0x19d = GU_TEXTURE_8BIT | GU_COLOR_8888 | GU_VERTEX_32BITF (0x14 bytes).
/// 8-bit texture coordinates are normalized by 128 on the GE, so 0x80 = 1.0.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct SpriteVertex
{
    public byte U;      // +0x00
    public byte V;      // +0x01
    public byte Pad0;   // +0x02
    public byte Pad1;   // +0x03
    public uint Color;  // +0x04  0xAABBGGRR
    public float X;     // +0x08
    public float Y;     // +0x0c
    public float Z;     // +0x10
}

/// <summary>
/// GU_SPRITES batch: each sprite is two vertices (top-left, bottom-right); the GE takes the colour of the
/// second vertex. Original class: 0xc bytes, vtable 0x14a30 = [0xf698 dtor, 0xf6a8 deleting dtor].
///
/// The vertex memory is owned by the caller and is NOT cleared between frames: <see cref="Reset"/> only
/// sets the count back to 0. The type 6 trail code reads colours that previous frames left in it (see
/// SpriteTrails.RenderDashedCurves), so the memory contents are part of the state.
/// </summary>
public sealed class SpriteBatch
{
    public const int VType = 0x19d;

    private readonly SpriteVertex[] _vertices; // +0x04
    private int _count;                        // +0x08  sprites

    /// <summary>0xf5d4</summary>
    public static int BufferBytes(int maxSprites) => maxSprites * 0x28;

    /// <summary>
    /// 0xf5e4: zeroes the memory, then sets uv (0,0) / (0x80,0x80) on every sprite. The original caller
    /// passes two more arguments (16, 16) that are ignored. <paramref name="vertices"/> must hold
    /// 2 * <paramref name="maxSprites"/> vertices.
    /// </summary>
    public SpriteBatch(int maxSprites, SpriteVertex[] vertices)
    {
        _count = 0;
        _vertices = vertices;
        Array.Clear(vertices, 0, maxSprites * 2); // sceKernelMemset(BufferBytes(maxSprites))
        for (var i = 0; i < maxSprites; i++)
        {
            ref var v0 = ref vertices[i * 2];
            ref var v1 = ref vertices[i * 2 + 1];
            v0.U = 0;
            v0.V = 0;
            v1.U = 0x80;
            v1.V = 0x80;
        }
    }

    /// <summary>0xf6d0 (always called with 0)</summary>
    public void Reset(int count) => _count = count;

    /// <summary>
    /// Appends one sprite (inlined at every call site in the original): writes v1.color, v0.x/y and
    /// v1.x/y, leaves uv and z untouched.
    /// </summary>
    public void Add(float x0, float y0, float x1, float y1, uint color)
    {
        ref var v0 = ref _vertices[_count * 2];
        ref var v1 = ref _vertices[_count * 2 + 1];
        v1.Color = color;
        v0.X = x0;
        v0.Y = y0;
        _count = _count + 1;
        v1.X = x1;
        v1.Y = y1;
    }

    /// <summary>Colour stored in the second vertex of sprite <paramref name="index"/> (whatever was last written there).</summary>
    public uint SpriteColor(int index) => _vertices[index * 2 + 1].Color;

    /// <summary>0xf6d8: sceGumDrawArray(GU_SPRITES, 0x19d, count*2, 0, vertices) when count > 0.</summary>
    public void Draw(GuContext gu)
    {
        if (_count > 0)
        {
            gu.GumDrawArray(GU_SPRITES, VType, _count * 2, new ReadOnlySpan<SpriteVertex>(_vertices, 0, _count * 2));
        }
    }

    public SpriteVertex[] Vertices => _vertices;

    /// <summary>Sprites added since the last <see cref="Reset"/>.</summary>
    public int Count => _count;
}

/// <summary>One vertex of the background quad: vtype 0x19c = GU_COLOR_8888 | GU_VERTEX_32BITF (0x10 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct BackgroundVertex
{
    public uint Color; // +0x00
    public float X;    // +0x04
    public float Y;    // +0x08
    public float Z;    // +0x0c
}

/// <summary>
/// Full-screen gradient quad behind the sprites. Original class: 0x14 bytes, vtable 0x147c8 =
/// [0x109c0 dtor, 0x109d0 deleting dtor]. The four vertices live in a global array in the original
/// (0x16400, 4 x 0x10 bytes) shared by every BackgroundQuad; here the array is passed in by the owner
/// (one per SpriteTrails instance). Drawn as a triangle strip centred on the origin of the controller's
/// current matrix.
/// </summary>
public sealed class BackgroundQuad
{
    private readonly BackgroundVertex[] _vertices; // global 0x16400 in the original
    private readonly uint[] _colors = new uint[4]; // +0x04..+0x10

    /// <summary>0xd5b8: also (re)initialises the shared vertex positions to +-282.353 x +-160.</summary>
    public BackgroundQuad(BackgroundVertex[] sharedVertices, uint c0, uint c1, uint c2, uint c3)
    {
        _vertices = sharedVertices;
        var halfW = BitsFloat(0x438d2d2d); // 282.35294 = 564.7059 / 2
        const float halfH = 160.0f;
        _vertices[0] = new BackgroundVertex { Color = 0, X = -halfW, Y = halfH, Z = 0.0f };
        _vertices[1] = new BackgroundVertex { Color = 0, X = halfW, Y = halfH, Z = 0.0f };
        _vertices[2] = new BackgroundVertex { Color = 0, X = -halfW, Y = -halfH, Z = 0.0f };
        _vertices[3] = new BackgroundVertex { Color = 0, X = halfW, Y = -halfH, Z = 0.0f };
        _colors[3] = c3;
        _colors[0] = c0;
        _colors[1] = c1;
        _colors[2] = c2;
    }

    /// <summary>
    /// 0xd670: colours of vertex 0 (-x,+y), 1 (+x,+y), 2 (-x,-y), 3 (+x,-y). Only the RGB bytes are used
    /// by <see cref="Draw"/>.
    /// </summary>
    public void SetColors(uint c0, uint c1, uint c2, uint c3)
    {
        _colors[3] = c3;
        _colors[0] = c0;
        _colors[1] = c1;
        _colors[2] = c2;
    }

    /// <summary>
    /// 0xd684: vertex colour = (int)(scale * channel) per RGB channel, alpha forced to 0xff;
    /// sceGuFrontFace(GU_CCW); sceGumDrawArray(GU_TRIANGLE_STRIP, 0x19c, 4, 0, vertices).
    /// </summary>
    public void Draw(GuContext gu, float scale)
    {
        for (var i = 0; i < 4; i++)
        {
            _vertices[i].Color = ScaleColor(_colors[i], scale);
        }
        // sceKernelDcacheWritebackRange(vertices, 0x40): not needed here.
        gu.FrontFace(GU_CCW);
        gu.GumDrawArray(GU_TRIANGLE_STRIP, GU_COLOR_8888 | GU_VERTEX_32BITF | GU_TRANSFORM_3D, 4,
            new ReadOnlySpan<BackgroundVertex>(_vertices, 0, 4));
    }

    // (int)(scale * channel) for each RGB byte of 0x00BBGGRR, alpha forced to 0xff.
    // trunc.w.s results are combined without masking (scale is in [0, 1]).
    private static uint ScaleColor(uint c, float scale)
    {
        var r = (int)(scale * (float)(c & 0xff));
        var g = (int)(scale * (float)(c >> 8 & 0xff));
        var b = (int)(scale * (float)(c >> 16 & 0xff));
        return (uint)(b << 16 | g << 8 | r) | 0xff000000u;
    }
}
