using System.Buffers.Binary;
using System.Runtime.InteropServices;
using static PlattaPlayer.Visualizations.PSP.Gu.GuConstants;

namespace PlattaPlayer.Visualizations.PSP.Gu;

/// <summary>
/// The subset of PSPSDK libgu/libgum that visualizer_plugin.prx calls (through paf.prx's exports, which
/// have the same semantics), ported from the decompilation's OpenGL host (src/host/gl_gu.cpp).
///
/// Nothing is drawn here. Each call either updates the emulated GE state or appends a draw to the
/// current <see cref="GuFrame"/>, with the vertices decoded, transformed to clip space and their colour
/// and texture coordinates resolved, so the renderer only has to replay them. Method names drop the
/// <c>sceGu</c> prefix; the libgum ones keep a <c>Gum</c> prefix.
///
/// GE semantics worth knowing (all handled here or in the renderer):
/// <list type="bullet">
/// <item>Colours are 0xAABBGGRR (bytes R, G, B, A in memory).</item>
/// <item>Blend source factor 0/1 means DST_COLOR / 1-DST_COLOR, destination 0/1 means SRC_COLOR /
/// 1-SRC_COLOR, 10 = the fixed colour.</item>
/// <item>Vertex components are ordered weights, texture, colour, normal, position; each is aligned to
/// its own element size and the vertex to the largest one.</item>
/// <item>A GU_SPRITES pair takes its colour and z from the second vertex.</item>
/// </list>
/// The camera is paf's: a perspective view of a 480x272 virtual screen at distance 236 (near 10,
/// far 1e6), y up, with the libgum model matrix on top. Through-mode (GU_TRANSFORM_2D) vertices are
/// screen pixels, y down.
/// </summary>
public sealed class GuContext
{
    public const int ScreenWidth = 480;
    public const int ScreenHeight = 272;

    private static readonly int[] ElementSize = [0, 1, 2, 4];

    private readonly GuFrame _frame = new();
    private readonly Mat4 _projection;
    private readonly Mat4 _view;
    private readonly List<Mat4> _modelStack = [Mat4.Identity];
    private readonly bool[] _states = new bool[22];
    private readonly Dictionary<PafSurface, GuTexture> _surfaceTextures = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<uint[], GuTexture> _rawTextures = new(ReferenceEqualityComparer.Instance);
    private DecodedVertex[] _decoded = new DecodedVertex[256];
    private uint[] _linear = new uint[1024];

    private uint _material = 0xffffffff;
    private uint _clearColor;
    private int _tfx = GU_TFX_MODULATE;
    private int _tcc = GU_TCC_RGBA;
    private int _texPsm = GU_PSM_8888;
    private int _texSwizzle;
    private uint[]? _texPixels;
    private int _texWidth, _texHeight, _texBufferWidth;
    private PafSurface? _texSurface;
    private float _texScaleU = 1, _texScaleV = 1, _texOffsetU, _texOffsetV;
    private int _blendOp = GU_ADD, _blendSrc = GU_SRC_ALPHA, _blendDst = GU_ONE_MINUS_SRC_ALPHA;
    private uint _blendSrcFix, _blendDstFix;
    private int _alphaFunc = GU_ALWAYS, _alphaRef;
    private int _patchU = 1, _patchV = 1;
    private int _frontFace = GU_CW;

    public GuContext()
    {
        _projection = Mat4.Perspective(136.0f / 236.0f, 480.0f / 272.0f, 10.0f, 1000000.0f); // paf 0x13092c
        _view = Mat4.Identity;
        _view.M[14] = -236.0f; // camera at (0, 0, 236) looking down -z
    }

    /// <summary>The commands recorded since the last <see cref="BeginFrame"/>.</summary>
    public GuFrame Frame => _frame;

    /// <summary>The front face last set (recorded for completeness; culling is never enabled).</summary>
    public int FrontFaceOrder => _frontFace;

    /// <summary>
    /// Resets the GE state to what paf has set when it calls a visualizer (host GuBeginFrame): identity
    /// model matrix, white material, standard alpha blending, MODULATE/RGBA, unit texture scale. Starts a
    /// new frame.
    /// </summary>
    public void BeginFrame()
    {
        _frame.Reset();
        _modelStack.Clear();
        _modelStack.Add(Mat4.Identity);
        _material = 0xffffffff;
        _texSurface = null;
        _texPixels = null;
        Array.Clear(_states);
        Enable(GU_BLEND);
        BlendFunc(GU_ADD, GU_SRC_ALPHA, GU_ONE_MINUS_SRC_ALPHA, 0, 0);
        TexFunc(GU_TFX_MODULATE, GU_TCC_RGBA);
        TexScale(1, 1);
        TexOffset(0, 0);
        _alphaFunc = GU_ALWAYS;
        _alphaRef = 0;
    }

    // ================================================================ libgu

    public void Enable(int state)
    {
        if ((uint)state < (uint)_states.Length) _states[state] = true;
    }

    public void Disable(int state)
    {
        if ((uint)state < (uint)_states.Length) _states[state] = false;
    }

    public void AlphaFunc(int func, int value, int mask)
    {
        _alphaFunc = func & 7;
        _alphaRef = value & mask & 0xff;
    }

    public void BlendFunc(int op, int src, int dest, uint srcFix, uint destFix)
    {
        _blendOp = op;
        _blendSrc = src;
        _blendDst = dest;
        _blendSrcFix = srcFix;
        _blendDstFix = destFix;
    }

    public void ClearColor(uint color) => _clearColor = color;

    public void Clear(int flags)
    {
        if ((flags & (GU_COLOR_BUFFER_BIT | GU_STENCIL_BUFFER_BIT)) == 0) return;
        _frame.Commands.Add(new GuCommand { Kind = GuCommandKind.Clear, ClearColor = _clearColor, ClearFlags = flags });
    }

    public void Color(uint color) => _material = color;

    /// <summary>sceGuCopyImage for 32-bit (and 16-bit) images held in managed arrays: a plain copy at the
    /// time it is issued (what the GE's transfer amounts to here).</summary>
    public void CopyImage(int psm, int sx, int sy, int width, int height, int srcw, ReadOnlySpan<uint> src,
                          int dx, int dy, int destw, Span<uint> dest)
    {
        if (psm != GU_PSM_8888) throw new NotSupportedException("Only 8888 image copies are emulated.");
        for (var y = 0; y < height; y++)
            src.Slice((sy + y) * srcw + sx, width).CopyTo(dest.Slice((dy + y) * destw + dx, width));
    }

    public void DrawArray<T>(int prim, int vtype, int count, ReadOnlySpan<T> vertices) where T : unmanaged =>
        Draw(prim, vtype, count, MemoryMarshal.AsBytes(vertices));

    public void FrontFace(int order) => _frontFace = order;

    public void PatchDivide(uint ulevel, uint vlevel)
    {
        _patchU = (int)ulevel;
        _patchV = (int)vlevel;
    }

    /// <summary>sceGuPatchPrim. Patches are always tessellated into triangles here.</summary>
    public void PatchPrim(int prim)
    {
    }

    /// <summary>sceGuScissor. No visualizer enables the scissor test, so this is a no-op.</summary>
    public void Scissor(int x, int y, int w, int h)
    {
    }

    public void TexFlush()
    {
    }

    public void TexFunc(int tfx, int tcc)
    {
        _tfx = tfx;
        _tcc = tcc;
    }

    /// <summary>sceGuTexImage with the texture in a managed array (only level 0 is used).</summary>
    public void TexImage(int mipmap, int width, int height, int tbw, uint[] pixels)
    {
        if (mipmap != 0) return;
        _texSurface = null;
        _texPixels = pixels;
        _texWidth = width;
        _texHeight = height;
        _texBufferWidth = tbw;
    }

    /// <summary>sceGuTexMapMode. Environment mapping is not used by the reachable visualizers.</summary>
    public void TexMapMode(int mode, uint a1, uint a2)
    {
    }

    public void TexMode(int tpsm, int maxmips, int a2, int swizzle)
    {
        _texPsm = tpsm;
        _texSwizzle = swizzle;
    }

    public void TexOffset(float u, float v)
    {
        _texOffsetU = u;
        _texOffsetV = v;
    }

    public void TexScale(float u, float v)
    {
        _texScaleU = u;
        _texScaleV = v;
    }

    // ================================================================ libgum (operates on the model matrix)

    public void GumDrawArray<T>(int prim, int vtype, int count, ReadOnlySpan<T> vertices) where T : unmanaged =>
        Draw(prim, vtype, count, MemoryMarshal.AsBytes(vertices));

    /// <summary>
    /// sceGumDrawBezier (paf 8ABBA32A, emits GE PRIM 0x05): cubic Bezier patches sharing edges, ucount =
    /// 3*pu+1 and vcount = 3*pv+1 control points, each patch subdivided by <see cref="PatchDivide"/>.
    /// Evaluated on the CPU and recorded as triangles.
    /// </summary>
    public void GumDrawBezier<T>(int vtype, int ucount, int vcount, ReadOnlySpan<T> vertices) where T : unmanaged
    {
        var cp = Decode(vtype, ucount * vcount, MemoryMarshal.AsBytes(vertices));
        int pu = (ucount - 1) / 3, pv = (vcount - 1) / 3;
        int du = Math.Max(1, _patchU), dv = Math.Max(1, _patchV);
        var hasUv = (vtype & GU_TEXTURE_BITS) != 0;
        var grid = new DecodedVertex[(du + 1) * (dv + 1)];
        Span<float> bu = stackalloc float[4];
        Span<float> bv = stackalloc float[4];

        var texture = _states[GU_TEXTURE_2D] ? ResolveTexture() : null;
        var mvp = CurrentMvp();
        var command = BeginDraw(GuPrimitive.Triangles, texture);
        var count = 0;
        var start = _frame.VertexCount;

        for (var py = 0; py < pv; py++)
        for (var px = 0; px < pu; px++)
        {
            for (var j = 0; j <= dv; j++)
            for (var i = 0; i <= du; i++)
            {
                Bernstein((float)i / du, bu);
                Bernstein((float)j / dv, bv);
                var r = new DecodedVertex { HasColor = cp[0].HasColor };
                float cr = 0, cg = 0, cb = 0, ca = 0;
                for (var b = 0; b < 4; b++)
                for (var a = 0; a < 4; a++)
                {
                    ref readonly var p = ref cp[(py * 3 + b) * ucount + px * 3 + a];
                    var w = bu[a] * bv[b];
                    r.X += p.X * w; r.Y += p.Y * w; r.Z += p.Z * w;
                    r.U += p.U * w; r.V += p.V * w;
                    cr += (p.Color & 0xff) * w;
                    cg += (p.Color >> 8 & 0xff) * w;
                    cb += (p.Color >> 16 & 0xff) * w;
                    ca += (p.Color >> 24) * w;
                }
                r.Color = PackColor(cr, cg, cb, ca);
                if (!hasUv)
                {
                    // the GE generates patch UVs when none are given
                    r.U = (px + (float)i / du) / pu;
                    r.V = (py + (float)j / dv) / pv;
                }
                grid[j * (du + 1) + i] = r;
            }

            for (var j = 0; j < dv; j++)
            for (var i = 0; i < du; i++)
            {
                var out6 = _frame.Allocate(6, out _);
                ref readonly var v00 = ref grid[j * (du + 1) + i];
                ref readonly var v10 = ref grid[j * (du + 1) + i + 1];
                ref readonly var v01 = ref grid[(j + 1) * (du + 1) + i];
                ref readonly var v11 = ref grid[(j + 1) * (du + 1) + i + 1];
                out6[0] = Emit(v00, v00.U, v00.V, false, mvp, texture);
                out6[1] = Emit(v01, v01.U, v01.V, false, mvp, texture);
                out6[2] = Emit(v10, v10.U, v10.V, false, mvp, texture);
                out6[3] = Emit(v10, v10.U, v10.V, false, mvp, texture);
                out6[4] = Emit(v01, v01.U, v01.V, false, mvp, texture);
                out6[5] = Emit(v11, v11.U, v11.V, false, mvp, texture);
                count += 6;
            }
        }

        command.First = start;
        command.Count = count;
        if (count > 0) _frame.Commands.Add(command);
    }

    public void GumLoadIdentity() => _modelStack[^1] = Mat4.Identity;

    public void GumPushMatrix() => _modelStack.Add(_modelStack[^1]);

    public void GumPopMatrix()
    {
        if (_modelStack.Count > 1) _modelStack.RemoveAt(_modelStack.Count - 1);
    }

    public void GumScale(float x, float y, float z)
    {
        var s = Mat4.Identity;
        s.M[0] = x; s.M[5] = y; s.M[10] = z;
        _modelStack[^1] = _modelStack[^1] * s;
    }

    public void GumTranslate(float x, float y, float z)
    {
        var t = Mat4.Identity;
        t.M[12] = x; t.M[13] = y; t.M[14] = z;
        _modelStack[^1] = _modelStack[^1] * t;
    }

    // ================================================================ paf

    /// <summary>
    /// paf_Surface_SetTexture (0xD1CF54D2): binds the surface as the current GU texture and sets the
    /// texture offset/scale.
    /// </summary>
    public void SetTexture(PafSurface? surface, float offsetU, float offsetV, float scaleU, float scaleV)
    {
        if (surface is null) return;
        _texSurface = surface;
        _texPixels = null;
        // A surface that is still locked may have been written since it was last bound.
        if (surface.LockCount > 0) surface.Touch();
        TexOffset(offsetU, offsetV);
        TexScale(scaleU, scaleV);
    }

    // ================================================================ recording

    private void Draw(int prim, int vtype, int count, ReadOnlySpan<byte> data)
    {
        if (count <= 0 || data.IsEmpty) return;
        if ((vtype & (GU_INDEX_BITS | GU_WEIGHT_BITS)) != 0)
            throw new NotSupportedException("Indexed and weighted vertices are not emulated.");

        var through = (vtype & GU_TRANSFORM_2D) != 0;
        var vs = Decode(vtype, count, data);
        var texture = (vtype & GU_TEXTURE_BITS) != 0 && _states[GU_TEXTURE_2D] ? ResolveTexture() : null;
        var mvp = through ? default : CurrentMvp();

        if (prim == GU_SPRITES)
        {
            var command = BeginDraw(GuPrimitive.Triangles, texture);
            var sprites = count / 2;
            var output = _frame.Allocate(sprites * 6, out command.First);
            command.Count = sprites * 6;
            for (var i = 0; i < sprites; i++)
            {
                ref readonly var a = ref vs[i * 2];
                ref readonly var b = ref vs[i * 2 + 1];
                // colour and z come from the second vertex
                var p0 = b; p0.X = a.X; p0.Y = a.Y;
                var p1 = b; p1.X = b.X; p1.Y = a.Y;
                var p2 = b;
                var p3 = b; p3.X = a.X; p3.Y = b.Y;
                var o = output.Slice(i * 6, 6);
                o[0] = Emit(p0, a.U, a.V, through, mvp, texture);
                o[1] = Emit(p1, b.U, a.V, through, mvp, texture);
                o[2] = Emit(p2, b.U, b.V, through, mvp, texture);
                o[3] = o[0];
                o[4] = o[2];
                o[5] = Emit(p3, a.U, b.V, through, mvp, texture);
            }
            if (command.Count > 0) _frame.Commands.Add(command);
            return;
        }

        var primitive = prim switch
        {
            GU_POINTS => GuPrimitive.Points,
            GU_LINES => GuPrimitive.Lines,
            GU_LINE_STRIP => GuPrimitive.LineStrip,
            GU_TRIANGLES => GuPrimitive.Triangles,
            GU_TRIANGLE_STRIP => GuPrimitive.TriangleStrip,
            GU_TRIANGLE_FAN => GuPrimitive.TriangleFan,
            _ => throw new NotSupportedException($"GU primitive {prim}"),
        };
        var cmd = BeginDraw(primitive, texture);
        var outVerts = _frame.Allocate(count, out cmd.First);
        cmd.Count = count;
        for (var i = 0; i < count; i++)
            outVerts[i] = Emit(vs[i], vs[i].U, vs[i].V, through, mvp, texture);
        _frame.Commands.Add(cmd);
    }

    private GuCommand BeginDraw(GuPrimitive primitive, GuTexture? texture) => new()
    {
        Kind = GuCommandKind.Draw,
        Primitive = primitive,
        Texture = texture,
        State = new GuDrawState(
            _states[GU_BLEND], _blendOp, _blendSrc, _blendDst, _blendSrcFix, _blendDstFix,
            _states[GU_ALPHA_TEST], _alphaFunc, _alphaRef,
            texture is not null, _tfx, _tcc),
    };

    private GuVertex Emit(in DecodedVertex v, float u, float tv, bool through, in Mat4 mvp, GuTexture? texture)
    {
        var o = new GuVertex { Color = v.HasColor ? v.Color : _material };
        if (through)
        {
            o.X = v.X / (ScreenWidth / 2f) - 1f;
            o.Y = 1f - v.Y / (ScreenHeight / 2f);
            o.Z = 0f;
            o.W = 1f;
        }
        else
        {
            var m = mvp.M;
            o.X = m[0] * v.X + m[4] * v.Y + m[8] * v.Z + m[12];
            o.Y = m[1] * v.X + m[5] * v.Y + m[9] * v.Z + m[13];
            o.Z = m[2] * v.X + m[6] * v.Y + m[10] * v.Z + m[14];
            o.W = m[3] * v.X + m[7] * v.Y + m[11] * v.Z + m[15];
        }
        if (texture is not null)
        {
            // through-mode UVs are texels
            if (through)
            {
                u /= texture.Width;
                tv /= texture.Height;
            }
            o.U = _texOffsetU + _texScaleU * u;
            o.V = _texOffsetV + _texScaleV * tv;
        }
        return o;
    }

    private Mat4 CurrentMvp() => _projection * (_view * _modelStack[^1]);

    private GuTexture? ResolveTexture()
    {
        if (_texSurface is { } surface)
        {
            if (!_surfaceTextures.TryGetValue(surface, out var st))
            {
                st = new GuTexture(surface.Width, surface.Height, surface.Pixels);
                _surfaceTextures[surface] = st;
            }
            st.Version = surface.Version;
            return st;
        }

        if (_texPixels is not { } pixels || _texPsm != GU_PSM_8888 || _texWidth <= 0 || _texHeight <= 0) return null;

        var count = _texWidth * _texHeight;
        if (_linear.Length < count) _linear = new uint[count];
        var linear = _linear.AsSpan(0, count);
        Linearize(pixels, linear);

        if (!_rawTextures.TryGetValue(pixels, out var tex))
        {
            tex = new GuTexture(_texWidth, _texHeight, linear.ToArray());
            _rawTextures[pixels] = tex;
            return tex;
        }
        if (tex.Width != _texWidth || tex.Height != _texHeight)
        {
            tex.Width = _texWidth;
            tex.Height = _texHeight;
            tex.Pixels = linear.ToArray();
            tex.Version++;
        }
        else if (!linear.SequenceEqual(tex.Pixels))
        {
            linear.CopyTo(tex.Pixels);
            tex.Version++;
        }
        return tex;
    }

    // 32-bit textures; swizzled = 16-byte (4 pixel) x 8 row blocks, row-major over the block grid.
    private void Linearize(uint[] src, Span<uint> dst)
    {
        int w = _texWidth, h = _texHeight, pitch = _texBufferWidth;
        if (_texSwizzle == 0)
        {
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = y * pitch + x;
                dst[y * w + x] = i < src.Length ? src[i] : 0;
            }
            return;
        }

        var s = 0;
        for (var by = 0; by < h; by += 8)
        for (var bx = 0; bx < pitch; bx += 4)
        for (var y = 0; y < 8; y++, s += 4)
        {
            if (by + y >= h) continue;
            for (var k = 0; k < 4; k++)
            {
                var x = bx + k;
                if (x < w && s + k < src.Length) dst[(by + y) * w + x] = src[s + k];
            }
        }
    }

    private ReadOnlySpan<DecodedVertex> Decode(int vtype, int count, ReadOnlySpan<byte> data)
    {
        int tex = vtype & 3, col = vtype >> 2 & 7, nrm = vtype >> 5 & 3, pos = vtype >> 7 & 3;
        var through = (vtype & GU_TRANSFORM_2D) != 0;
        int off = 0, maxA = 1;
        int tOff = -1, cOff = -1, pOff = -1;
        if (tex != 0) { off = Align(off, ElementSize[tex]); tOff = off; off += ElementSize[tex] * 2; maxA = Math.Max(maxA, ElementSize[tex]); }
        if (col != 0) { var s = col == 7 ? 4 : 2; off = Align(off, s); cOff = off; off += s; maxA = Math.Max(maxA, s); }
        if (nrm != 0) { off = Align(off, ElementSize[nrm]); off += ElementSize[nrm] * 3; maxA = Math.Max(maxA, ElementSize[nrm]); }
        if (pos != 0) { off = Align(off, ElementSize[pos]); pOff = off; off += ElementSize[pos] * 3; maxA = Math.Max(maxA, ElementSize[pos]); }
        var stride = Align(off, maxA);

        if (_decoded.Length < count) _decoded = new DecodedVertex[Math.Max(count, _decoded.Length * 2)];
        var output = _decoded.AsSpan(0, count);
        for (var i = 0; i < count; i++)
        {
            var p = data.Slice(i * stride, stride);
            ref var v = ref output[i];
            v = default;
            if (tex != 0)
            {
                switch (ElementSize[tex])
                {
                    case 4:
                        v.U = BinaryPrimitives.ReadSingleLittleEndian(p[tOff..]);
                        v.V = BinaryPrimitives.ReadSingleLittleEndian(p[(tOff + 4)..]);
                        break;
                    case 2: // unsigned 16-bit: 32768 = 1.0
                        int a = BinaryPrimitives.ReadUInt16LittleEndian(p[tOff..]);
                        int b = BinaryPrimitives.ReadUInt16LittleEndian(p[(tOff + 2)..]);
                        v.U = through ? a : a / 32768.0f;
                        v.V = through ? b : b / 32768.0f;
                        break;
                    default: // unsigned 8-bit: 128 = 1.0
                        v.U = through ? p[tOff] : p[tOff] / 128.0f;
                        v.V = through ? p[tOff + 1] : p[tOff + 1] / 128.0f;
                        break;
                }
            }
            if (col != 0)
            {
                v.HasColor = true;
                v.Color = col == 7
                    ? BinaryPrimitives.ReadUInt32LittleEndian(p[cOff..])
                    : Expand(col - 4, BinaryPrimitives.ReadUInt16LittleEndian(p[cOff..]));
            }
            if (pos != 0)
            {
                v.X = ReadComponent(p, pOff, ElementSize[pos], 0, through);
                v.Y = ReadComponent(p, pOff, ElementSize[pos], 1, through);
                v.Z = ReadComponent(p, pOff, ElementSize[pos], 2, through);
            }
        }
        return output;
    }

    private static float ReadComponent(ReadOnlySpan<byte> p, int offset, int size, int k, bool through)
    {
        var q = p[(offset + size * k)..];
        return size switch
        {
            4 => BinaryPrimitives.ReadSingleLittleEndian(q),
            2 => through ? BinaryPrimitives.ReadInt16LittleEndian(q) : BinaryPrimitives.ReadInt16LittleEndian(q) / 32768.0f,
            _ => through ? (sbyte)q[0] : (sbyte)q[0] / 128.0f,
        };
    }

    private static int Align(int off, int a) => (off + a - 1) & ~(a - 1);

    // 16-bit formats -> 0xAABBGGRR
    private static uint Expand(int psm, uint v) => psm switch
    {
        GU_PSM_5650 => (v & 31) * 255 / 31 | ((v >> 5 & 63) * 255 / 63) << 8 | ((v >> 11 & 31) * 255 / 31) << 16 | 0xff000000u,
        GU_PSM_5551 => (v & 31) * 255 / 31 | ((v >> 5 & 31) * 255 / 31) << 8 | ((v >> 10 & 31) * 255 / 31) << 16 | (v >> 15 != 0 ? 0xff000000u : 0),
        GU_PSM_4444 => (v & 15) * 17 | ((v >> 4 & 15) * 17) << 8 | ((v >> 8 & 15) * 17) << 16 | ((v >> 12 & 15) * 17) << 24,
        _ => v,
    };

    private static uint PackColor(float r, float g, float b, float a)
    {
        static uint C(float c) => (uint)Math.Clamp((int)MathF.Round(c), 0, 255);
        return C(r) | C(g) << 8 | C(b) << 16 | C(a) << 24;
    }

    private static void Bernstein(float t, Span<float> b)
    {
        var s = 1 - t;
        b[0] = s * s * s;
        b[1] = 3 * t * s * s;
        b[2] = 3 * t * t * s;
        b[3] = t * t * t;
    }

    private struct DecodedVertex
    {
        public float U, V;
        public uint Color;
        public bool HasColor;
        public float X, Y, Z;
    }

    /// <summary>4x4 float matrix, column-major (GL order), as in the host.</summary>
    private struct Mat4
    {
        public float[] M;

        public static Mat4 Identity
        {
            get
            {
                var r = new Mat4 { M = new float[16] };
                r.M[0] = r.M[5] = r.M[10] = r.M[15] = 1;
                return r;
            }
        }

        public static Mat4 Perspective(float tanHalf, float aspect, float n, float f)
        {
            var r = new Mat4 { M = new float[16] };
            var ct = 1.0f / tanHalf;
            r.M[0] = ct / aspect;
            r.M[5] = ct;
            r.M[10] = (f + n) / (n - f);
            r.M[11] = -1;
            r.M[14] = 2 * f * n / (n - f);
            return r;
        }

        public static Mat4 operator *(Mat4 a, Mat4 b)
        {
            var r = new Mat4 { M = new float[16] };
            for (var c = 0; c < 4; c++)
            for (var rr = 0; rr < 4; rr++)
            {
                float s = 0;
                for (var k = 0; k < 4; k++) s += a.M[k * 4 + rr] * b.M[c * 4 + k];
                r.M[c * 4 + rr] = s;
            }
            return r;
        }
    }
}
